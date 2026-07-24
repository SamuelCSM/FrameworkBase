using System;
using System.Collections.Generic;
using System.Linq;

namespace Framework.Editor
{
    /// <summary>问题严重级：Error 构建门禁拦截；Warning 打印提示不拦截。</summary>
    public enum AddressablesIssueSeverity
    {
        Warning,
        Error,
    }

    /// <summary>单条校验问题（规则名 + 严重级 + 可读消息）。</summary>
    public sealed class AddressablesValidationIssue
    {
        public AddressablesIssueSeverity Severity;
        public string Rule;
        public string Message;

        public override string ToString() => $"[{Severity}] {Rule}: {Message}";
    }

    /// <summary>条目模型（采集层从 AddressableAssetEntry 填充；测试直接构造）。</summary>
    public sealed class AddressablesEntryModel
    {
        public string Guid;
        public string AssetPath;
        public string Address;

        /// <summary>按目录约定推导的规范地址；null 表示不适用（不在受管目录下，如 Framework 内置资源）。</summary>
        public string ExpectedAddress;

        /// <summary>源资产磁盘体积（构建前代理指标，非最终 bundle 体积）。</summary>
        public long SizeBytes;

        public bool HasRemoteLabel;
        public bool IsScene;
    }

    /// <summary>
    /// Bundle 打包模式（框架侧枚举，与 Addressables API 解耦——采集层映射，规则层只吃这个）。
    /// Unknown 表示未采集/不适用，规则一律跳过，故不会误触未设该字段的合成模型（如单测）。
    /// </summary>
    public enum BundlePackingKind
    {
        Unknown = 0,
        PackTogether,
        PackSeparately,
        PackTogetherByLabel,
    }

    /// <summary>
    /// Bundle 命名风格（框架侧枚举）。只有 AppendHash / OnlyHash 嵌的是**内容**哈希，
    /// 内容变则文件名变、CDN 按 URL 天然失效；NoHash 无哈希、FileNameHash 只是文件名哈希，都不随内容变。
    /// </summary>
    public enum BundleNamingKind
    {
        Unknown = 0,
        AppendHash,
        OnlyHash,
        FileNameHash,
        NoHash,
    }

    /// <summary>Bundle 压缩模式（框架侧枚举）。</summary>
    public enum BundleCompressionKind
    {
        Unknown = 0,
        Uncompressed,
        LZ4,
        LZMA,
    }

    /// <summary>分组模型。</summary>
    public sealed class AddressablesGroupModel
    {
        public string Name;

        /// <summary>是否挂了 BundledAssetGroupSchema（没有则该组根本不参与打包，属配置错误）。</summary>
        public bool HasBundledSchema = true;

        /// <summary>BuildPath / LoadPath 的 Profile 变量名（与阈值中的期望值比对）。</summary>
        public string BuildPathName = "";
        public string LoadPathName = "";

        /// <summary>Bundle 打包模式（默认 Unknown，规则跳过；采集层从 schema 填充）。</summary>
        public BundlePackingKind Packing = BundlePackingKind.Unknown;

        /// <summary>Bundle 命名风格（默认 Unknown，规则跳过；采集层从 schema 填充）。</summary>
        public BundleNamingKind Naming = BundleNamingKind.Unknown;

        /// <summary>Bundle 压缩模式（默认 Unknown，规则跳过；采集层从 schema 填充）。</summary>
        public BundleCompressionKind Compression = BundleCompressionKind.Unknown;

        public List<AddressablesEntryModel> Entries = new List<AddressablesEntryModel>();
    }

    /// <summary>
    /// 校验输入模型：与 AssetDatabase / Addressables API 完全解耦，
    /// 规则引擎只吃这个模型——采集层负责填充，单测直接手工构造。
    /// </summary>
    public sealed class AddressablesValidationModel
    {
        public List<AddressablesGroupModel> Groups = new List<AddressablesGroupModel>();

        /// <summary>条目资产路径 → 其（递归）依赖的资产路径列表。采集层已过滤脚本/asmdef 等非打包资产。</summary>
        public Dictionary<string, List<string>> Dependencies = new Dictionary<string, List<string>>();

        /// <summary>全部已显式注册为 Addressable 的资产路径（隐式依赖判定用）。</summary>
        public HashSet<string> AddressableAssetPaths = new HashSet<string>();

        /// <summary>受管目录（ResourcesOut）内存在于磁盘但未注册的资产路径（同步漂移）。</summary>
        public List<string> UnregisteredManagedAssets = new List<string>();

        /// <summary>资产体积查询（隐式依赖消息补充用；可为空）。</summary>
        public Dictionary<string, long> AssetSizes = new Dictionary<string, long>();
    }

    /// <summary>校验阈值与环境约定（默认值即框架规范，项目可在调用处覆盖）。</summary>
    public sealed class AddressablesValidationThresholds
    {
        /// <summary>单资产源体积告警阈值。移动端超过它的单资产（大图集/长音频）应考虑拆分或压缩。</summary>
        public long MaxSingleAssetBytes = 32L * 1024 * 1024;

        /// <summary>单组源资产总体积告警阈值。超过意味着该组更新粒度过粗，改一个资源玩家要重下一大包。</summary>
        public long MaxGroupSourceBytes = 256L * 1024 * 1024;

        /// <summary>
        /// 远端 PackTogether 组的条目数告警阈值。超过它 → 打成一个大 bundle，改任一资源都要重下整包
        /// （与按体积的组超限互补：多个小文件体积不大但粒度同样粗）。
        /// </summary>
        public int MaxPackTogetherRemoteEntries = 25;

        /// <summary>本地组白名单：仅这些组允许（且必须）走 Local 路径随包内置，其余一律 Remote。</summary>
        public HashSet<string> LocalGroups = new HashSet<string> { "Framework", "Default Local Group" };

        /// <summary>远端条目必须携带的下载 label（热更下载按它聚合）。</summary>
        public string RemoteLabel = "remote";

        // Profile 变量名期望值（采集层用 AddressableAssetSettings 常量填充，避免硬编码漂移）
        public string RemoteBuildPath = "Remote.BuildPath";
        public string RemoteLoadPath = "Remote.LoadPath";
        public string LocalBuildPath = "Local.BuildPath";
        public string LocalLoadPath = "Local.LoadPath";
    }

    /// <summary>
    /// Addressables 分组打包规则引擎（纯函数，不碰 AssetDatabase）。
    /// 规则清单与修复指引见 Resource/ADDRESSABLES_GUIDE.md。
    /// </summary>
    public static class AddressablesValidationRules
    {
        public static List<AddressablesValidationIssue> Validate(
            AddressablesValidationModel model,
            AddressablesValidationThresholds thresholds)
        {
            var issues = new List<AddressablesValidationIssue>();

            foreach (AddressablesGroupModel group in model.Groups)
                ValidateGroup(group, thresholds, issues);

            ValidateDuplicateImplicitDependencies(model, issues);
            ValidateUnregisteredManagedAssets(model, issues);

            return issues;
        }

        // ── 组级规则 ─────────────────────────────────────────────────────────

        private static void ValidateGroup(
            AddressablesGroupModel group,
            AddressablesValidationThresholds th,
            List<AddressablesValidationIssue> issues)
        {
            // Addressables 自动生成的 Built In Data 是引擎元数据组，不参与普通 Bundle 路径和 Schema 规则。
            if (string.Equals(group.Name, "Built In Data", StringComparison.Ordinal))
                return;

            // 规则 1：必须挂 BundledAssetGroupSchema，否则组内资源根本不会被打进任何 bundle
            if (!group.HasBundledSchema)
            {
                Add(issues, AddressablesIssueSeverity.Error, "GroupMissingSchema",
                    $"组 [{group.Name}] 缺少 BundledAssetGroupSchema，条目不会参与打包");
                return; // 后续路径/label 规则失去意义
            }

            // 规则 2：本地组白名单外一律 Remote；白名单内必须 Local。
            // 错配后果：本该热更的资源被焊死进包（发版才能改），或本地资源被误传 CDN。
            bool shouldBeLocal = th.LocalGroups.Contains(group.Name);
            if (shouldBeLocal)
            {
                if (group.BuildPathName != th.LocalBuildPath || group.LoadPathName != th.LocalLoadPath)
                    Add(issues, AddressablesIssueSeverity.Error, "LocalGroupWrongPath",
                        $"本地组 [{group.Name}] 路径错配：Build={group.BuildPathName} Load={group.LoadPathName}，" +
                        $"应为 {th.LocalBuildPath}/{th.LocalLoadPath}");
            }
            else
            {
                if (group.BuildPathName != th.RemoteBuildPath || group.LoadPathName != th.RemoteLoadPath)
                    Add(issues, AddressablesIssueSeverity.Error, "RemoteGroupWrongPath",
                        $"远端组 [{group.Name}] 路径错配：Build={group.BuildPathName} Load={group.LoadPathName}，" +
                        $"应为 {th.RemoteBuildPath}/{th.RemoteLoadPath}（本地组需加入白名单）");
            }

            // 规则 3：空组是配置垃圾，容易误导后续维护者
            if (group.Entries.Count == 0)
            {
                Add(issues, AddressablesIssueSeverity.Warning, "EmptyGroup",
                    $"组 [{group.Name}] 没有任何条目，建议删除");
                return;
            }

            // 规则 4：场景与非场景资产混包——Addressables 构建期会直接报错，这里提前拦
            bool hasScene = group.Entries.Any(e => e.IsScene);
            bool hasAsset = group.Entries.Any(e => !e.IsScene);
            if (hasScene && hasAsset)
            {
                Add(issues, AddressablesIssueSeverity.Error, "SceneMixedWithAssets",
                    $"组 [{group.Name}] 同时包含场景与普通资产（场景必须独立成组，否则 Addressables 构建失败）");
            }

            long groupTotal = 0;
            foreach (AddressablesEntryModel entry in group.Entries)
            {
                groupTotal += entry.SizeBytes;

                // 规则 5：地址必须符合目录推导规范（地址乱写 → 运行时字符串找不到资源）
                if (entry.ExpectedAddress != null && entry.Address != entry.ExpectedAddress)
                {
                    Add(issues, AddressablesIssueSeverity.Warning, "AddressMismatch",
                        $"[{group.Name}] {entry.AssetPath} 地址 \"{entry.Address}\" 不符合规范，" +
                        $"应为 \"{entry.ExpectedAddress}\"（执行 Register Assets (Sync) 自动修正）");
                }

                // 规则 6：远端组条目必须带下载 label，否则启动时 GetDownloadSizeAsync 统计不到它
                if (!shouldBeLocal && !entry.HasRemoteLabel)
                {
                    Add(issues, AddressablesIssueSeverity.Warning, "MissingRemoteLabel",
                        $"[{group.Name}] {entry.Address} 缺少 \"{th.RemoteLabel}\" label，" +
                        "热更下载阶段不会预下载它（首次使用时才边玩边下）");
                }

                // 规则 7：单资产超体积告警
                if (entry.SizeBytes > th.MaxSingleAssetBytes)
                {
                    Add(issues, AddressablesIssueSeverity.Warning, "AssetOverBudget",
                        $"[{group.Name}] {entry.AssetPath} 源体积 {FormatMb(entry.SizeBytes)}，" +
                        $"超过单资产阈值 {FormatMb(th.MaxSingleAssetBytes)}，考虑压缩或拆分");
                }
            }

            // 规则 8：组总体积超阈值 → 更新粒度过粗
            if (groupTotal > th.MaxGroupSourceBytes)
            {
                Add(issues, AddressablesIssueSeverity.Warning, "GroupOverBudget",
                    $"组 [{group.Name}] 源资产总体积 {FormatMb(groupTotal)} 超过阈值 {FormatMb(th.MaxGroupSourceBytes)}，" +
                    "建议按功能/更新频率拆分子目录成组，避免改一个资源玩家重下一大包");
            }

            // 规则 11~13：远端组 bundle 布局审计（本地组随包内置、不涉及 CDN/补丁粒度，跳过）
            if (!shouldBeLocal)
                ValidateRemoteBundleLayout(group, th, issues);
        }

        /// <summary>
        /// 远端组 bundle 布局审计（仅远端组）：命名内容哈希、压缩、打包粒度三项。
        /// 均为 Warning——布局是权衡项而非硬错，不阻断构建，与组超限/重复依赖同级只作咨询。
        /// 字段为 Unknown（未采集/合成模型）时逐条跳过，不误触。
        /// </summary>
        private static void ValidateRemoteBundleLayout(
            AddressablesGroupModel group,
            AddressablesValidationThresholds th,
            List<AddressablesValidationIssue> issues)
        {
            // 规则 11：远端 bundle 命名须嵌内容哈希，否则内容热更后文件名不变，
            // CDN/HTTP 边缘缓存可能按 URL 命中旧字节，把玩家卡在旧资源上（热更特有隐患，关联 ADR-005/009）。
            // 只有 AppendHash / OnlyHash 嵌内容哈希；NoHash 无哈希、FileNameHash 是文件名哈希都不随内容变。
            if (group.Naming == BundleNamingKind.NoHash || group.Naming == BundleNamingKind.FileNameHash)
            {
                Add(issues, AddressablesIssueSeverity.Warning, "RemoteBundleNamingNoContentHash",
                    $"远端组 [{group.Name}] Bundle 命名为 {group.Naming}，文件名不随内容变——" +
                    "内容热更后 CDN/缓存可能按 URL 供旧字节。改用 Append Hash / Use Hash of AssetBundle。");
            }

            // 规则 12：远端 bundle 未压缩 → 白白多传数倍字节，CDN 流量与玩家下载都受累。
            if (group.Compression == BundleCompressionKind.Uncompressed)
            {
                Add(issues, AddressablesIssueSeverity.Warning, "RemoteBundleUncompressed",
                    $"远端组 [{group.Name}] Bundle 未压缩，下载体积是 LZ4 的数倍。远端组建议 LZ4（按需解压、体积均衡）。");
            }

            // 规则 13：PackTogether + 条目多 → 改任一资源都要重下整包，补丁粒度过粗。
            // 与按体积的组超限（规则 8）互补：多个小文件体积不大但粒度同样粗。
            if (group.Packing == BundlePackingKind.PackTogether &&
                group.Entries.Count > th.MaxPackTogetherRemoteEntries)
            {
                Add(issues, AddressablesIssueSeverity.Warning, "CoarsePatchGranularity",
                    $"远端组 [{group.Name}] 打包模式 PackTogether 且含 {group.Entries.Count} 个条目" +
                    $"（阈值 {th.MaxPackTogetherRemoteEntries}）——改任一资源玩家需重下整包。" +
                    "按更新频率拆组或改 Pack Separately 细化补丁粒度。");
            }
        }

        // ── 跨组规则 ─────────────────────────────────────────────────────────

        /// <summary>
        /// 规则 9：隐式依赖重复打包检测——Addressables 最经典的包体/内存双倍坑。
        /// 一个未显式注册的资产（如共享贴图）被多个组的条目依赖时，会被分别拷贝进每个组的 bundle：
        /// 包体膨胀、运行时同资产双份内存、且两份互不相等（==/引用比较失效）。
        /// 修复：把该资产移入 ResourcesOut 下某个共享目录显式注册，让各组按引用共享同一 bundle。
        /// </summary>
        private static void ValidateDuplicateImplicitDependencies(
            AddressablesValidationModel model,
            List<AddressablesValidationIssue> issues)
        {
            // 依赖路径 → 依赖它的组名集合
            var owners = new Dictionary<string, HashSet<string>>();

            foreach (AddressablesGroupModel group in model.Groups)
            {
                foreach (AddressablesEntryModel entry in group.Entries)
                {
                    if (!model.Dependencies.TryGetValue(entry.AssetPath, out List<string> deps))
                        continue;

                    foreach (string dep in deps)
                    {
                        if (dep == entry.AssetPath)
                            continue;
                        if (model.AddressableAssetPaths.Contains(dep))
                            continue; // 显式注册的依赖走引用共享，不会重复

                        if (!owners.TryGetValue(dep, out HashSet<string> groups))
                        {
                            groups = new HashSet<string>();
                            owners[dep] = groups;
                        }
                        groups.Add(group.Name);
                    }
                }
            }

            foreach (KeyValuePair<string, HashSet<string>> pair in owners.Where(p => p.Value.Count >= 2)
                         .OrderByDescending(p => SizeOf(model, p.Key)))
            {
                long size = SizeOf(model, pair.Key);
                string sizeText = size > 0 ? $"（{FormatMb(size)}）" : "";
                Add(issues, AddressablesIssueSeverity.Warning, "DuplicateImplicitDependency",
                    $"隐式依赖 {pair.Key}{sizeText} 被 {pair.Value.Count} 个组重复打包：" +
                    $"{string.Join(", ", pair.Value.OrderBy(g => g))}——包体与内存双份，" +
                    "建议移入 ResourcesOut 共享目录显式注册");
            }
        }

        /// <summary>规则 10：受管目录内有资产未注册（磁盘与 Settings 漂移），汇总一条告警。</summary>
        private static void ValidateUnregisteredManagedAssets(
            AddressablesValidationModel model,
            List<AddressablesValidationIssue> issues)
        {
            if (model.UnregisteredManagedAssets.Count == 0)
                return;

            IEnumerable<string> examples = model.UnregisteredManagedAssets.Take(5);
            Add(issues, AddressablesIssueSeverity.Warning, "UnregisteredManagedAsset",
                $"受管目录内有 {model.UnregisteredManagedAssets.Count} 个资产未注册为 Addressable" +
                $"（如 {string.Join("; ", examples)}），执行 Register Assets (Sync) 同步");
        }

        // ── 工具 ─────────────────────────────────────────────────────────────

        private static long SizeOf(AddressablesValidationModel model, string path)
            => model.AssetSizes.TryGetValue(path, out long size) ? size : 0;

        private static string FormatMb(long bytes) => $"{bytes / 1024f / 1024f:0.##} MB";

        private static void Add(
            List<AddressablesValidationIssue> issues,
            AddressablesIssueSeverity severity,
            string rule,
            string message)
        {
            issues.Add(new AddressablesValidationIssue { Severity = severity, Rule = rule, Message = message });
        }
    }
}
