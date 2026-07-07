using System.Collections.Generic;
using System.IO;
using UnityEngine;
using UnityEditor;
using UnityEditor.AddressableAssets;
using UnityEditor.AddressableAssets.Settings;
using UnityEditor.AddressableAssets.Settings.GroupSchemas;

namespace Framework.Editor
{
    // ════════════════════════════════════════════════════════════════
    // 共享工具（内部使用，对外不暴露）
    // ════════════════════════════════════════════════════════════════
    internal static class AddrHelper
    {
        // ── 唯一需要配置的常量 ────────────────────────────────────
        internal const string Root  = "Assets/ResourcesOut";
        internal const string Label = "remote";

        internal static readonly HashSet<string> SupportedExt = new HashSet<string>
        {
            ".prefab", ".unity", ".mat",  ".png",  ".jpg", ".jpeg",
            ".wav",    ".mp3",   ".ogg",  ".anim", ".controller",
            ".asset",  ".fbx",   ".obj", ".bytes",
        };

        /// <summary>AssetPath → 无扩展名相对地址。不在 Root 下返回 null。</summary>
        internal static string Address(string assetPath)
        {
            string prefix = Root + "/";
            if (!assetPath.StartsWith(prefix)) return null;
            string rel = assetPath.Substring(prefix.Length);
            string ext = Path.GetExtension(rel);
            return string.IsNullOrEmpty(ext) ? rel : rel.Substring(0, rel.Length - ext.Length);
        }

        /// <summary>一级子目录名 = 分组名。直属根目录的文件返回 null。</summary>
        internal static string GroupName(string assetPath)
        {
            string prefix = Root + "/";
            if (!assetPath.StartsWith(prefix)) return null;
            string rel   = assetPath.Substring(prefix.Length);
            int    slash = rel.IndexOf('/');
            return slash >= 0 ? rel.Substring(0, slash) : null;
        }

        /// <summary>获取或自动创建远端分组（目录名即分组名，无需预先声明）。</summary>
        internal static AddressableAssetGroup GetOrCreateRemoteGroup(
            AddressableAssetSettings settings, string groupName)
        {
            var group = settings.FindGroup(groupName);
            if (group != null) return group;

            group = settings.CreateGroup(groupName, false, false, true, null);
            var s = group.AddSchema<BundledAssetGroupSchema>();
            s.BuildPath.SetVariableByName(settings, AddressableAssetSettings.kRemoteBuildPath);
            s.LoadPath.SetVariableByName(settings, AddressableAssetSettings.kRemoteLoadPath);
            s.BundleMode = BundledAssetGroupSchema.BundlePackingMode.PackTogether;
            group.AddSchema<ContentUpdateGroupSchema>();
            Debug.Log($"[Addressables] 自动创建远端分组 [{groupName}]");
            return group;
        }
    }

    // ════════════════════════════════════════════════════════════════
    /// <summary>
    /// Addressables 管理工具（菜单：Framework →）
    ///
    /// 约定：Assets/ResourcesOut/[GroupName]/路径/文件.ext
    ///         → 分组 GroupName，地址 GroupName/路径/文件（无扩展名）
    ///   新增目录 = 自动创建同名分组，无需修改任何代码。
    ///
    /// 菜单说明：
    ///   Register Assets (Sync)   日常使用：全量同步 ResourcesOut 到 Addressables
    ///   Validate Addressables    排查问题：打印所有分组与条目，检查地址规范
    /// </summary>
    // ════════════════════════════════════════════════════════════════
    public static class AddressablesSetup
    {
        private const string HotUpdateRemoteProfileName = "HotUpdateRemote";
        private const string FullPackageLocalProfileName = "FullPackageLocal";

        // ─── 全量同步（主要工具） ─────────────────────────────────
        /// <summary>
        /// 幂等操作：扫描 ResourcesOut，使 Addressables 与磁盘完全一致。
        ///   • 新文件       → 注册为独立条目，分组 = 一级目录名（按需自动创建）
        ///   • 地址不规范   → 自动修正为无扩展名相对路径
        ///   • 文件已删除   → 移除对应条目
        /// 初次使用、批量导入、或自动注册失效时执行。
        /// </summary>
        [MenuItem("Framework/Register Assets (Sync ResourcesOut)")]
        public static void RegisterAssets()
        {
            var settings = GetOrInitSettings();
            if (settings == null) return;

            int added = 0, updated = 0, removed = 0, skipped = 0;

            // ── Step 1：收集磁盘上所有受支持的资产 ───────────────
            var toRegister = new Dictionary<string, (string groupName, string address)>();
            foreach (string guid in AssetDatabase.FindAssets("", new[] { AddrHelper.Root }))
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                if (AssetDatabase.IsValidFolder(path)) continue;

                string ext = Path.GetExtension(path).ToLowerInvariant();
                if (!AddrHelper.SupportedExt.Contains(ext)) continue;

                string address   = AddrHelper.Address(path);
                string groupName = AddrHelper.GroupName(path);
                if (address == null || groupName == null) continue;

                toRegister[guid] = (groupName, address);
            }

            // ── Step 2：移除已不存在文件所对应的旧条目 ───────────
            var managed = new HashSet<string> { "Framework" };
            foreach (var v in toRegister.Values) managed.Add(v.groupName);

            foreach (var group in settings.groups)
            {
                if (group == null || !managed.Contains(group.Name)) continue;
                var stale = new List<AddressableAssetEntry>();
                foreach (var entry in group.entries)
                    if (!toRegister.ContainsKey(entry.guid)) stale.Add(entry);
                foreach (var entry in stale)
                {
                    Debug.Log($"[Addressables] 移除 [{group.Name}] {entry.address}");
                    settings.RemoveAssetEntry(entry.guid);
                    removed++;
                }
            }

            // ── Step 3：注册或修正 ────────────────────────────────
            foreach (var kv in toRegister)
            {
                string guid      = kv.Key;
                string groupName = kv.Value.groupName;
                string address   = kv.Value.address;

                var group    = AddrHelper.GetOrCreateRemoteGroup(settings, groupName);
                var existing = settings.FindAssetEntry(guid);

                if (existing == null)
                {
                    var entry = settings.CreateOrMoveEntry(guid, group, false, false);
                    entry.address = address;
                    entry.SetLabel(AddrHelper.Label, true, true, false);
                    Debug.Log($"[Addressables] 注册 [{groupName}] {address}");
                    added++;
                }
                else if (existing.address != address)
                {
                    Debug.Log($"[Addressables] 修正 [{groupName}] {existing.address}  →  {address}");
                    existing.address = address;
                    updated++;
                }
                else
                {
                    skipped++;
                }
            }

            EditorUtility.SetDirty(settings);
            AssetDatabase.SaveAssets();
            Debug.Log($"[Addressables] 同步完成  新增:{added}  修正:{updated}  移除:{removed}  跳过:{skipped}");
        }

        // ─── 验证（排查工具） ─────────────────────────────────────
        /// <summary>
        /// 深度校验入口（历史菜单保留，行为升级为完整规则引擎）：
        /// 地址规范、remote/local 路径、label、场景混包、隐式依赖重复打包、
        /// 体积阈值、同步漂移。规则说明见 Resource/ADDRESSABLES_GUIDE.md。
        /// </summary>
        [MenuItem("Framework/Validate Addressables")]
        public static void ValidateAddressables()
        {
            AddressablesValidator.RunAndReport();
        }

        // ─── Profile 方案（A 方案：整包本地化） ───────────────────────
        public static void SetupBuildProfiles()
        {
            var settings = GetOrInitSettings();
            if (settings == null) return;

            var profileSettings = settings.profileSettings;
            string baseProfileId = settings.activeProfileId;

            string hotUpdateProfileId = EnsureProfile(profileSettings, HotUpdateRemoteProfileName, baseProfileId);
            string fullPackageProfileId = EnsureProfile(profileSettings, FullPackageLocalProfileName, baseProfileId);

            // FullPackageLocal：让所有使用 Remote 变量的分组在整包模式下变为“随包本地加载”
            profileSettings.SetValue(
                fullPackageProfileId,
                AddressableAssetSettings.kRemoteBuildPath,
                $"[{AddressableAssetSettings.kLocalBuildPath}]");
            profileSettings.SetValue(
                fullPackageProfileId,
                AddressableAssetSettings.kRemoteLoadPath,
                $"[{AddressableAssetSettings.kLocalLoadPath}]");

            EditorUtility.SetDirty(settings);
            AssetDatabase.SaveAssets();

            Debug.Log(
                "[Addressables] Profile 初始化完成：\n" +
                "  - HotUpdateRemote（日常热更）\n" +
                "  - FullPackageLocal（整包内置全部 remote 资源）\n" +
                "整包构建前切 FullPackageLocal，构建后切回 HotUpdateRemote。");
        }

        public static void SwitchToHotUpdateRemote()
        {
            SwitchActiveProfile(HotUpdateRemoteProfileName);
        }

        public static void SwitchToFullPackageLocal()
        {
            SwitchActiveProfile(FullPackageLocalProfileName);
        }

        public static void PrepareFullPackage()
        {
            SetupBuildProfiles();
            SwitchToFullPackageLocal();

            var settings = AddressableAssetSettingsDefaultObject.Settings;
            if (settings == null)
            {
                Debug.LogError("[Addressables] Prepare Full Package 失败：Addressables Settings 不存在");
                return;
            }

            // 构建 Addressables 前先过深度校验：Error 级问题（路径错配/场景混包等）直接终止，
            // 与玩家构建的 AddressablesBuildCheck 门禁保持同一标准。
            if (!AddressablesValidator.ValidateForBuild(out string errorSummary))
            {
                Debug.LogError($"[Addressables] Prepare Full Package 终止：{errorSummary}");
                throw new System.InvalidOperationException(errorSummary);
            }

            try
            {
                AddressableAssetSettings.BuildPlayerContent();
                Debug.Log("[Addressables] Prepare Full Package 完成：已 Setup Profiles + 切换 FullPackageLocal + Build Addressables");
            }
            catch (System.Exception ex)
            {
                Debug.LogError($"[Addressables] Prepare Full Package 失败：{ex.Message}");
                throw;
            }
        }

        private static string EnsureProfile(
            AddressableAssetProfileSettings profileSettings,
            string profileName,
            string baseProfileId)
        {
            string profileId = profileSettings.GetProfileId(profileName);
            if (!string.IsNullOrEmpty(profileId))
                return profileId;

            profileId = profileSettings.AddProfile(profileName, baseProfileId);
            Debug.Log($"[Addressables] 创建 Profile: {profileName}");
            return profileId;
        }

        private static void SwitchActiveProfile(string profileName)
        {
            var settings = AddressableAssetSettingsDefaultObject.Settings;
            if (settings == null)
            {
                Debug.LogWarning("[Addressables] Settings 不存在，请先执行 Setup Profiles");
                return;
            }

            string profileId = settings.profileSettings.GetProfileId(profileName);
            if (string.IsNullOrEmpty(profileId))
            {
                Debug.LogWarning($"[Addressables] 未找到 Profile: {profileName}，请先执行 Setup Profiles");
                return;
            }

            settings.activeProfileId = profileId;
            EditorUtility.SetDirty(settings);
            AssetDatabase.SaveAssets();
            Debug.Log($"[Addressables] 已切换 Active Profile -> {profileName}");
        }

        // ─── 内部：初始化 Settings 并确保 Framework 分组存在 ──────
        private static AddressableAssetSettings GetOrInitSettings()
        {
            var settings = AddressableAssetSettingsDefaultObject.Settings;
            if (settings == null)
            {
                settings = AddressableAssetSettings.Create(
                    AddressableAssetSettingsDefaultObject.kDefaultConfigFolder,
                    AddressableAssetSettingsDefaultObject.kDefaultConfigAssetName,
                    true, true);
                Debug.Log("[Addressables] Settings 初始化完成");
            }

            // 确保 Framework 本地分组存在（唯一的特殊分组，随包内置）
            if (settings.FindGroup("Framework") == null)
            {
                var g = settings.CreateGroup("Framework", false, false, true, null);
                var s = g.AddSchema<BundledAssetGroupSchema>();
                s.BuildPath.SetVariableByName(settings, AddressableAssetSettings.kLocalBuildPath);
                s.LoadPath.SetVariableByName(settings, AddressableAssetSettings.kLocalLoadPath);
                s.BundleMode = BundledAssetGroupSchema.BundlePackingMode.PackTogether;
                g.AddSchema<ContentUpdateGroupSchema>();
                Debug.Log("[Addressables] 创建本地分组 [Framework]");
            }

            return settings;
        }
    }

    // ════════════════════════════════════════════════════════════════
    /// <summary>
    /// 资产导入自动注册（AssetPostprocessor，无菜单，自动触发）
    ///
    /// 文件放入 Assets/ResourcesOut/ 子目录 → Unity 导入时自动：
    ///   ① 按一级目录名获取（或创建）分组
    ///   ② 注册为独立条目，地址 = 无扩展名相对路径
    ///   ③ 打上 remote label
    /// 无需手动勾选 Addressable，无需手动跑菜单。
    /// </summary>
    // ════════════════════════════════════════════════════════════════
    public class AddressableAutoRegistrar : AssetPostprocessor
    {
        private static void OnPostprocessAllAssets(
            string[] importedAssets, string[] deletedAssets,
            string[] movedAssets,    string[] movedFromAssetPaths)
        {
            var settings = AddressableAssetSettingsDefaultObject.Settings;
            if (settings == null) return;

            bool dirty = false;
            foreach (string path in importedAssets) dirty |= TryRegister(settings, path);
            foreach (string path in movedAssets)    dirty |= TryRegister(settings, path);

            if (dirty) { EditorUtility.SetDirty(settings); AssetDatabase.SaveAssets(); }
        }

        private static bool TryRegister(AddressableAssetSettings settings, string assetPath)
        {
            if (AssetDatabase.IsValidFolder(assetPath)) return false;

            string ext = Path.GetExtension(assetPath).ToLowerInvariant();
            if (!AddrHelper.SupportedExt.Contains(ext)) return false;

            string address   = AddrHelper.Address(assetPath);
            string groupName = AddrHelper.GroupName(assetPath);
            if (address == null || groupName == null) return false;

            string guid = AssetDatabase.AssetPathToGUID(assetPath);
            if (string.IsNullOrEmpty(guid)) return false;

            var group    = AddrHelper.GetOrCreateRemoteGroup(settings, groupName);
            var existing = settings.FindAssetEntry(guid);

            if (existing == null)
            {
                var entry = settings.CreateOrMoveEntry(guid, group, false, false);
                entry.address = address;
                entry.SetLabel(AddrHelper.Label, true, true, false);
                Debug.Log($"[AutoRegister] 注册 [{groupName}] {address}");
                return true;
            }

            if (existing.address != address)
            {
                Debug.Log($"[AutoRegister] 修正 {existing.address}  →  {address}");
                existing.address = address;
                return true;
            }

            return false;
        }
    }
}
