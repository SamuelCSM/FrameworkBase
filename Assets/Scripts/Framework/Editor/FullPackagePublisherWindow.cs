using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Editor.ExcelTool;
using Framework.Core;
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
        private class VersionSnapshot
        {
            public string AppVersion = "1.0";
            public int ResourceVersion = 1;
            public int CodeVersion = 1;
            public bool ForceUpdate = true;
            public string MinCompatibleVersion = "1.0";
            public string Description = "";
        }

        [Serializable]
        private class LaunchPhaseMetricSnapshot
        {
            // 字段由 JsonUtility.FromJson 反射赋值，编译器无法静态识别，故局部关闭 CS0649（字段从未赋值）误报。
#pragma warning disable CS0649
            public string Phase;
            public string DisplayName;
            public bool Success;
            public long DurationMs;
            public string Detail;
            public string Error;
            public long StartTicksUtc;
#pragma warning restore CS0649
        }

        [Serializable]
        private class LaunchRunMetricSnapshot
        {
            // 字段由 JsonUtility.FromJson 反射赋值，编译器无法静态识别，故局部关闭 CS0649（字段从未赋值）误报。
#pragma warning disable CS0649
            public string RunId;
            public string StartedAtUtc;
            public bool Success;
            public string EndReason;
#pragma warning restore CS0649
            public List<LaunchPhaseMetricSnapshot> Phases = new List<LaunchPhaseMetricSnapshot>();
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
        private static string LaunchMetricPath =>
            Path.Combine(Application.persistentDataPath, "launch_metrics_last.json");

        private string _excelFolder = "Assets/RefData_Excel";
        private string _streamingDbPath = "Assets/StreamingAssets/RefData/config.db";
        private bool _overwriteTables = true;
        private bool _enableValidation = true;
        private bool _autoSwitchBackAfterBuild = true;
        private string _appVersion = "2.0";
        private string _minCompatibleVersion = "2.0";
        private bool _writeFullUpdateVersion = true;
        private bool _requireHealthyLaunchTelemetry = true;
        private string _buildOutputPath = "";
        private PipelineState _state = PipelineState.Idle;
        // 是否有流水线动作正在执行（含已登记待执行）。用于防止 OnGUI 期间连点重复触发构建。
        private bool _isRunning;
        private string _profileBeforeRun = string.Empty;
        private readonly List<string> _validationResults = new List<string>();
        private string _lastTelemetryEndReason = string.Empty;
        private bool _lastTelemetrySuccess = false;
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

        private void DrawConfig()
        {
            EditorGUILayout.LabelField("配置", EditorStyles.boldLabel);
            EditorGUILayout.BeginVertical("box");

            _appVersion = EditorGUILayout.TextField("AppVersion", _appVersion);
            _minCompatibleVersion = EditorGUILayout.TextField("MinCompatible", _minCompatibleVersion);
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
                Dispatch(RunWriteFullUpdateVersion);

            if (GUILayout.Button("1) 准备整包资源（Setup + Switch + Build）", GUILayout.Height(30)))
                Dispatch(RunPrepareFullPackage);

            if (GUILayout.Button("2) 导出首包 RefData（Batch + StreamingAssetsOnly）", GUILayout.Height(30)))
                Dispatch(RunExportRefData);

            if (GUILayout.Button("3) 同步热更程序集 + HybridCLR AOT -> StreamingAssets", GUILayout.Height(30)))
                Dispatch(RunSyncHotUpdate);

            if (GUILayout.Button("4) 打开 Build Settings", GUILayout.Height(30)))
                Dispatch(() => OpenBuildSettingsWindow(_autoSwitchBackAfterBuild));

            if (GUILayout.Button("5) 切回 HotUpdateRemote", GUILayout.Height(30)))
                Dispatch(RunSwitchBackRemote);

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

        private void RunPrepareFullPackage()
        {
            AddressablesSetup.PrepareFullPackage();
            AppendLog("完成：Prepare Full Package");
        }

        private void RunWriteFullUpdateVersion()
        {
            if (string.IsNullOrEmpty(_appVersion))
                throw new Exception("AppVersion 不能为空");

            PlayerSettings.bundleVersion = _appVersion;
            AppendLog($"完成：PlayerSettings.bundleVersion = {_appVersion}");

            if (!_writeFullUpdateVersion)
            {
                AppendLog("跳过：未写入 version.json（按当前开关设置）");
                return;
            }

            var snapshot = new VersionSnapshot
            {
                AppVersion = _appVersion,
                ResourceVersion = 1,
                CodeVersion = 1,
                ForceUpdate = true,
                MinCompatibleVersion = string.IsNullOrEmpty(_minCompatibleVersion) ? _appVersion : _minCompatibleVersion,
                Description = ""
            };

            string json = BuildVersionJson(snapshot);

            Directory.CreateDirectory(ServerDataDir);
            File.WriteAllText(Path.Combine(ServerDataDir, "version.json"), json, Encoding.UTF8);

            string streamingDir = Path.Combine(Application.dataPath, "StreamingAssets");
            Directory.CreateDirectory(streamingDir);
            File.WriteAllText(Path.Combine(streamingDir, "version.json"), json, Encoding.UTF8);

            AssetDatabase.Refresh();
            AppendLog($"完成：整包 version.json 已写入（App={snapshot.AppVersion}, ForceUpdate=true）");
        }

        private static readonly string[] BootstrapRequiredTables = { "language", "loading_tips" };

        private void RunExportRefData()
        {
            if (string.IsNullOrEmpty(_excelFolder) || !Directory.Exists(_excelFolder))
                throw new Exception($"Excel 文件夹无效：{_excelFolder}");

            var excelFiles = Directory.GetFiles(_excelFolder, "*.xlsx", SearchOption.AllDirectories)
                .Where(f => !Path.GetFileName(f).StartsWith("~$", StringComparison.Ordinal))
                .ToList();

            if (excelFiles.Count == 0)
                throw new Exception($"目录中未找到 xlsx：{_excelFolder}");

            var config = new ExcelExporter.ExportConfig
            {
                OutputDbPath = _streamingDbPath,
                AddressableBytesOutputPath = "Assets/ResourcesOut/RefData/config.db.bytes",
                OutputTarget = ExcelExporter.DatabaseOutputTarget.StreamingAssetsOnly,
                OverwriteExistingTables = _overwriteTables,
                PruneMissingTablesOnBatch = true,
                EnableValidation = _enableValidation,
                VerboseLogging = false
            };

            var exporter = new ExcelExporter(config);
            var results = exporter.ExportBatch(excelFiles);

            int success = results.Count(r => r.Success);
            int fail = results.Count(r => !r.Success);
            if (fail > 0)
            {
                string errors = string.Join("\n\n", results.Where(r => !r.Success).Select(r => $"{r.TableName}: {r.ErrorMessage}"));
                throw new Exception($"RefData 导出失败 {fail} 项：\n{errors}");
            }

            var exportedTables = results.Where(r => r.Success).Select(r => r.TableName).ToList();
            AppendLog($"批量导出 {success} 张表：{string.Join(", ", exportedTables)}");

            // 批量模式偶发只导出首包配表的第一个工作表时，按表名补导 language / loading_tips
            EnsureBootstrapConfigTables(exporter, excelFiles);

            string dbPath = ToAbsoluteProjectPath(_streamingDbPath);
            ValidateRequiredConfigTables(dbPath);

            AppendLog($"完成：RefData 已写入 {_streamingDbPath}");
        }

        /// <summary>
        /// 首包启动依赖表缺失时，从「首包配表」工作簿按表名显式补导（避免仅导出第一个 sheet）。
        /// </summary>
        private void EnsureBootstrapConfigTables(ExcelExporter exporter, List<string> excelFiles)
        {
            string dbPath = ToAbsoluteProjectPath(_streamingDbPath);
            var missing = GetMissingRequiredTables(dbPath, BootstrapRequiredTables);
            if (missing.Count == 0)
                return;

            string bootstrapPath = excelFiles.FirstOrDefault(IsBootstrapWorkbookPath);
            if (string.IsNullOrEmpty(bootstrapPath))
            {
                throw new Exception(
                    $"首包 config.db 缺少表：{string.Join(", ", missing)}。\n" +
                    $"请在 Excel 目录放置含 language、loading_tips 工作表的「首包配表.xlsx」。");
            }

            AppendLog(
                $"缺少首包表 {string.Join(", ", missing)}，从 {Path.GetFileName(bootstrapPath)} 按工作表名补导…");

            foreach (string table in missing)
            {
                var result = exporter.ExportExcel(bootstrapPath, table);
                if (!result.Success)
                    throw new Exception($"补导首包表「{table}」失败：{result.ErrorMessage}");

                AppendLog($"补导成功：{table}（{result.RowCount} 行）");
            }
        }

        private static bool IsBootstrapWorkbookPath(string path)
        {
            string name = Path.GetFileNameWithoutExtension(path) ?? string.Empty;
            return name.Contains("首包", StringComparison.Ordinal) && name.Contains("配表", StringComparison.Ordinal);
        }

        private static List<string> GetMissingRequiredTables(string dbPath, string[] requiredTables)
        {
            var missing = new List<string>();
            using (var db = new SQLiteConnection(dbPath))
            {
                foreach (string table in requiredTables)
                {
                    int count = db.ExecuteScalar<int>(
                        "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name=?",
                        table);
                    if (count == 0)
                        missing.Add(table);
                }
            }

            return missing;
        }

        private void RunSyncHotUpdate()
        {
            HotUpdatePublisher.SyncToStreamingAssetsForBuild(showDialog: false);
            AppendLog("完成：热更程序集 / HybridCLR AOT / version.json 已同步到 StreamingAssets");
        }

        private void RunSwitchBackRemote()
        {
            AddressablesSetup.SwitchToHotUpdateRemote();
            AppendLog("完成：已切回 HotUpdateRemote");
        }

        private void RunAll()
        {
            BeginPipeline();
            try
            {
                SetState(PipelineState.Validating);
                ValidatePipelineInputs(requireBuildOutputPath: false);

                SetState(PipelineState.WritingVersion);
                RunWriteFullUpdateVersion();

                SetState(PipelineState.PreparingAddressables);
                RunPrepareFullPackage();

                SetState(PipelineState.ExportingRefData);
                RunExportRefData();

                SetState(PipelineState.SyncingStreamingAssets);
                RunSyncHotUpdate();
                VerifyRequiredStreamingAssetsOutputs();

                OpenBuildSettingsWindow(_autoSwitchBackAfterBuild);
                SetState(PipelineState.Completed);
                EditorUtility.DisplayDialog("整包流程完成",
                    _autoSwitchBackAfterBuild
                        ? "1~4 步骤已执行完成，请立即在 Build Settings 执行 Build。\n\nBuild 成功后将自动切回 HotUpdateRemote。"
                        : "1~4 步骤已执行完成，请立即在 Build Settings 执行 Build。\n\n⚠ Build 完成后，再回到面板手动点「5) 切回 HotUpdateRemote」。",
                    "OK");
            }
            catch (Exception ex)
            {
                SetState(PipelineState.Failed);
                RollbackProfileIfNeeded();
                AppendLog($"[ERROR] {ex.Message}");
                WriteFullPackageBuildReport(success: false, errorMessage: ex.Message);
                EditorUtility.DisplayDialog("整包流程中断", ex.Message, "OK");
            }
            finally
            {
                EndPipeline();
            }
        }

        private void RunBuildFullPackageOneClick()
        {
            bool shouldRestoreProfile = _autoSwitchBackAfterBuild;
            BeginPipeline();
            try
            {
                SetState(PipelineState.Validating);
                ValidatePipelineInputs(requireBuildOutputPath: true);

                SetState(PipelineState.WritingVersion);
                RunWriteFullUpdateVersion();

                SetState(PipelineState.PreparingAddressables);
                RunPrepareFullPackage();

                SetState(PipelineState.ExportingRefData);
                RunExportRefData();

                SetState(PipelineState.SyncingStreamingAssets);
                RunSyncHotUpdate();
                VerifyRequiredStreamingAssetsOutputs();

                SetState(PipelineState.BuildingPlayer);
                RunBuildPlayer();
                SetState(PipelineState.Completed);

                if (shouldRestoreProfile)
                    RunSwitchBackRemote();

                EditorUtility.DisplayDialog("整包构建完成",
                    "整包构建已完成：\n" +
                    "1) 全量资源（FullPackageLocal）\n" +
                    "2) StreamingAssets/RefData/config.db\n" +
                    "3) StreamingAssets/热更程序集 *.dll.bytes\n" +
                    "4) StreamingAssets/HybridCLRMetadata/*.dll.bytes\n" +
                    "5) version.json\n" +
                    (shouldRestoreProfile ? "6) 已自动切回 HotUpdateRemote" : "6) 请手动切回 HotUpdateRemote"),
                    "OK");
                WriteFullPackageBuildReport(success: true);
            }
            catch (Exception ex)
            {
                SetState(PipelineState.Failed);
                RollbackProfileIfNeeded();
                AppendLog($"[ERROR] {ex.Message}");
                WriteFullPackageBuildReport(success: false, errorMessage: ex.Message);
                EditorUtility.DisplayDialog("整包构建失败", ex.Message, "OK");
            }
            finally
            {
                // 兜底：若开启了自动回切，确保直接 BuildPlayer 路径也能恢复到 HotUpdateRemote。
                if (shouldRestoreProfile)
                {
                    try
                    {
                        AddressablesSetup.SwitchToHotUpdateRemote();
                    }
                    catch (Exception restoreEx)
                    {
                        AppendLog($"[WARN] 自动切回 HotUpdateRemote 失败：{restoreEx.Message}");
                    }
                }

                EndPipeline();
            }
        }

        private void RunBuildPlayer()
        {
            string outputPath = EnsureBuildOutputPath();
            SavePrefs();

            string[] scenes = EditorBuildSettings.scenes
                .Where(s => s.enabled)
                .Select(s => s.path)
                .ToArray();
            if (scenes.Length == 0)
                throw new Exception("Build Settings 中没有启用的场景，无法构建整包。");

            var options = new BuildPlayerOptions
            {
                scenes = scenes,
                locationPathName = outputPath,
                target = EditorUserBuildSettings.activeBuildTarget,
                options = BuildOptions.None
            };

            AppendLog($"开始 BuildPlayer -> {outputPath}");
            BuildReport report = BuildPipeline.BuildPlayer(options);
            if (report.summary.result != BuildResult.Succeeded)
                throw new Exception($"BuildPlayer 失败：{report.summary.result}");

            AppendLog($"完成：BuildPlayer 成功，输出：{outputPath}");
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
                var snapshot = JsonUtility.FromJson<VersionSnapshot>(File.ReadAllText(path));
                if (snapshot == null) return;
                _appVersion = string.IsNullOrEmpty(snapshot.AppVersion) ? _appVersion : snapshot.AppVersion;
                _minCompatibleVersion = string.IsNullOrEmpty(snapshot.MinCompatibleVersion) ? _minCompatibleVersion : snapshot.MinCompatibleVersion;
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

        private static string BuildVersionJson(VersionSnapshot v)
        {
            return
$@"{{
  ""AppVersion"":          ""{v.AppVersion}"",
  ""ResourceVersion"":     {v.ResourceVersion},
  ""CodeVersion"":         {v.CodeVersion},
  ""ForceUpdate"":         {v.ForceUpdate.ToString().ToLowerInvariant()},
  ""MinCompatibleVersion"":""{v.MinCompatibleVersion}"",
  ""Description"":         ""{v.Description}"",
  ""PatchFiles"":          []
}}";
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

        private void ValidatePipelineInputs(bool requireBuildOutputPath)
        {
            if (string.IsNullOrEmpty(_appVersion))
                throw new Exception("AppVersion 不能为空。");

            if (string.IsNullOrEmpty(_excelFolder) || !Directory.Exists(_excelFolder))
                throw new Exception($"Excel 源目录无效：{_excelFolder}");

            if (!HasExcelFiles(_excelFolder))
                throw new Exception($"Excel 源目录下未找到 .xlsx：{_excelFolder}");

            if (string.IsNullOrEmpty(_streamingDbPath))
                throw new Exception("首包数据库路径不能为空。");

            if (!_streamingDbPath.Replace('\\', '/').StartsWith("Assets/StreamingAssets/", StringComparison.OrdinalIgnoreCase))
                throw new Exception("首包数据库路径必须位于 Assets/StreamingAssets 下。");

            if (requireBuildOutputPath && string.IsNullOrEmpty(_buildOutputPath))
                throw new Exception("请先设置 Build 输出路径（用于一键整包构建）。");

            Il2CppToolchainValidator.ValidateForActiveBuildTarget();

            ValidateLaunchTelemetryIfNeeded();
        }

        private static bool HasExcelFiles(string excelFolder)
        {
            return Directory.GetFiles(excelFolder, "*.xlsx", SearchOption.AllDirectories)
                .Any(f => !Path.GetFileName(f).StartsWith("~$", StringComparison.Ordinal));
        }

        private void VerifyRequiredStreamingAssetsOutputs()
        {
            string streamingDir = Path.Combine(Application.dataPath, "StreamingAssets");
            string localDbPath = ToAbsoluteProjectPath(_streamingDbPath);

            string[] mustExist =
            {
                localDbPath,
                Path.Combine(streamingDir, "version.json"),
                Path.Combine(ServerDataDir, "version.json")
            };

            var missing = new List<string>();
            foreach (string file in mustExist)
            {
                if (!File.Exists(file))
                    missing.Add(file);
            }

            foreach (string bytesFileName in VersionManager.HotUpdateAssemblyFileNames)
            {
                string file = Path.Combine(streamingDir, bytesFileName);
                if (!File.Exists(file))
                    missing.Add(file);
            }

            foreach (string file in HybridCLRStreamingAssetsSync.GetRequiredStreamingAssetsMetadataPaths())
            {
                if (!File.Exists(file))
                    missing.Add(file);
            }

            if (missing.Count > 0)
                throw new Exception("关键产物缺失：\n" + string.Join("\n", missing));

            ValidateRequiredConfigTables(localDbPath);
            ValidateUiAddressMappings(localDbPath);
        }

        private static string ToAbsoluteProjectPath(string projectPath)
        {
            if (Path.IsPathRooted(projectPath))
                return projectPath;

            string normalized = projectPath.Replace('\\', '/');
            if (normalized.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase))
                return Path.Combine(Directory.GetParent(Application.dataPath).FullName, normalized);

            return Path.GetFullPath(projectPath);
        }

        /// <summary>
        /// 记录流程起始状态（当前 active profile），便于失败时回滚。
        /// </summary>
        private void BeginPipeline()
        {
            _profileBeforeRun = GetActiveProfileName();
            _validationResults.Clear();
            _lastTelemetryEndReason = string.Empty;
            _lastTelemetrySuccess = false;
            AppendLog($"流程开始，当前Profile={_profileBeforeRun}");
        }

        /// <summary>
        /// 结束流程并清理临时状态。
        /// </summary>
        private void EndPipeline()
        {
            _profileBeforeRun = string.Empty;
            if (_state != PipelineState.Failed && _state != PipelineState.Completed)
                _state = PipelineState.Idle;
        }

        private void SetState(PipelineState state)
        {
            _state = state;
            AppendLog($"状态切换 -> {_state}");
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

        /// <summary>
        /// 流程失败时自动回滚 Addressables Profile，避免发布环境残留在 FullPackageLocal。
        /// </summary>
        private void RollbackProfileIfNeeded()
        {
            if (string.IsNullOrEmpty(_profileBeforeRun))
                return;

            string currentProfile = GetActiveProfileName();
            if (string.Equals(currentProfile, _profileBeforeRun, StringComparison.Ordinal))
                return;

            try
            {
                if (string.Equals(_profileBeforeRun, "HotUpdateRemote", StringComparison.Ordinal))
                    AddressablesSetup.SwitchToHotUpdateRemote();
                else if (string.Equals(_profileBeforeRun, "FullPackageLocal", StringComparison.Ordinal))
                    AddressablesSetup.SwitchToFullPackageLocal();

                AppendLog($"已回滚Profile: {currentProfile} -> {_profileBeforeRun}");
            }
            catch (Exception ex)
            {
                AppendLog($"[WARN] Profile 回滚失败：{ex.Message}");
            }
        }

        /// <summary>
        /// 读取当前 Addressables Active Profile 名称。
        /// </summary>
        private static string GetActiveProfileName()
        {
            var settings = AddressableAssetSettingsDefaultObject.Settings;
            if (settings == null || settings.profileSettings == null)
                return string.Empty;

            string activeId = settings.activeProfileId;
            return settings.profileSettings.GetProfileName(activeId) ?? string.Empty;
        }

        private void ValidateLaunchTelemetryIfNeeded()
        {
            if (!File.Exists(LaunchMetricPath))
            {
                string msg = "[Telemetry] 未找到 launch_metrics_last.json，建议先跑一次启动流程冒烟。";
                AppendLog(msg);
                if (_requireHealthyLaunchTelemetry)
                    throw new Exception(msg + "\n请先启动一次客户端并完成 LaunchFlow。");
                return;
            }

            try
            {
                var metric = JsonUtility.FromJson<LaunchRunMetricSnapshot>(File.ReadAllText(LaunchMetricPath));
                if (metric == null)
                {
                    const string msg = "[Telemetry] launch_metrics_last.json 解析失败。";
                    AppendLog(msg);
                    if (_requireHealthyLaunchTelemetry)
                        throw new Exception(msg);
                    return;
                }

                long totalMs = 0;
                string failedPhase = string.Empty;
                foreach (var phase in metric.Phases)
                {
                    totalMs += phase.DurationMs;
                    if (!phase.Success && string.IsNullOrEmpty(failedPhase))
                        failedPhase = string.IsNullOrEmpty(phase.DisplayName) ? phase.Phase : phase.DisplayName;
                }

                AppendLog($"[Telemetry] 最近启动: success={metric.Success}, reason={metric.EndReason}, total={totalMs}ms");
                if (!string.IsNullOrEmpty(failedPhase))
                    AppendLog($"[Telemetry] 失败阶段: {failedPhase}");
                _lastTelemetryEndReason = metric.EndReason ?? string.Empty;
                _lastTelemetrySuccess = metric.Success;

                if (_requireHealthyLaunchTelemetry && !metric.Success)
                {
                    if (IsRecoverableLaunchTelemetryFailure(metric.EndReason, failedPhase))
                    {
                        AppendLog(
                            "[Telemetry] 最近一次失败属于旧整包缺热更程序集导致的可恢复失败，" +
                            "本次将重新同步完整热更程序集后继续构建。");
                        return;
                    }

                    throw new Exception(
                        "[Telemetry] 最近一次启动埋点为失败，已阻断整包构建。\n" +
                        $"reason={metric.EndReason}, failed_phase={failedPhase}");
                }
            }
            catch (Exception ex)
            {
                AppendLog($"[Telemetry] 读取启动埋点失败: {ex.Message}");
                if (_requireHealthyLaunchTelemetry)
                    throw;
            }
        }

        /// <summary>
        /// 判断最近一次启动失败是否可通过重新整包自愈。
        /// </summary>
        /// <param name="endReason">LaunchFlow 结束原因错误码。</param>
        /// <param name="failedPhase">首个失败阶段名称。</param>
        /// <returns>可由当前整包流程重新同步产物后修复时返回 true。</returns>
        private static bool IsRecoverableLaunchTelemetryFailure(string endReason, string failedPhase)
        {
            if (string.Equals(endReason, TelemetryErrorCodes.Launch.HotUpdateAssemblyLoadFailed, StringComparison.Ordinal))
                return true;

            return string.Equals(failedPhase, "step08_hotupdate_assembly_load", StringComparison.OrdinalIgnoreCase);
        }

        private void ValidateRequiredConfigTables(string dbPath)
        {
            var missing = GetMissingRequiredTables(dbPath, BootstrapRequiredTables);

            if (missing.Count > 0)
            {
                AddValidationResult("critical_tables", false, string.Join(",", missing));
                throw new Exception(
                    "首包 config.db 缺少关键表：\n" + string.Join("\n", missing) +
                    "\n\n请确认：\n" +
                    "1) 首包配表.xlsx 含 loading_tips 工作表且已保存；\n" +
                    "2) 关闭 Excel 后重新点「一键整包构建」；\n" +
                    "3) 查看 Console 是否有 [ExcelReader] 跳过工作表 或 补导失败 日志。");
            }

            AddValidationResult("critical_tables", true, "language,loading_tips");
        }

        private void ValidateUiAddressMappings(string dbPath)
        {
            var settings = AddressableAssetSettingsDefaultObject.Settings;
            if (settings == null)
                throw new Exception("Addressables Settings 不存在，无法校验 UI 地址映射。");

            var addressSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var group in settings.groups)
            {
                if (group == null) continue;
                foreach (var entry in group.entries)
                {
                    if (!string.IsNullOrEmpty(entry.address))
                        addressSet.Add(entry.address);
                }
            }

            var missingRows = new List<UiWndResAddressRow>();
            using (var db = new SQLiteConnection(dbPath))
            {
                int tableExists = db.ExecuteScalar<int>(
                    "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='ui_wnd_res'");

                if (tableExists == 0)
                {
                    AddValidationResult("ui_address_mapping", true, "skip(ui_wnd_res missing)");
                    return;
                }

                var rows = db.Query<UiWndResAddressRow>("SELECT Id, Address, Desc FROM ui_wnd_res");
                foreach (var row in rows)
                {
                    if (string.IsNullOrEmpty(row.Address)) continue;
                    if (!addressSet.Contains(row.Address))
                        missingRows.Add(row);
                }
            }

            if (missingRows.Count > 0)
            {
                string distinct = string.Join(
                    "\n",
                    missingRows
                        .GroupBy(row => row.Address, StringComparer.OrdinalIgnoreCase)
                        .Select(group => FormatMissingUiAddressRow(group.First())));
                AddValidationResult("ui_address_mapping", false, "missing");
                throw new Exception(
                    "UI 配表引用了不存在的 Addressables 资源：\n" +
                    distinct +
                    "\n\n造成原因：config.db 的 ui_wnd_res 表仍保留这些 UI 地址，但对应 Prefab 已删除或没有注册到 Addressables。\n" +
                    $"配置来源：{_excelFolder}/通用UI资源表.xlsx -> ui_wnd_res。\n\n" +
                    "处理方式：\n" +
                    "1) 如果这是已删除的测试资源，请从通用UI资源表.xlsx 的 ui_wnd_res 工作表删除对应行；\n" +
                    "2) 如果这是正式资源，请恢复 Prefab 到 Assets/ResourcesOut 下并重新同步 Addressables；\n" +
                    "3) 关闭 Excel 后重新点击「一键整包构建」，让 RefData/config.db 重新导出。");
            }

            AddValidationResult("ui_address_mapping", true, "all addresses resolved");
        }

        /// <summary>
        /// 格式化缺失的 UI 地址记录，便于在整包失败弹窗中直接定位配置行。
        /// </summary>
        private static string FormatMissingUiAddressRow(UiWndResAddressRow row)
        {
            string desc = string.IsNullOrEmpty(row.Desc) ? string.Empty : $"，Desc={row.Desc}";
            return $"Id={row.Id}，Address={row.Address}{desc}";
        }

        private void AddValidationResult(string rule, bool passed, string detail)
        {
            string line = $"{rule}: {(passed ? "PASS" : "FAIL")} ({detail})";
            _validationResults.Add(line);
            AppendLog($"[Check] {line}");
        }

        private void WriteFullPackageBuildReport(bool success, string errorMessage = null)
        {
            var report = new FullPackageBuildReportSnapshot
            {
                GeneratedAtUtc = DateTime.UtcNow.ToString("O"),
                Success = success,
                Error = errorMessage ?? string.Empty,
                ProfileBefore = _profileBeforeRun,
                ProfileAfter = GetActiveProfileName(),
                AppVersion = _appVersion,
                MinCompatibleVersion = _minCompatibleVersion,
                BuildOutputPath = _buildOutputPath,
                BuildTarget = EditorUserBuildSettings.activeBuildTarget.ToString(),
                AutoSwitchBackAfterBuild = _autoSwitchBackAfterBuild,
                TelemetryEndReason = _lastTelemetryEndReason,
                TelemetrySuccess = _lastTelemetrySuccess,
                ValidationResults = new List<string>(_validationResults)
            };

            report.Artifacts.Add(BuildArtifactSnapshotOf(ToAbsoluteProjectPath(_streamingDbPath)));
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

        /// <summary>
        /// ui_wnd_res 表中用于校验 Addressables 映射的最小字段集合。
        /// </summary>
        private class UiWndResAddressRow
        {
            /// <summary>UI 配表记录编号，用于定位 Excel 源行。</summary>
            public int Id { get; set; }

            /// <summary>UI Prefab 的 Addressables 地址。</summary>
            public string Address { get; set; }

            /// <summary>配置描述，用于在错误提示中区分测试资源和正式资源。</summary>
            public string Desc { get; set; }
        }
    }
}
