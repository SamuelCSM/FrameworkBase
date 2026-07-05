using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
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

        // ─── 发布选项 ──────────────────────────────────────────
        private bool _publishResource = false;  // 是否发布资源更新
        private bool _publishCode     = false;  // 是否发布代码更新
        private bool _forceUpdate     = false;  // 是否强制整包更新

        // ─── 输出路径 ──────────────────────────────────────────
        // version.json 写入目录（可在窗口里修改）
        private string _versionOutputDir = @"D:\IIS\Updates";
        // bundle 同步到 IIS 的目录（留空则只写 ServerData）
        private string _bundleOutputDir  = @"D:\IIS\StandaloneWindows64";

        // ─── 内部状态 ──────────────────────────────────────────
        private string _log = "";
        private bool   _building = false;
        private Vector2 _scroll;

        // ServerData 本地路径（相对项目根）
        private static string ServerDataDir =>
            Path.Combine(Directory.GetParent(Application.dataPath).FullName,
                         "ServerData", "Updates");

        /// <summary>
        /// 热更程序集发布产物信息，用于生成 version.json 的 PatchFiles。
        /// </summary>
        private readonly struct HotUpdateDllArtifact
        {
            /// <summary>补丁文件名，例如 HotUpdate.dll.bytes。</summary>
            public readonly string FileName;

            /// <summary>补丁文件大小，单位为字节。</summary>
            public readonly long Size;

            /// <summary>补丁文件 MD5，用于客户端下载后校验。</summary>
            public readonly string Md5;

            /// <summary>
            /// 创建热更程序集发布产物信息。
            /// </summary>
            public HotUpdateDllArtifact(string fileName, long size, string md5)
            {
                FileName = fileName;
                Size = size;
                Md5 = md5;
            }
        }

        /// <summary>
        /// 获取指定构建目标的 HybridCLR 热更 DLL 生成路径。
        /// </summary>
        /// <param name="target">Unity 构建目标。</param>
        /// <param name="bytesFileName">运行时加载的热更程序集 bytes 文件名。</param>
        /// <returns>指定热更程序集 DLL 的绝对路径。</returns>
        private static string GetHotUpdateDllSrc(BuildTarget target, string bytesFileName)
        {
            string assemblyName = VersionManager.ToAssemblyName(bytesFileName);
            return Path.Combine(Directory.GetParent(Application.dataPath).FullName,
                         "HybridCLRData", "HotUpdateDlls",
                         GetBuildTargetName(target), assemblyName + ".dll");
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
                var info = JsonUtility.FromJson<VersionSnapshot>(json);
                _appVersion      = info.AppVersion      ?? "1.0";
                _resourceVersion = info.ResourceVersion;
                _codeVersion     = info.CodeVersion;
                _minCompatible   = info.MinCompatibleVersion ?? _appVersion;
                _description     = "";
                AppendLog($"已读取当前版本：App={_appVersion} " +
                          $"Resource={_resourceVersion} Code={_codeVersion}");
            }
            catch (Exception e)
            {
                AppendLog($"读取 version.json 失败：{e.Message}");
            }
        }

        // ─── GUI ───────────────────────────────────────────────
        private void OnGUI()
        {
            EditorGUILayout.Space(6);
            GUILayout.Label("热更新一键发布工具", EditorStyles.boldLabel);
            EditorGUILayout.HelpBox(
                "勾选要发布的内容 → 填写更新描述 → 点击「一键发布」\n" +
                "工具会自动递增版本号、Build 资源、复制 DLL、生成 version.json",
                MessageType.Info);

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
                EditorGUILayout.HelpBox(
                    "⚠ 整包更新：发布后 ResourceVersion / CodeVersion 将重置为 1，" +
                    "请确认 AppVersion 已修改为新版本号。",
                    MessageType.Warning);

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

        // ─── 核心发布流程 ───────────────────────────────────────
        private void Publish()
        {
            _building = true;
            _log = "";

            try
            {
                // 1. 计算新版本号
                // 整包更新（AppVersion 已变更）：热更计数归 1 重算，
                // 因为新大版本的安装包里内置的就是 Resource=1 / Code=1。
                int newResource, newCode;
                if (_forceUpdate)
                {
                    newResource = 1;
                    newCode     = 1;
                }
                else
                {
                    newResource = _publishResource ? _resourceVersion + 1 : _resourceVersion;
                    newCode     = _publishCode     ? _codeVersion     + 1 : _codeVersion;
                }

                AppendLog($"===== 开始发布 =====");
                AppendLog(_forceUpdate
                    ? $"整包更新：App={_appVersion}  Resource/Code 重置为 1"
                    : $"热更版本：App={_appVersion}  Resource={_resourceVersion}→{newResource}  Code={_codeVersion}→{newCode}");

                // 2. Build Addressables（资源更新时）
                if (_publishResource)
                {
                    AppendLog("[1/4] 构建 Addressables 资源包...");
                    BuildAddressables();
                    AppendLog("      Addressables Build 完成");

                    // 可选：同步 bundle 到 IIS 目录
                    if (!string.IsNullOrEmpty(_bundleOutputDir))
                    {
                        AppendLog($"      同步 bundle → {_bundleOutputDir}");
                        SyncBundles(_bundleOutputDir);
                    }
                }

                // 3. 复制热更程序集（代码更新时）
                var dllArtifacts = new List<HotUpdateDllArtifact>();

                if (_publishCode)
                {
                    AppendLog("[2/4] 编译热更 DLL（HybridCLR CompileDll）...");
                    CompileDllForActivePlatform();
                    AppendLog("      编译完成，复制热更程序集...");
                    dllArtifacts = CopyHotUpdateDlls();
                    foreach (var artifact in dllArtifacts)
                        AppendLog($"      {artifact.FileName} 大小={artifact.Size}B  MD5={artifact.Md5}");
                }

                // 4. 生成 version.json
                AppendLog("[3/4] 生成 version.json...");
                string json = BuildVersionJson(
                    newResource, newCode, dllArtifacts);

                // 写入 ServerData/Updates
                Directory.CreateDirectory(ServerDataDir);
                File.WriteAllText(Path.Combine(ServerDataDir, "version.json"),
                    json, System.Text.Encoding.UTF8);
                AppendLog($"      写入 → {ServerDataDir}");

                // 写入 IIS 输出目录
                if (!string.IsNullOrEmpty(_versionOutputDir))
                {
                    Directory.CreateDirectory(_versionOutputDir);
                    File.WriteAllText(
                        Path.Combine(_versionOutputDir, "version.json"),
                        json, System.Text.Encoding.UTF8);
                    AppendLog($"      写入 → {_versionOutputDir}");
                }

                // 5. 更新内存版本号
                AppendLog("[4/4] 更新本地版本记录...");
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

        // ─── Addressables Build ────────────────────────────────
        private static void BuildAddressables()
        {
            var settings = AddressableAssetSettingsDefaultObject.Settings;
            if (settings == null)
                throw new Exception("未找到 Addressables Settings，请先执行 Framework/Setup Addressables");

            AddressableAssetSettings.BuildPlayerContent(out AddressablesPlayerBuildResult result);

            if (!string.IsNullOrEmpty(result.Error))
                throw new Exception($"Addressables Build 失败：{result.Error}");
        }

        // ─── 同步 bundle 到目标目录 ────────────────────────────
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

        private static void SyncBundles(string destDir)
        {
            string srcDir = Path.Combine(
                Directory.GetParent(Application.dataPath).FullName,
                "ServerData", GetBuildTargetName());

            if (!Directory.Exists(srcDir))
                throw new Exception($"ServerData 源目录不存在：{srcDir}");

            Directory.CreateDirectory(destDir);
            foreach (string file in Directory.GetFiles(srcDir))
            {
                string dest = Path.Combine(destDir, Path.GetFileName(file));
                File.Copy(file, dest, overwrite: true);
            }
        }

        // ─── HybridCLR 编译热更 DLL ────────────────────────────
        private static void CompileDllForActivePlatform()
        {
            BuildTarget target = EditorUserBuildSettings.activeBuildTarget;
            CompileDllCommand.CompileDll(target);
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

        // ─── 复制热更程序集 DLL ────────────────────────────────
        /// <summary>
        /// 复制全部热更程序集到发布目录，并返回用于 version.json 的补丁清单。
        /// </summary>
        private List<HotUpdateDllArtifact> CopyHotUpdateDlls()
        {
            Directory.CreateDirectory(ServerDataDir);

            if (!string.IsNullOrEmpty(_versionOutputDir))
                Directory.CreateDirectory(_versionOutputDir);

            var artifacts = new List<HotUpdateDllArtifact>(VersionManager.HotUpdateAssemblyFileNames.Length);
            foreach (string destFileName in VersionManager.HotUpdateAssemblyFileNames)
            {
                string src = GetHotUpdateDllSrc(EditorUserBuildSettings.activeBuildTarget, destFileName);
                if (!File.Exists(src))
                    throw new Exception(
                        $"未找到热更程序集：{src}\n请先执行 HybridCLR/Generate/All");

                // 写到 ServerData/Updates/
                string destLocal = Path.Combine(ServerDataDir, destFileName);
                File.Copy(src, destLocal, overwrite: true);

                // 写到 IIS Updates 目录
                if (!string.IsNullOrEmpty(_versionOutputDir))
                    File.Copy(src, Path.Combine(_versionOutputDir, destFileName), overwrite: true);

                long size = new FileInfo(destLocal).Length;
                string md5 = ComputeMD5(destLocal);
                artifacts.Add(new HotUpdateDllArtifact(destFileName, size, md5));
            }

            return artifacts;
        }

        // ─── 生成 version.json 字符串 ──────────────────────────
        private string BuildVersionJson(
            int newResource, int newCode,
            IReadOnlyList<HotUpdateDllArtifact> dllArtifacts)
        {
            // 构建 PatchFiles 数组（仅代码更新时有条目）
            string patchFiles = "[]";
            if (_publishCode && dllArtifacts != null && dllArtifacts.Count > 0)
                patchFiles = BuildPatchFilesJson(dllArtifacts);

            string desc = EscapeJsonString(_description);

            return
$@"{{
  ""AppVersion"":          ""{_appVersion}"",
  ""ResourceVersion"":     {newResource},
  ""CodeVersion"":         {newCode},
  ""ForceUpdate"":         {(_forceUpdate ? "true" : "false")},
  ""MinCompatibleVersion"":""{_minCompatible}"",
  ""Description"":         ""{desc}"",
  ""PatchFiles"":          {patchFiles}
}}";
        }

        /// <summary>
        /// 根据热更程序集产物构建 PatchFiles JSON 数组。
        /// </summary>
        private static string BuildPatchFilesJson(IReadOnlyList<HotUpdateDllArtifact> dllArtifacts)
        {
            var sb = new StringBuilder();
            sb.Append("[\n");
            for (int i = 0; i < dllArtifacts.Count; i++)
            {
                var artifact = dllArtifacts[i];
                string dllUrl = $"http://127.0.0.1:80/Updates/{artifact.FileName}";
                sb.Append("    {\n");
                sb.Append("      \"FileName\": \"").Append(EscapeJsonString(artifact.FileName)).Append("\",\n");
                sb.Append("      \"Url\":      \"").Append(EscapeJsonString(dllUrl)).Append("\",\n");
                sb.Append("      \"Size\":     ").Append(artifact.Size).Append(",\n");
                sb.Append("      \"MD5\":      \"").Append(EscapeJsonString(artifact.Md5)).Append("\"\n");
                sb.Append("    }");
                if (i < dllArtifacts.Count - 1)
                    sb.Append(",");
                sb.Append("\n");
            }
            sb.Append("  ]");
            return sb.ToString();
        }

        /// <summary>
        /// 转义写入 version.json 的字符串字段。
        /// </summary>
        private static string EscapeJsonString(string value)
        {
            return (value ?? string.Empty)
                .Replace("\\", "\\\\")
                .Replace("\"", "\\\"")
                .Replace("\r", "\\r")
                .Replace("\n", "\\n");
        }

        // ─── 工具方法 ──────────────────────────────────────────
        private static string ComputeMD5(string filePath)
        {
            using var md5 = MD5.Create();
            using var stream = File.OpenRead(filePath);
            byte[] hash = md5.ComputeHash(stream);
            return BitConverter.ToString(hash).Replace("-", "").ToLowerInvariant();
        }

        private static string GetBuildTargetName()
        {
            return GetBuildTargetName(EditorUserBuildSettings.activeBuildTarget);
        }

        /// <summary>
        /// 将 Unity 构建目标转换为 HybridCLRData 使用的平台目录名。
        /// </summary>
        /// <param name="target">Unity 构建目标。</param>
        /// <returns>HybridCLRData 下的平台目录名。</returns>
        private static string GetBuildTargetName(BuildTarget target)
        {
            return target switch
            {
                BuildTarget.StandaloneWindows   => "StandaloneWindows",
                BuildTarget.StandaloneWindows64 => "StandaloneWindows64",
                BuildTarget.Android             => "Android",
                BuildTarget.iOS                 => "iOS",
                _                               => target.ToString()
            };
        }

        private void AppendLog(string msg)
        {
            _log += $"[{DateTime.Now:HH:mm:ss}] {msg}\n";
            Repaint();
        }

        // 用于读取 version.json 的简化结构
        [Serializable]
        private class VersionSnapshot
        {
            // 字段由 JsonUtility.FromJson 反射赋值，编译器无法静态识别，故局部关闭 CS0649（字段从未赋值）误报。
#pragma warning disable CS0649
            public string AppVersion;
            public int    ResourceVersion;
            public int    CodeVersion;
            public string MinCompatibleVersion;
#pragma warning restore CS0649
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
