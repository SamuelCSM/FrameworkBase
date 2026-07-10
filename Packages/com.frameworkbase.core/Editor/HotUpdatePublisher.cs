using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Framework.Editor.Release;
using Framework.HotUpdate;
using HybridCLR.Editor.Commands;
using UnityEditor;
using UnityEditor.AddressableAssets;
using UnityEditor.AddressableAssets.Build;
using UnityEditor.AddressableAssets.Settings;
using UnityEngine;

namespace Framework.Editor
{
    /// <summary>
    /// 热更新一键发布工具
    ///
    /// 打开方式：Unity 菜单 → Framework → Hot Update Publisher
    ///
    /// 功能：
    ///   - 自动读取当前 version.json
    ///   - 勾选需要更新的内容（资源 / 代码）自动递增对应版本号
    ///   - 一键完成：Build Addressables → 复制 DLL → 生成 version.json → 同步到输出目录
    /// </summary>
    public class HotUpdatePublisher : EditorWindow
    {
        // ─── 版本号 ────────────────────────────────────────────
        private string _appVersion = "1.0";
        private int    _resourceVersion = 1;
        private int    _codeVersion     = 1;
        private string _minCompatible   = "1.0";
        private string _description     = "";
        // 灰度放量百分比：0=全量下发；1~99 仅命中分桶设备应用本次更新（上调放量=同版本重发清单只改此值）。
        private int    _grayPercent     = 0;
        // 整包更新跳转链接（应用商店/安装包地址），仅勾选"强制整包更新"时写入清单。
        private string _updateUrl       = "";

        // ─── 发布选项 ──────────────────────────────────────────
        private bool _publishResource = false;  // 是否发布资源更新
        private bool _publishCode     = false;  // 是否发布代码更新
        private bool _forceUpdate     = false;  // 是否强制整包更新

        // ─── 输出路径 ──────────────────────────────────────────
        // 部署目录属机器级配置：默认留空（只写工程内 ServerData），路径存 EditorPrefs 不进仓库。
        private const string VersionOutputDirPrefsKey = "FrameworkBase.HotUpdatePublisher.VersionOutputDir";
        private const string BundleOutputDirPrefsKey  = "FrameworkBase.HotUpdatePublisher.BundleOutputDir";

        // version.json 写入目录（可在窗口里修改；留空则只写 ServerData）
        private string _versionOutputDir = string.Empty;
        // bundle 同步目录（留空则只写 ServerData）
        private string _bundleOutputDir  = string.Empty;

        private void OnEnable()
        {
            _versionOutputDir = EditorPrefs.GetString(VersionOutputDirPrefsKey, _versionOutputDir);
            _bundleOutputDir  = EditorPrefs.GetString(BundleOutputDirPrefsKey,  _bundleOutputDir);
        }

        private void OnDisable()
        {
            EditorPrefs.SetString(VersionOutputDirPrefsKey, _versionOutputDir ?? string.Empty);
            EditorPrefs.SetString(BundleOutputDirPrefsKey,  _bundleOutputDir ?? string.Empty);
        }

        // ─── 内部状态 ──────────────────────────────────────────
        private string _log = "";
        private bool   _building = false;
        private Vector2 _scroll;

        // ServerData 本地路径（相对项目根）
        private static string ServerDataDir =>
            Path.Combine(Directory.GetParent(Application.dataPath).FullName,
                         "ServerData", "Updates");

        /// <summary>
        /// 获取指定构建目标的 HybridCLR 热更 DLL 生成路径（委托到流水线步骤共用工具）。
        /// </summary>
        /// <param name="target">Unity 构建目标。</param>
        /// <param name="bytesFileName">运行时加载的热更程序集 bytes 文件名。</param>
        /// <returns>指定热更程序集 DLL 的绝对路径。</returns>
        private static string GetHotUpdateDllSrc(BuildTarget target, string bytesFileName)
        {
            return HotUpdateReleaseSteps.GetHotUpdateDllSrc(target, bytesFileName);
        }

        [MenuItem("Framework/Hot Update Publisher")]
        public static void Open()
        {
            var win = GetWindow<HotUpdatePublisher>("热更新发布");
            win.minSize = new Vector2(500, 580);
            win.LoadCurrentVersion();
        }

        // ─── 初始化：读取 ServerData/Updates/version.json ──────
        private void LoadCurrentVersion()
        {
            string path = Path.Combine(ServerDataDir, "version.json");
            if (!File.Exists(path))
            {
                AppendLog("未找到 version.json，使用默认版本 1.0 / 1 / 1");
                return;
            }

            try
            {
                string json = File.ReadAllText(path);
                var info = JsonUtility.FromJson<UpdateInfo>(json);
                _appVersion      = info.AppVersion      ?? "1.0";
                _resourceVersion = info.ResourceVersion;
                _codeVersion     = info.CodeVersion;
                _minCompatible   = info.MinCompatibleVersion ?? _appVersion;
                _description     = "";
                // 回填当前放量值：支持"同版本重发清单、只上调 GrayPercent"的放量操作。
                _grayPercent     = info.GrayPercent;
                _updateUrl       = info.UpdateUrl ?? "";
                AppendLog($"已读取当前版本：App={_appVersion} " +
                          $"Resource={_resourceVersion} Code={_codeVersion} Gray={_grayPercent}");
            }
            catch (Exception e)
            {
                AppendLog($"读取 version.json 失败：{e.Message}");
            }
        }

        // ─── 编辑器界面 ────────────────────────────────────────
        private void OnGUI()
        {
            EditorGUILayout.Space(6);
            GUILayout.Label("热更新一键发布工具", EditorStyles.boldLabel);
            EditorGUILayout.HelpBox(
                "勾选要发布的内容 → 填写更新描述 → 点击「一键发布」\n" +
                "工具会自动递增版本号、Build 资源、复制 DLL、生成 version.json",
                MessageType.Info);

            EditorGUILayout.Space(4);

            DrawReleaseEnvSelector();

            EditorGUILayout.Space(4);

            // ── 版本号区域 ──────────────────────────────────────
            GUILayout.Label("当前版本", EditorStyles.boldLabel);
            EditorGUI.BeginDisabledGroup(_building);

            EditorGUILayout.BeginVertical("box");
            _appVersion = EditorGUILayout.TextField("App Version（整包版本）", _appVersion);
            EditorGUI.BeginDisabledGroup(true);
            EditorGUILayout.IntField("Resource Version（当前）", _resourceVersion);
            EditorGUILayout.IntField("Code Version（当前）", _codeVersion);
            EditorGUI.EndDisabledGroup();
            _minCompatible = EditorGUILayout.TextField("最低兼容版本", _minCompatible);
            EditorGUILayout.EndVertical();

            EditorGUILayout.Space(4);

            // ── 发布选项 ────────────────────────────────────────
            GUILayout.Label("本次发布内容", EditorStyles.boldLabel);
            EditorGUILayout.BeginVertical("box");

            int previewRes  = _forceUpdate ? 1 : _resourceVersion + (_publishResource ? 1 : 0);
            int previewCode = _forceUpdate ? 1 : _codeVersion     + (_publishCode     ? 1 : 0);

            _publishResource = EditorGUILayout.ToggleLeft(
                $"  资源更新（Build Addressables，ResourceVersion → {previewRes}）",
                _publishResource);

            _publishCode = EditorGUILayout.ToggleLeft(
                $"  代码更新（复制 HotUpdate.dll，CodeVersion → {previewCode}）",
                _publishCode);

            EditorGUILayout.Space(2);
            _forceUpdate = EditorGUILayout.ToggleLeft(
                "  强制整包更新（AppVersion 已变更，阻止旧包进入游戏）",
                _forceUpdate);

            if (_forceUpdate)
            {
                EditorGUILayout.HelpBox(
                    "⚠ 整包更新：发布后 ResourceVersion / CodeVersion 将重置为 1，" +
                    "请确认 AppVersion 已修改为新版本号。",
                    MessageType.Warning);
                _updateUrl = EditorGUILayout.TextField("整包下载链接（商店/安装包）", _updateUrl);
            }

            EditorGUILayout.EndVertical();

            EditorGUILayout.Space(4);

            // ── 灰度放量 ────────────────────────────────────────
            GUILayout.Label("灰度放量", EditorStyles.boldLabel);
            EditorGUILayout.BeginVertical("box");
            _grayPercent = EditorGUILayout.IntSlider(
                new GUIContent("放量百分比", "0=全量下发；1~99 仅命中分桶的设备应用本次更新。" +
                                            "上调放量：同版本重新点发布、只改此值即可（清单会重签）。"),
                _grayPercent, 0, 100);
            if (_grayPercent > 0 && _grayPercent < 100)
                EditorGUILayout.HelpBox(
                    $"本次更新仅对约 {_grayPercent}% 的设备生效，其余设备按\"无更新\"继续。",
                    MessageType.Info);
            EditorGUILayout.EndVertical();

            EditorGUILayout.Space(4);

            // ── 更新描述 ────────────────────────────────────────
            GUILayout.Label("更新描述", EditorStyles.boldLabel);
            _description = EditorGUILayout.TextArea(_description,
                GUILayout.Height(48));

            EditorGUILayout.Space(4);

            // ── 输出路径 ────────────────────────────────────────
            GUILayout.Label("输出路径", EditorStyles.boldLabel);
            EditorGUILayout.BeginVertical("box");

            EditorGUILayout.BeginHorizontal();
            _versionOutputDir = EditorGUILayout.TextField("version.json 输出目录", _versionOutputDir);
            if (GUILayout.Button("...", GUILayout.Width(28)))
                _versionOutputDir = EditorUtility.OpenFolderPanel("选择目录", _versionOutputDir, "") 
                                    .Replace('/', Path.DirectorySeparatorChar);
            EditorGUILayout.EndHorizontal();

            EditorGUILayout.BeginHorizontal();
            _bundleOutputDir = EditorGUILayout.TextField("Bundle 同步目录（可空）", _bundleOutputDir);
            if (GUILayout.Button("...", GUILayout.Width(28)))
                _bundleOutputDir = EditorUtility.OpenFolderPanel("选择目录", _bundleOutputDir, "")
                                   .Replace('/', Path.DirectorySeparatorChar);
            EditorGUILayout.EndHorizontal();

            EditorGUILayout.EndVertical();

            EditorGUILayout.Space(6);

            // ── 操作按钮 ────────────────────────────────────────
            EditorGUI.BeginDisabledGroup(_building || (!_publishResource && !_publishCode && !_forceUpdate));
            if (GUILayout.Button("一键发布热更新", GUILayout.Height(36)))
                Publish();
            EditorGUI.EndDisabledGroup();

            EditorGUILayout.Space(4);

            // ── 打包辅助：准备 StreamingAssets ─────────────────
            EditorGUI.BeginDisabledGroup(_building);
            EditorGUILayout.HelpBox(
                "打包 Player 前必须先点「同步 StreamingAssets」，\n" +
                "将当前热更程序集组与 HybridCLR AOT 元数据放入内置目录，作为首次运行的基础版本。",
                MessageType.Warning);
            if (GUILayout.Button("同步热更程序集 + HybridCLR AOT → StreamingAssets（打包前执行）", GUILayout.Height(30)))
                SyncToStreamingAssets();
            EditorGUI.EndDisabledGroup();

            EditorGUILayout.Space(4);

            EditorGUI.BeginDisabledGroup(_building);
            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Button("刷新当前版本"))  LoadCurrentVersion();
            if (GUILayout.Button("清空日志"))      _log = "";
            EditorGUILayout.EndHorizontal();
            EditorGUI.EndDisabledGroup();

            EditorGUILayout.Space(4);

            // ── 日志区域 ────────────────────────────────────────
            GUILayout.Label("发布日志", EditorStyles.boldLabel);
            _scroll = EditorGUILayout.BeginScrollView(_scroll,
                GUILayout.Height(140));
            EditorGUILayout.SelectableLabel(_log,
                EditorStyles.miniLabel,
                GUILayout.ExpandHeight(true));
            EditorGUILayout.EndScrollView();
        }

        // ─── 发布环境选择 ───────────────────────────────────────
        /// <summary>绘制发布环境下拉（dev/qa/staging/prod），选择结果存 EditorPrefs（机器级）。</summary>
        private void DrawReleaseEnvSelector()
        {
            GUILayout.Label("发布环境", EditorStyles.boldLabel);
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
            else
            {
                EditorGUILayout.LabelField($"BaseUrl：{profile.BaseUrl}");
                EditorGUILayout.LabelField($"强制 HTTPS：{profile.RequireHttps}  强制签名：{profile.RequireManifestSignature}");
                if (profile.RequireManifestSignature && !UpdateManifestSigner.HasUsablePrivateKey)
                    EditorGUILayout.HelpBox(
                        "该环境要求签名，但本机未配置可用私钥——发布将被阻断。\n" +
                        "菜单 Framework → Hot Update Security → Generate Signing Key Pair / Set Private Key Path。",
                        MessageType.Warning);
            }

            EditorGUILayout.EndVertical();
        }

        // ─── 核心发布流程 ───────────────────────────────────────
        private void Publish()
        {
            _building = true;
            _log = "";

            try
            {
                // 版本递增规则收口到 VersionPolicy（整包归 1、热更 +1）。
                (int newResource, int newCode) = VersionPolicy.Next(
                    _forceUpdate, _publishResource, _publishCode, _resourceVersion, _codeVersion);

                AppendLog($"===== 开始发布 =====");
                AppendLog(_forceUpdate
                    ? $"整包更新：App={_appVersion}  Resource/Code 重置为 1"
                    : $"热更版本：App={_appVersion}  Resource={_resourceVersion}→{newResource}  Code={_codeVersion}→{newCode}");

                // 流程收敛到 ReleasePipeline：窗口只负责收集参数与展示结果，
                // 未来 CI / Release Center 组装同一组步骤即可复用整条流水线。
                var context = new ReleaseContext
                {
                    PublishResource      = _publishResource,
                    PublishCode          = _publishCode,
                    ForceUpdate          = _forceUpdate,
                    AppVersion           = _appVersion,
                    ResourceVersion      = newResource,
                    CodeVersion          = newCode,
                    MinCompatibleVersion = _minCompatible,
                    Description          = _description ?? string.Empty,
                    GrayPercent          = _grayPercent,
                    UpdateUrl            = _updateUrl ?? string.Empty,
                    ServerDataDir        = ServerDataDir,
                    VersionOutputDir     = _versionOutputDir,
                    BundleOutputDir      = _bundleOutputDir,
                    Log                  = AppendLog
                };

                var pipelineResult = ReleasePipeline.Run(new IReleaseStep[]
                {
                    new HotUpdateReleaseSteps.ValidateReleaseEnvironment(),
                    new HotUpdateReleaseSteps.BuildAddressables(),
                    new HotUpdateReleaseSteps.CompileAndCopyHotUpdateDlls(),
                    new HotUpdateReleaseSteps.GenerateManifest(),
                    new HotUpdateReleaseSteps.WriteAndSignManifest(),
                    new ReleasePublishingSteps.WriteReleaseLedger(),
                    new ReleasePublishingSteps.AtomicPublishArtifacts()
                }, context);

                if (!pipelineResult.Success)
                    throw new Exception($"步骤 {pipelineResult.FailedStep} 失败：{pipelineResult.Error}");

                // 更新内存版本号
                _resourceVersion = newResource;
                _codeVersion     = newCode;

                AppendLog("===== 发布成功 =====");
                EditorUtility.DisplayDialog("发布完成",
                    $"热更新发布成功！\n\n" +
                    $"ResourceVersion: {newResource}\n" +
                    $"CodeVersion:     {newCode}",
                    "OK");
            }
            catch (Exception e)
            {
                AppendLog($"[ERROR] {e.Message}");
                EditorUtility.DisplayDialog("发布失败", e.Message, "OK");
            }
            finally
            {
                _building = false;
                Repaint();
            }
        }

        /// <summary>
        /// 将当前全部热更程序集同步到 StreamingAssets，作为 Player 内置初始版本。
        /// 打包前必须执行一次，否则首次启动找不到解释域 DLL。
        /// </summary>
        private void SyncToStreamingAssets()
        {
            try
            {
                AppendLog("[StreamingAssets] 检查/生成 HybridCLR 产物（热更程序集 + AOT 元数据）...");
                EnsureHybridCLROutputsForBuild(EditorUserBuildSettings.activeBuildTarget);

                string streamingDir = Path.Combine(Application.dataPath, "StreamingAssets");
                Directory.CreateDirectory(streamingDir);

                // 1. 同步全部热更程序集
                foreach (string bytesFileName in VersionManager.HotUpdateAssemblyFileNames)
                {
                    string src = GetHotUpdateDllSrc(EditorUserBuildSettings.activeBuildTarget, bytesFileName);
                    string dllDest = Path.Combine(streamingDir, bytesFileName);
                    File.Copy(src, dllDest, overwrite: true);
                    AppendLog($"[StreamingAssets] {bytesFileName} → {dllDest}");
                }

                // 2. 同步当前 version.json（让 Player 知道自己的出厂版本，避免首次启动误触发热更）
                string versionSrc = Path.Combine(ServerDataDir, "version.json");
                if (!File.Exists(versionSrc))
                    versionSrc = Path.Combine(_versionOutputDir, "version.json");

                if (File.Exists(versionSrc))
                {
                    string versionDest = Path.Combine(streamingDir, "version.json");
                    File.Copy(versionSrc, versionDest, overwrite: true);
                    AppendLog($"[StreamingAssets] version.json → {versionDest}");
                }
                else
                {
                    AppendLog("[StreamingAssets] 警告：未找到 version.json，Player 首次启动可能误触发热更");
                }

                HybridCLRStreamingAssetsSync.SyncMetadataToStreamingAssets(refreshAssetDatabase: false);
                AppendLog("[StreamingAssets] HybridCLR AOT 元数据已同步");

                AssetDatabase.Refresh();
                EditorUtility.DisplayDialog("同步完成",
                    "StreamingAssets 已更新：\n" +
                    "  • 热更程序集 *.dll.bytes\n" +
                    "  • version.json\n" +
                    "  • HybridCLRMetadata/*.dll.bytes\n\n" +
                    "现在可以执行 File → Build Settings → Build 打包。", "OK");
            }
            catch (Exception ex)
            {
                AppendLog($"[ERROR] StreamingAssets 同步失败：{ex.Message}");
                EditorUtility.DisplayDialog("同步失败", ex.Message, "OK");
                throw;
            }
        }

        /// <summary>
        /// 确保整包构建所需的 HybridCLR 生成产物齐全；缺失时自动执行 Generate/All。
        /// </summary>
        /// <param name="target">当前构建目标。</param>
        private static void EnsureHybridCLROutputsForBuild(BuildTarget target)
        {
            CompileDllCommand.CompileDll(target);

            var missing = GetMissingHybridCLROutputs(target);
            if (missing.Count == 0)
                return;

            if (target != EditorUserBuildSettings.activeBuildTarget)
            {
                throw new InvalidOperationException(
                    $"HybridCLR Generate/All 只能生成当前 ActiveBuildTarget 的产物。当前={EditorUserBuildSettings.activeBuildTarget}, 需要={target}");
            }

            Il2CppToolchainValidator.ValidateForBuildTarget(target);

            Debug.LogWarning(
                "[HotUpdatePublisher] HybridCLR 生成产物缺失，将自动执行 HybridCLR/Generate/All：\n" +
                string.Join("\n", missing));

            PrebuildCommand.GenerateAll();

            missing = GetMissingHybridCLROutputs(target);
            if (missing.Count > 0)
            {
                throw new InvalidOperationException(
                    "HybridCLR/Generate/All 执行后仍缺少发布必需产物：\n" +
                    string.Join("\n", missing));
            }
        }

        /// <summary>
        /// 获取缺失的 HybridCLR 发布必需生成产物。
        /// </summary>
        /// <param name="target">当前构建目标。</param>
        /// <returns>缺失产物的绝对路径列表。</returns>
        private static System.Collections.Generic.List<string> GetMissingHybridCLROutputs(BuildTarget target)
        {
            var missing = new System.Collections.Generic.List<string>();

            foreach (string bytesFileName in VersionManager.HotUpdateAssemblyFileNames)
            {
                string hotUpdateDll = GetHotUpdateDllSrc(target, bytesFileName);
                if (!File.Exists(hotUpdateDll))
                    missing.Add(hotUpdateDll);
            }

            missing.AddRange(HybridCLRStreamingAssetsSync.GetMissingGeneratedMetadataFiles(target));
            return missing;
        }

        /// <summary>
        /// 对外暴露：打包前同步热更程序集组、HybridCLR AOT 元数据与 version.json 到 StreamingAssets。
        /// </summary>
        public static void SyncToStreamingAssetsForBuild(string versionOutputDir = null, bool showDialog = true)
        {
            try
            {
                BuildTarget target = EditorUserBuildSettings.activeBuildTarget;
                EnsureHybridCLROutputsForBuild(target);

                string streamingDir = Path.Combine(Application.dataPath, "StreamingAssets");
                Directory.CreateDirectory(streamingDir);

                foreach (string bytesFileName in VersionManager.HotUpdateAssemblyFileNames)
                {
                    string src = GetHotUpdateDllSrc(target, bytesFileName);
                    if (!File.Exists(src))
                    {
                        if (showDialog)
                            EditorUtility.DisplayDialog("错误",
                                $"未找到热更程序集：\n{src}\n\n请先执行 HybridCLR/Generate/All 再打包。", "OK");
                        return;
                    }

                    string dllDest = Path.Combine(streamingDir, bytesFileName);
                    File.Copy(src, dllDest, overwrite: true);
                }

                string versionSrc = Path.Combine(ServerDataDir, "version.json");
                if (!File.Exists(versionSrc) && !string.IsNullOrEmpty(versionOutputDir))
                    versionSrc = Path.Combine(versionOutputDir, "version.json");

                if (File.Exists(versionSrc))
                {
                    string versionDest = Path.Combine(streamingDir, "version.json");
                    File.Copy(versionSrc, versionDest, overwrite: true);
                }

                HybridCLRStreamingAssetsSync.SyncMetadataToStreamingAssets(target, refreshAssetDatabase: false);

                AssetDatabase.Refresh();

                if (showDialog)
                {
                    EditorUtility.DisplayDialog("同步完成",
                        "StreamingAssets 已更新：\n" +
                        "  • 热更程序集 *.dll.bytes\n" +
                        "  • version.json（如果存在）\n" +
                        "  • HybridCLRMetadata/*.dll.bytes\n\n" +
                        "现在可以执行 File → Build Settings → Build 打包。", "OK");
                }
            }
            catch (Exception e)
            {
                if (showDialog)
                    EditorUtility.DisplayDialog("同步失败", e.Message, "OK");
                throw;
            }
        }

        private void AppendLog(string msg)
        {
            _log += $"[{DateTime.Now:HH:mm:ss}] {msg}\n";
            Repaint();
        }
    }

    /// <summary>
    /// IL2CPP 本机构建工具链校验器，用于在发布流程开始前提前阻断环境缺失问题。
    /// </summary>
    public static class Il2CppToolchainValidator
    {
        /// <summary>
        /// 校验当前 ActiveBuildTarget 的 IL2CPP 工具链环境。
        /// </summary>
        public static void ValidateForActiveBuildTarget()
        {
            ValidateForBuildTarget(EditorUserBuildSettings.activeBuildTarget);
        }

        /// <summary>
        /// 校验指定构建目标的 IL2CPP 工具链环境。
        /// </summary>
        /// <param name="target">需要校验的 Unity 构建目标。</param>
        public static void ValidateForBuildTarget(BuildTarget target)
        {
            if (!RequiresWindowsX64Toolchain(target))
                return;

            var missing = new List<string>();
            if (!HasVisualStudioCppToolchain(out string vsDetail))
                missing.Add("Visual Studio 2022/2019 Build Tools：缺少“使用 C++ 的桌面开发”或 VC++ x64 编译工具。");

            if (!HasWindowsSdk(out string sdkDetail))
                missing.Add("Windows 10/11 SDK：缺少 Windows SDK Include/Lib。");

            if (missing.Count == 0)
                return;

            throw new InvalidOperationException(
                "当前一键打包需要 IL2CPP Windows x64 C++ 工具链，但本机环境不完整。\n\n" +
                "缺失项：\n- " + string.Join("\n- ", missing) + "\n\n" +
                "处理方式：打开 Visual Studio Installer，给当前 VS/Build Tools 安装“使用 C++ 的桌面开发”，并勾选：\n" +
                "- MSVC v143/v142 x64/x86 build tools\n" +
                "- Windows 10 SDK 或 Windows 11 SDK\n\n" +
                $"检测详情：\nVS C++: {vsDetail}\nWindows SDK: {sdkDetail}");
        }

        /// <summary>
        /// 判断当前平台配置是否需要 Windows x64 C++ 工具链。
        /// </summary>
        /// <param name="target">Unity 构建目标。</param>
        /// <returns>需要 Windows x64 C++ 工具链时返回 true。</returns>
        private static bool RequiresWindowsX64Toolchain(BuildTarget target)
        {
            if (Application.platform != RuntimePlatform.WindowsEditor)
                return false;

            if (target != BuildTarget.StandaloneWindows64 && target != BuildTarget.StandaloneWindows)
                return false;

            BuildTargetGroup group = BuildPipeline.GetBuildTargetGroup(target);
            return PlayerSettings.GetScriptingBackend(group) == ScriptingImplementation.IL2CPP;
        }

        /// <summary>
        /// 检查 Visual Studio 是否安装 VC++ x64 编译工具。
        /// </summary>
        /// <param name="detail">检测详情，用于错误提示。</param>
        /// <returns>检测到 VC++ x64 工具时返回 true。</returns>
        private static bool HasVisualStudioCppToolchain(out string detail)
        {
            string vswhere = FindVsWhere();
            if (string.IsNullOrEmpty(vswhere))
            {
                detail = "未找到 vswhere.exe。";
                return HasVcVarsInKnownLocations(out detail);
            }

            string output = RunProcess(
                vswhere,
                "-products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath -format value");

            var installations = SplitLines(output)
                .Where(Directory.Exists)
                .ToList();

            if (installations.Any(HasVcVars64))
            {
                detail = string.Join("; ", installations);
                return true;
            }

            string allInstallations = RunProcess(vswhere, "-products * -property installationPath -format value");
            detail = string.IsNullOrWhiteSpace(allInstallations)
                ? "未检测到 Visual Studio 安装。"
                : $"已检测到 VS，但未检测到 VC++ x64 组件：{allInstallations.Trim()}";
            return false;
        }

        /// <summary>
        /// 检查 Windows SDK Include 目录是否存在。
        /// </summary>
        /// <param name="detail">检测详情，用于错误提示。</param>
        /// <returns>检测到 Windows SDK 时返回 true。</returns>
        private static bool HasWindowsSdk(out string detail)
        {
            var roots = new[]
            {
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Windows Kits", "10", "Include"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Windows Kits", "11", "Include"),
            };

            var versions = roots
                .Where(Directory.Exists)
                .SelectMany(root => Directory.GetDirectories(root).Select(Path.GetFileName))
                .Where(name => !string.IsNullOrEmpty(name))
                .ToList();

            if (versions.Count > 0)
            {
                detail = string.Join(", ", versions);
                return true;
            }

            string vswhere = FindVsWhere();
            if (!string.IsNullOrEmpty(vswhere))
            {
                string output = RunProcess(
                    vswhere,
                    "-products * -requiresAny -requires Microsoft.VisualStudio.Component.Windows10SDK.* Microsoft.VisualStudio.Component.Windows11SDK.* -property installationPath -format value");

                var installations = SplitLines(output)
                    .Where(Directory.Exists)
                    .ToList();

                if (installations.Count > 0)
                {
                    detail = $"Visual Studio 组件中检测到 Windows SDK：{string.Join("; ", installations)}";
                    return true;
                }
            }

            detail = "未找到 C:\\Program Files (x86)\\Windows Kits\\10/11\\Include，也未在 Visual Studio 组件中检测到 Windows SDK。";
            return false;
        }

        /// <summary>
        /// 查找 Visual Studio Installer 提供的 vswhere.exe。
        /// </summary>
        /// <returns>vswhere.exe 路径；不存在时返回空字符串。</returns>
        private static string FindVsWhere()
        {
            string[] candidates =
            {
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Microsoft Visual Studio", "Installer", "vswhere.exe"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Microsoft Visual Studio", "Installer", "vswhere.exe")
            };

            return candidates.FirstOrDefault(File.Exists) ?? string.Empty;
        }

        /// <summary>
        /// 在常见安装目录中查找 vcvars64.bat。
        /// </summary>
        /// <param name="detail">检测详情，用于错误提示。</param>
        /// <returns>找到 vcvars64.bat 时返回 true。</returns>
        private static bool HasVcVarsInKnownLocations(out string detail)
        {
            string[] roots =
            {
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Microsoft Visual Studio"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Microsoft Visual Studio")
            };

            foreach (string root in roots.Where(Directory.Exists))
            {
                string[] matches = Directory.GetFiles(root, "vcvars64.bat", SearchOption.AllDirectories);
                if (matches.Length > 0)
                {
                    detail = matches[0];
                    return true;
                }
            }

            detail = "未找到 VC\\Auxiliary\\Build\\vcvars64.bat。";
            return false;
        }

        /// <summary>
        /// 判断指定 Visual Studio 安装目录是否包含 vcvars64.bat。
        /// </summary>
        /// <param name="installationPath">Visual Studio 安装目录。</param>
        /// <returns>存在 vcvars64.bat 时返回 true。</returns>
        private static bool HasVcVars64(string installationPath)
        {
            string vcvars = Path.Combine(installationPath, "VC", "Auxiliary", "Build", "vcvars64.bat");
            return File.Exists(vcvars);
        }

        /// <summary>
        /// 执行外部检测程序并返回标准输出。
        /// </summary>
        /// <param name="fileName">可执行文件路径。</param>
        /// <param name="arguments">命令行参数。</param>
        /// <returns>标准输出文本；执行失败时返回空字符串。</returns>
        private static string RunProcess(string fileName, string arguments)
        {
            try
            {
                using var process = new System.Diagnostics.Process();
                process.StartInfo = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = fileName,
                    Arguments = arguments,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                };
                process.Start();
                string output = process.StandardOutput.ReadToEnd();
                process.WaitForExit(5000);
                return output;
            }
            catch
            {
                return string.Empty;
            }
        }

        /// <summary>
        /// 按行拆分外部检测程序输出。
        /// </summary>
        /// <param name="text">输出文本。</param>
        /// <returns>非空行列表。</returns>
        private static IEnumerable<string> SplitLines(string text)
        {
            return (text ?? string.Empty)
                .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(line => line.Trim());
        }
    }
}
