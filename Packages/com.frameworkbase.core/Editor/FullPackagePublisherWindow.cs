using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Editor.ExcelTool;
using Framework.Core;
using Framework.Editor.Release;
using Framework.HotUpdate;
using SQLite;
using UnityEditor;
using UnityEditor.AddressableAssets;
using UnityEditor.Build.Reporting;
using UnityEngine;

namespace Framework.Editor
{
    /// <summary>
    /// 整包发布辅助面板：串联 FullPackage Profile、RefData 首包导出、StreamingAssets 同步。
    /// </summary>
    public class FullPackagePublisherWindow : EditorWindow
    {
        internal const string AutoSwitchBackAfterBuildPrefsKey = "ClientBase.FullPackage.AutoSwitchBackAfterBuild";
        private const string BuildOutputPathPrefsKey = "ClientBase.FullPackage.BuildOutputPath";

        /// <summary>
        /// 整包一键流程状态机。
        /// 用于日志跟踪、失败定位以及发布步骤可视化。
        /// </summary>
        private enum PipelineState
        {
            /// <summary>空闲状态，未开始执行流程。</summary>
            Idle,
            /// <summary>执行发布前校验（路径、输入、产物前置条件）。</summary>
            Validating,
            /// <summary>写入整包 version.json 与 PlayerSettings 版本号。</summary>
            WritingVersion,
            /// <summary>准备 FullPackageLocal 并构建 Addressables。</summary>
            PreparingAddressables,
            /// <summary>批量导出 RefData 到首包数据库。</summary>
            ExportingRefData,
            /// <summary>同步热更程序集和 version.json 到 StreamingAssets。</summary>
            SyncingStreamingAssets,
            /// <summary>执行 BuildPlayer 生成整包。</summary>
            BuildingPlayer,
            /// <summary>流程成功完成。</summary>
            Completed,
            /// <summary>流程失败中断（将尝试自动回滚 Profile）。</summary>
            Failed
        }

        [Serializable]
        private class BuildArtifactSnapshot
        {
            public string Path;
            public bool Exists;
            public long Size;
            public string Md5;
        }

        [Serializable]
        private class FullPackageBuildReportSnapshot
        {
            public string GeneratedAtUtc;
            public bool Success;
            public string Error;
            public string ProfileBefore;
            public string ProfileAfter;
            public string AppVersion;
            public string MinCompatibleVersion;
            public string BuildOutputPath;
            public string BuildTarget;
            public bool AutoSwitchBackAfterBuild;
            public string TelemetryEndReason;
            public bool TelemetrySuccess;
            public List<string> ValidationResults = new List<string>();
            public List<BuildArtifactSnapshot> Artifacts = new List<BuildArtifactSnapshot>();
        }

        private static string ServerDataDir =>
            Path.Combine(Directory.GetParent(Application.dataPath).FullName, "ServerData", "Updates");

        private string _excelFolder = "Assets/RefData_Excel";
        private string _streamingDbPath = "Assets/StreamingAssets/RefData/config.db";
        private bool _overwriteTables = true;
        private bool _enableValidation = true;
        private bool _autoSwitchBackAfterBuild = true;
        private string _appVersion = "2.0";
        private string _minCompatibleVersion = "2.0";
        // 整包更新跳转链接（应用商店/安装包地址），写入整包 version.json 的 UpdateUrl，供旧包强更弹窗跳转。
        private string _fullPackageUpdateUrl = "";
        private bool _writeFullUpdateVersion = true;
        private bool _requireHealthyLaunchTelemetry = true;
        private string _buildOutputPath = "";
        private PipelineState _state = PipelineState.Idle;
        // 是否有流水线动作正在执行（含已登记待执行）。用于防止 OnGUI 期间连点重复触发构建。
        private bool _isRunning;
        // 最近一次流水线的上下文（报告数据来源：Profile 现场/遥测结论/校验结果由步骤回写）。
        private FullPackageReleaseContext _lastRunContext;
        private Vector2 _scroll;
        private string _log = string.Empty;

        [MenuItem("Framework/Full Package Publisher")]
        public static void Open()
        {
            var window = GetWindow<FullPackagePublisherWindow>("整包发布");
            window.minSize = new Vector2(620, 500);
            window.LoadVersionSnapshot();
            window.Show();
        }

        private void OnGUI()
        {
            EditorGUILayout.Space(8);
            GUILayout.Label("整包发布面板", EditorStyles.boldLabel);
            EditorGUILayout.HelpBox(
                "将整包流程串起来：\n" +
                "1) FullPackageLocal（Setup + Switch + Build Addressables）\n" +
                "2) 按 Excel 文件夹批量导出首包 RefData/config.db\n" +
                "3) 同步热更程序集 + HybridCLR AOT -> StreamingAssets\n" +
                "4) 打开 Build Settings\n" +
                "5) 切回 HotUpdateRemote（默认可自动）", MessageType.Info);

            EditorGUILayout.Space(6);
            DrawConfig();
            EditorGUILayout.Space(8);
            DrawActions();
            EditorGUILayout.Space(8);
            DrawLog();
        }

        /// <summary>绘制发布环境下拉（dev/qa/staging/prod），选择结果存 EditorPrefs（机器级）。</summary>
        private void DrawReleaseEnvSelector()
        {
            EditorGUILayout.LabelField("发布环境", EditorStyles.boldLabel);
            EditorGUILayout.BeginVertical("box");

            string[] envs = ReleaseProfileStore.KnownEnvironments;
            int current = Mathf.Max(0, Array.IndexOf(envs, ReleaseProfileStore.ActiveEnv));
            int selected = EditorGUILayout.Popup("目标环境", current, envs);
            if (selected != current)
                ReleaseProfileStore.ActiveEnv = envs[selected];

            var profile = ReleaseProfileStore.TryLoad(ReleaseProfileStore.ActiveEnv, out string loadError);
            if (profile == null)
            {
                EditorGUILayout.HelpBox($"未加载到 {ReleaseProfileStore.ActiveEnv}.json：{loadError}", MessageType.Error);
            }
            else if (profile.RequireManifestSignature && !UpdateManifestSigner.HasUsablePrivateKey)
            {
                EditorGUILayout.HelpBox(
                    $"环境 {profile.Name} 要求签名，但本机未配置可用私钥——发布将被阻断。\n" +
                    "菜单 Framework → Hot Update Security → Generate Signing Key Pair / Set Private Key Path。",
                    MessageType.Warning);
            }

            EditorGUILayout.EndVertical();
        }

        private void DrawConfig()
        {
            DrawReleaseEnvSelector();
            EditorGUILayout.Space(6);

            EditorGUILayout.LabelField("配置", EditorStyles.boldLabel);
            EditorGUILayout.BeginVertical("box");

            _appVersion = EditorGUILayout.TextField("AppVersion", _appVersion);
            _minCompatibleVersion = EditorGUILayout.TextField("MinCompatible", _minCompatibleVersion);
            _fullPackageUpdateUrl = EditorGUILayout.TextField(
                new GUIContent("整包下载链接", "应用商店/安装包地址，写入 version.json 的 UpdateUrl，旧包强更弹窗据此跳转；可空"),
                _fullPackageUpdateUrl);
            _writeFullUpdateVersion = EditorGUILayout.Toggle("写入整包 version.json", _writeFullUpdateVersion);
            _requireHealthyLaunchTelemetry = EditorGUILayout.Toggle("要求最近启动埋点通过", _requireHealthyLaunchTelemetry);

            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField("Excel 源目录", GUILayout.Width(90));
            _excelFolder = EditorGUILayout.TextField(_excelFolder);
            if (GUILayout.Button("浏览", GUILayout.Width(60)))
            {
                var selected = EditorUtility.OpenFolderPanel("选择 Excel 文件夹", Application.dataPath, "");
                if (!string.IsNullOrEmpty(selected))
                {
                    var relative = FileUtil.GetProjectRelativePath(selected);
                    _excelFolder = string.IsNullOrEmpty(relative) ? selected : relative;
                }
            }
            EditorGUILayout.EndHorizontal();
            EditorGUILayout.HelpBox("用于第 2 步：批量扫描该目录下所有 .xlsx，导出到首包 config.db。", MessageType.None);

            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField("首包数据库", GUILayout.Width(90));
            _streamingDbPath = EditorGUILayout.TextField(_streamingDbPath);
            if (GUILayout.Button("浏览", GUILayout.Width(60)))
            {
                var selected = EditorUtility.SaveFilePanel("选择首包数据库", Application.dataPath, "config", "db");
                if (!string.IsNullOrEmpty(selected))
                {
                    var relative = FileUtil.GetProjectRelativePath(selected);
                    _streamingDbPath = string.IsNullOrEmpty(relative) ? selected : relative;
                }
            }
            EditorGUILayout.EndHorizontal();

            _overwriteTables = EditorGUILayout.Toggle("覆盖已存在的表", _overwriteTables);
            _enableValidation = EditorGUILayout.Toggle("启用数据校验", _enableValidation);
            _autoSwitchBackAfterBuild = EditorGUILayout.Toggle("Build成功后自动切回HotUpdateRemote", _autoSwitchBackAfterBuild);
            EditorGUILayout.HelpBox(
                "通常建议开启。仅在你需要连续打多个 FullPackageLocal 包、且不希望每次 Build 后自动回切时关闭。",
                MessageType.None);
            EditorGUILayout.HelpBox(
                "“要求最近启动埋点通过”开启时：若 launch_metrics_last.json 不存在或最近一次启动失败，将阻断一键整包流程。",
                MessageType.None);

            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField("Build 输出路径", GUILayout.Width(90));
            _buildOutputPath = EditorGUILayout.TextField(_buildOutputPath);
            if (GUILayout.Button("浏览", GUILayout.Width(60)))
            {
                _buildOutputPath = PickBuildOutputPath();
                SavePrefs();
            }
            EditorGUILayout.EndHorizontal();

            EditorGUILayout.EndVertical();
        }

        private void DrawActions()
        {
            EditorGUILayout.LabelField("操作", EditorStyles.boldLabel);
            EditorGUILayout.BeginVertical("box");

            if (GUILayout.Button("0) 写入整包版本号（version.json + PlayerSettings）", GUILayout.Height(30)))
                Dispatch(() => RunSingle(
                    new HotUpdateReleaseSteps.ValidateReleaseEnvironment(),
                    new FullPackageReleaseSteps.WriteFullPackageVersion(_writeFullUpdateVersion)));

            if (GUILayout.Button("1) 准备整包资源（Setup + Switch + Build）", GUILayout.Height(30)))
                Dispatch(() => RunSingle(new FullPackageReleaseSteps.PrepareFullPackageAddressables()));

            if (GUILayout.Button("2) 导出首包 RefData（Batch + StreamingAssetsOnly）", GUILayout.Height(30)))
                Dispatch(() => RunSingle(new FullPackageReleaseSteps.ExportRefData()));

            if (GUILayout.Button("3) 同步热更程序集 + HybridCLR AOT -> StreamingAssets", GUILayout.Height(30)))
                Dispatch(() => RunSingle(new FullPackageReleaseSteps.SyncStreamingAssetsAndVerify()));

            if (GUILayout.Button("4) 打开 Build Settings", GUILayout.Height(30)))
                Dispatch(() => OpenBuildSettingsWindow(_autoSwitchBackAfterBuild));

            if (GUILayout.Button("5) 切回 HotUpdateRemote", GUILayout.Height(30)))
                Dispatch(() => RunSingle(new FullPackageReleaseSteps.SwitchBackHotUpdateRemote()));

            EditorGUILayout.Space(4);
            if (GUILayout.Button("一键执行 0->4（自动打开 Build Settings）", GUILayout.Height(36)))
                Dispatch(RunAll);

            if (GUILayout.Button("一键整包构建（全量资源 + RefData + HotUpdate + version）", GUILayout.Height(42)))
                Dispatch(RunBuildFullPackageOneClick);

            EditorGUILayout.EndVertical();
        }

        private void DrawLog()
        {
            EditorGUILayout.LabelField("执行日志", EditorStyles.boldLabel);
            EditorGUILayout.LabelField($"当前状态: {_state}");
            _scroll = EditorGUILayout.BeginScrollView(_scroll, GUILayout.Height(140));
            EditorGUILayout.SelectableLabel(_log, EditorStyles.miniLabel, GUILayout.ExpandHeight(true));
            EditorGUILayout.EndScrollView();
        }

        /// <summary>
        /// 用当前窗口参数组装整包流水线上下文。
        /// 流程逻辑全部在 FullPackageReleaseSteps；窗口只负责收集参数、编排步骤与展示结果。
        /// </summary>
        private FullPackageReleaseContext BuildContext()
        {
            return new FullPackageReleaseContext
            {
                AppVersion = _appVersion,
                MinCompatibleVersion = _minCompatibleVersion,
                UpdateUrl = _fullPackageUpdateUrl ?? string.Empty,
                ForceUpdate = true,
                ServerDataDir = ServerDataDir,
                ExcelFolder = _excelFolder,
                StreamingDbPath = _streamingDbPath,
                OverwriteTables = _overwriteTables,
                EnableValidation = _enableValidation,
                RequireHealthyTelemetry = _requireHealthyLaunchTelemetry,
                BuildOutputPath = _buildOutputPath,
                Log = AppendLog
            };
        }

        /// <summary>单步按钮共用：跑一个只含指定步骤的迷你流水线，失败弹窗提示。</summary>
        private void RunSingle(params IReleaseStep[] steps)
        {
            var ctx = BuildContext();
            _lastRunContext = ctx;
            var result = ReleasePipeline.Run(steps, ctx);
            if (!result.Success)
                EditorUtility.DisplayDialog("执行失败", $"{result.FailedStep}：{result.Error}", "OK");
        }

        /// <summary>
        /// 整包公共步骤序列（不含 BuildPlayer）：环境门禁 → 输入校验 → 写版本 →
        /// 整包资源（可补偿：失败自动切回原 Profile）→ RefData 导出 → StreamingAssets 同步与校验。
        /// </summary>
        private List<IReleaseStep> BuildCommonSteps(bool requireBuildOutputPath)
        {
            return new List<IReleaseStep>
            {
                new HotUpdateReleaseSteps.ValidateReleaseEnvironment(),
                new FullPackageReleaseSteps.ValidateFullPackageInputs(requireBuildOutputPath),
                new FullPackageReleaseSteps.WriteFullPackageVersion(_writeFullUpdateVersion),
                new FullPackageReleaseSteps.PrepareFullPackageAddressables(),
                new FullPackageReleaseSteps.ExportRefData(),
                new FullPackageReleaseSteps.SyncStreamingAssetsAndVerify()
            };
        }

        /// <summary>一键执行 0→4：跑公共步骤后打开 Build Settings，由人工执行 Build。</summary>
        private void RunAll()
        {
            _state = PipelineState.Validating;
            var ctx = BuildContext();
            _lastRunContext = ctx;

            var result = ReleasePipeline.Run(BuildCommonSteps(requireBuildOutputPath: false), ctx);

            if (!result.Success)
            {
                _state = PipelineState.Failed;
                WriteFullPackageBuildReport(ctx, success: false, errorMessage: result.Error);
                EditorUtility.DisplayDialog("整包流程中断", $"{result.FailedStep}：{result.Error}", "OK");
                return;
            }

            _state = PipelineState.Completed;
            OpenBuildSettingsWindow(_autoSwitchBackAfterBuild);
            EditorUtility.DisplayDialog("整包流程完成",
                _autoSwitchBackAfterBuild
                    ? "1~4 步骤已执行完成，请立即在 Build Settings 执行 Build。\n\nBuild 成功后将自动切回 HotUpdateRemote。"
                    : "1~4 步骤已执行完成，请立即在 Build Settings 执行 Build。\n\n⚠ Build 完成后，再回到面板手动点「5) 切回 HotUpdateRemote」。",
                "OK");
        }

        /// <summary>一键整包构建：公共步骤 + BuildPlayer +（按开关）切回 HotUpdateRemote，最后落盘构建报告。</summary>
        private void RunBuildFullPackageOneClick()
        {
            EnsureBuildOutputPath();
            SavePrefs();

            _state = PipelineState.Validating;
            var ctx = BuildContext();
            _lastRunContext = ctx;

            var steps = BuildCommonSteps(requireBuildOutputPath: true);
            steps.Add(new FullPackageReleaseSteps.BuildPlayer());
            if (_autoSwitchBackAfterBuild)
                steps.Add(new FullPackageReleaseSteps.SwitchBackHotUpdateRemote());

            var result = ReleasePipeline.Run(steps, ctx);

            if (!result.Success)
            {
                // 失败路径的 Profile 恢复由 PrepareFullPackageAddressables 的补偿逆序完成。
                _state = PipelineState.Failed;
                WriteFullPackageBuildReport(ctx, success: false, errorMessage: result.Error);
                EditorUtility.DisplayDialog("整包构建失败", $"{result.FailedStep}：{result.Error}", "OK");
                return;
            }

            _state = PipelineState.Completed;
            WriteFullPackageBuildReport(ctx, success: true);
            EditorUtility.DisplayDialog("整包构建完成",
                "整包构建已完成：\n" +
                "1) 全量资源（FullPackageLocal）\n" +
                "2) StreamingAssets/RefData/config.db\n" +
                "3) StreamingAssets/热更程序集 *.dll.bytes\n" +
                "4) StreamingAssets/HybridCLRMetadata/*.dll.bytes\n" +
                "5) version.json\n" +
                (_autoSwitchBackAfterBuild ? "6) 已自动切回 HotUpdateRemote" : "6) 请手动切回 HotUpdateRemote"),
                "OK");
        }

        private void AppendLog(string message)
        {
            _log += $"[{DateTime.Now:HH:mm:ss}] {message}\n";
            Repaint();
        }

        private void LoadVersionSnapshot()
        {
            string path = Path.Combine(ServerDataDir, "version.json");
            if (!File.Exists(path)) return;

            try
            {
                var snapshot = JsonUtility.FromJson<UpdateInfo>(File.ReadAllText(path));
                if (snapshot == null) return;
                _appVersion = string.IsNullOrEmpty(snapshot.AppVersion) ? _appVersion : snapshot.AppVersion;
                _minCompatibleVersion = string.IsNullOrEmpty(snapshot.MinCompatibleVersion) ? _minCompatibleVersion : snapshot.MinCompatibleVersion;
                _fullPackageUpdateUrl = string.IsNullOrEmpty(snapshot.UpdateUrl) ? _fullPackageUpdateUrl : snapshot.UpdateUrl;
            }
            catch
            {
                // ignore invalid json and keep defaults
            }
        }

        private void OnEnable()
        {
            _buildOutputPath = EditorPrefs.GetString(BuildOutputPathPrefsKey, _buildOutputPath);
        }

        private static void OpenBuildSettingsWindow(bool autoSwitchBackAfterBuild)
        {
            if (autoSwitchBackAfterBuild)
                EditorPrefs.SetBool(AutoSwitchBackAfterBuildPrefsKey, true);

            // 用菜单命令 + 延迟调用，避免在 IMGUI Layout 过程中切窗导致布局栈报错。
            EditorApplication.delayCall += () =>
            {
                EditorApplication.ExecuteMenuItem("File/Build Settings...");
            };
        }

        private static string PickBuildOutputPath()
        {
            BuildTarget target = EditorUserBuildSettings.activeBuildTarget;
            string productName = string.IsNullOrEmpty(PlayerSettings.productName) ? "ClientBase" : PlayerSettings.productName;

            if (target == BuildTarget.StandaloneWindows || target == BuildTarget.StandaloneWindows64)
            {
                return EditorUtility.SaveFilePanel("选择整包输出路径", "", productName, "exe");
            }

            if (target == BuildTarget.Android)
            {
                return EditorUtility.SaveFilePanel("选择整包输出路径", "", productName, "apk");
            }

            // 其他平台默认选择目录
            return EditorUtility.OpenFolderPanel("选择整包输出目录", "", "");
        }

        private string EnsureBuildOutputPath()
        {
            if (!string.IsNullOrEmpty(_buildOutputPath))
                return _buildOutputPath;

            string selected = PickBuildOutputPath();
            if (string.IsNullOrEmpty(selected))
                throw new Exception("未选择 Build 输出路径。");

            _buildOutputPath = selected;
            return _buildOutputPath;
        }

        private void SavePrefs()
        {
            EditorPrefs.SetString(BuildOutputPathPrefsKey, _buildOutputPath ?? string.Empty);
        }

        /// <summary>
        /// 登记一个流水线动作，延后到 OnGUI（IMGUI 事件）之外的编辑器 tick 再执行。
        /// 必须如此：在 OnGUI 内直接调用 AddressableAssetSettings.BuildPlayerContent / BuildPipeline.BuildPlayer，
        /// Unity 会判定“当前不允许构建”（SBP 的 CanBuildPlayer 返回 false），抛
        /// “Unable to build with the current configuration”。延后到 delayCall 即可在 GUI 循环外正常构建。
        /// 同时用 _isRunning 防止连点重复触发。
        /// </summary>
        private void Dispatch(Action action)
        {
            if (_isRunning)
                return;
            _isRunning = true;
            EditorApplication.delayCall += () =>
            {
                try { action(); }
                finally { _isRunning = false; }
            };
        }

        private void WriteFullPackageBuildReport(FullPackageReleaseContext ctx, bool success, string errorMessage = null)
        {
            var report = new FullPackageBuildReportSnapshot
            {
                GeneratedAtUtc = DateTime.UtcNow.ToString("O"),
                Success = success,
                Error = errorMessage ?? string.Empty,
                ProfileBefore = ctx.ProfileBefore,
                ProfileAfter = FullPackageReleaseSteps.PrepareFullPackageAddressables.GetActiveProfileName(),
                AppVersion = _appVersion,
                MinCompatibleVersion = _minCompatibleVersion,
                BuildOutputPath = _buildOutputPath,
                BuildTarget = EditorUserBuildSettings.activeBuildTarget.ToString(),
                AutoSwitchBackAfterBuild = _autoSwitchBackAfterBuild,
                TelemetryEndReason = ctx.TelemetryEndReason,
                TelemetrySuccess = ctx.TelemetrySuccess,
                ValidationResults = new List<string>(ctx.ValidationResults)
            };

            report.Artifacts.Add(BuildArtifactSnapshotOf(FullPackageReleaseSteps.ToAbsoluteProjectPath(_streamingDbPath)));
            foreach (string bytesFileName in VersionManager.HotUpdateAssemblyFileNames)
                report.Artifacts.Add(BuildArtifactSnapshotOf(Path.Combine(Application.dataPath, "StreamingAssets", bytesFileName)));
            report.Artifacts.Add(BuildArtifactSnapshotOf(Path.Combine(Application.dataPath, "StreamingAssets", "version.json")));
            report.Artifacts.Add(BuildArtifactSnapshotOf(Path.Combine(ServerDataDir, "version.json")));
            if (!string.IsNullOrEmpty(_buildOutputPath))
                report.Artifacts.Add(BuildArtifactSnapshotOf(_buildOutputPath));

            string json = JsonUtility.ToJson(report, true);
            Directory.CreateDirectory(ServerDataDir);
            File.WriteAllText(Path.Combine(ServerDataDir, "full_package_build_report.json"), json, Encoding.UTF8);
            AppendLog("[Report] 已写入 ServerData/Updates/full_package_build_report.json");
        }

        private static BuildArtifactSnapshot BuildArtifactSnapshotOf(string path)
        {
            var snapshot = new BuildArtifactSnapshot
            {
                Path = path,
                Exists = File.Exists(path),
                Size = 0,
                Md5 = string.Empty
            };

            if (!snapshot.Exists)
                return snapshot;

            var info = new FileInfo(path);
            snapshot.Size = info.Length;
            snapshot.Md5 = ComputeFileMd5(path);
            return snapshot;
        }

        private static string ComputeFileMd5(string path)
        {
            using (var stream = File.OpenRead(path))
            using (var md5 = MD5.Create())
            {
                byte[] hash = md5.ComputeHash(stream);
                var sb = new StringBuilder(hash.Length * 2);
                foreach (byte b in hash)
                    sb.Append(b.ToString("x2"));
                return sb.ToString();
            }
        }

    }
}
