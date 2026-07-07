using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEditor.AddressableAssets;
using UnityEditor.AddressableAssets.Settings;
using UnityEditor.AddressableAssets.Settings.GroupSchemas;
using UnityEngine;

namespace Framework.Editor
{
    /// <summary>
    /// Addressables 深度校验器：从工程采集 <see cref="AddressablesValidationModel"/>，
    /// 交给纯规则引擎 <see cref="AddressablesValidationRules"/> 评估并输出报告。
    /// 触发方式：菜单 Framework → Validate Addressables (Deep)、
    /// 构建前置门禁 <see cref="AddressablesBuildCheck"/>、整包发布 PrepareFullPackage。
    /// 规则清单与修复指引见 Resource/ADDRESSABLES_GUIDE.md。
    /// </summary>
    public static class AddressablesValidator
    {
        /// <summary>依赖分析中忽略的扩展名（不参与 bundle 打包的资产类型）。</summary>
        private static readonly HashSet<string> IgnoredDependencyExt = new HashSet<string>
        {
            ".cs", ".asmdef", ".asmref", ".dll",
        };

        /// <summary>执行全量校验并打印报告。返回问题列表（Settings 不存在时返回空表并提示）。
        /// 菜单入口在 <see cref="AddressablesSetup.ValidateAddressables"/>（Framework → Validate Addressables）。</summary>
        public static List<AddressablesValidationIssue> RunAndReport()
        {
            var settings = AddressableAssetSettingsDefaultObject.Settings;
            if (settings == null)
            {
                Debug.LogWarning("[AddressablesValidator] Addressables Settings 不存在，跳过校验（先执行 Register Assets）");
                return new List<AddressablesValidationIssue>();
            }

            var thresholds = DefaultThresholds();
            AddressablesValidationModel model = BuildModelFromProject(settings, thresholds);
            List<AddressablesValidationIssue> issues = AddressablesValidationRules.Validate(model, thresholds);
            Report(issues);
            return issues;
        }

        /// <summary>
        /// 构建门禁入口：Error 级问题返回 false 并给出摘要（Warning 不拦截）。
        /// Settings 不存在视为通过（工程可能不使用 Addressables）。
        /// </summary>
        public static bool ValidateForBuild(out string errorSummary)
        {
            errorSummary = string.Empty;

            List<AddressablesValidationIssue> issues = RunAndReport();
            List<AddressablesValidationIssue> errors =
                issues.Where(i => i.Severity == AddressablesIssueSeverity.Error).ToList();
            if (errors.Count == 0)
                return true;

            var sb = new StringBuilder();
            sb.AppendLine($"Addressables 校验发现 {errors.Count} 个 Error：");
            foreach (AddressablesValidationIssue issue in errors)
                sb.AppendLine("  " + issue);
            errorSummary = sb.ToString();
            return false;
        }

        /// <summary>框架默认阈值（Profile 变量名取 Addressables 常量，避免硬编码漂移）。</summary>
        public static AddressablesValidationThresholds DefaultThresholds()
        {
            return new AddressablesValidationThresholds
            {
                RemoteBuildPath = AddressableAssetSettings.kRemoteBuildPath,
                RemoteLoadPath = AddressableAssetSettings.kRemoteLoadPath,
                LocalBuildPath = AddressableAssetSettings.kLocalBuildPath,
                LocalLoadPath = AddressableAssetSettings.kLocalLoadPath,
                RemoteLabel = AddrHelper.Label,
            };
        }

        // ── 采集层：工程 → 模型 ──────────────────────────────────────────────

        /// <summary>从 Addressables Settings 与 AssetDatabase 采集校验模型（不做任何判定）。</summary>
        public static AddressablesValidationModel BuildModelFromProject(
            AddressableAssetSettings settings,
            AddressablesValidationThresholds thresholds)
        {
            var model = new AddressablesValidationModel();

            foreach (AddressableAssetGroup group in settings.groups)
            {
                if (group == null)
                    continue;

                var groupModel = new AddressablesGroupModel { Name = group.Name };

                var schema = group.GetSchema<BundledAssetGroupSchema>();
                groupModel.HasBundledSchema = schema != null;
                if (schema != null)
                {
                    groupModel.BuildPathName = schema.BuildPath.GetName(settings) ?? string.Empty;
                    groupModel.LoadPathName = schema.LoadPath.GetName(settings) ?? string.Empty;
                }

                foreach (AddressableAssetEntry entry in group.entries)
                {
                    string path = entry.AssetPath;
                    var entryModel = new AddressablesEntryModel
                    {
                        Guid = entry.guid,
                        AssetPath = path,
                        Address = entry.address,
                        ExpectedAddress = AddrHelper.Address(path),
                        SizeBytes = FileSize(path),
                        HasRemoteLabel = entry.labels.Contains(thresholds.RemoteLabel),
                        IsScene = path.EndsWith(".unity", System.StringComparison.OrdinalIgnoreCase),
                    };
                    groupModel.Entries.Add(entryModel);
                    model.AddressableAssetPaths.Add(path);
                }

                model.Groups.Add(groupModel);
            }

            CollectDependencies(model);
            CollectUnregisteredManagedAssets(settings, model);
            return model;
        }

        /// <summary>递归采集每个条目的依赖（过滤脚本类与自身），并记录依赖体积供报告排序。</summary>
        private static void CollectDependencies(AddressablesValidationModel model)
        {
            foreach (AddressablesGroupModel group in model.Groups)
            {
                foreach (AddressablesEntryModel entry in group.Entries)
                {
                    if (string.IsNullOrEmpty(entry.AssetPath) || model.Dependencies.ContainsKey(entry.AssetPath))
                        continue;

                    var deps = new List<string>();
                    foreach (string dep in AssetDatabase.GetDependencies(entry.AssetPath, true))
                    {
                        if (dep == entry.AssetPath)
                            continue;
                        string ext = Path.GetExtension(dep).ToLowerInvariant();
                        if (IgnoredDependencyExt.Contains(ext))
                            continue;

                        deps.Add(dep);
                        if (!model.AssetSizes.ContainsKey(dep))
                            model.AssetSizes[dep] = FileSize(dep);
                    }
                    model.Dependencies[entry.AssetPath] = deps;
                }
            }
        }

        /// <summary>扫描受管目录（ResourcesOut），找出磁盘上有但未注册的资产（同步漂移）。</summary>
        private static void CollectUnregisteredManagedAssets(
            AddressableAssetSettings settings,
            AddressablesValidationModel model)
        {
            if (!AssetDatabase.IsValidFolder(AddrHelper.Root))
                return;

            foreach (string guid in AssetDatabase.FindAssets("", new[] { AddrHelper.Root }))
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                if (AssetDatabase.IsValidFolder(path))
                    continue;

                string ext = Path.GetExtension(path).ToLowerInvariant();
                if (!AddrHelper.SupportedExt.Contains(ext))
                    continue;

                if (settings.FindAssetEntry(guid) == null)
                    model.UnregisteredManagedAssets.Add(path);
            }
        }

        private static long FileSize(string assetPath)
        {
            try
            {
                string full = Path.GetFullPath(assetPath);
                return File.Exists(full) ? new FileInfo(full).Length : 0;
            }
            catch
            {
                return 0;
            }
        }

        // ── 报告输出 ─────────────────────────────────────────────────────────

        /// <summary>按严重级归类打印（Error → LogError，Warning → LogWarning，全通过打一条 Log）。</summary>
        public static void Report(List<AddressablesValidationIssue> issues)
        {
            int errors = issues.Count(i => i.Severity == AddressablesIssueSeverity.Error);
            int warnings = issues.Count - errors;

            Debug.Log($"══════════ Addressables 深度校验：{(issues.Count == 0 ? "通过 ✓" : $"{errors} Error / {warnings} Warning")} ══════════");
            foreach (AddressablesValidationIssue issue in issues)
            {
                if (issue.Severity == AddressablesIssueSeverity.Error)
                    Debug.LogError($"[AddressablesValidator] {issue}");
                else
                    Debug.LogWarning($"[AddressablesValidator] {issue}");
            }
        }
    }
}
