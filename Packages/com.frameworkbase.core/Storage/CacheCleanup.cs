using System;
using System.Collections.Generic;
using System.Linq;

namespace Framework.Storage
{
    /// <summary>
    /// 缓存条目类别。枚举值同时是清理优先级：值越小越先删（Temporary 最先，Diagnostic 最后），
    /// 使清理顺序完全确定、可预期。
    /// </summary>
    public enum CacheEntryKind
    {
        /// <summary>临时文件（下载中间产物等），最优先回收。</summary>
        Temporary = 0,
        /// <summary>无主暂存目录（未提交为正式槽的 staging 残留）。</summary>
        OrphanStaging = 1,
        /// <summary>已过期的历史版本目录（非 Active/Pending/LKG）。</summary>
        ObsoleteRelease = 2,
        /// <summary>诊断/日志类文件，最后才回收。</summary>
        Diagnostic = 3
    }

    /// <summary>可清理缓存条目。受保护条目参与容量统计，但永远不得进入删除计划。</summary>
    public sealed class CacheEntry
    {
        public string Path { get; set; }
        public long SizeBytes { get; set; }
        public DateTime LastWriteUtc { get; set; }
        public CacheEntryKind Kind { get; set; }
        public bool IsProtected { get; set; }
    }

    /// <summary>
    /// 缓存保留策略：配额上限与高/低水位。缓存超过「上限×高水位」触发清理，目标回落到「上限×低水位」，
    /// 用滞后区间避免在阈值附近反复抖动清理。
    /// </summary>
    public sealed class CacheRetentionPolicy
    {
        /// <summary>缓存配额上限（字节）。默认 512 MiB。</summary>
        public long MaxCacheBytes { get; set; } = 512L * 1024L * 1024L;

        /// <summary>触发清理的高水位比例（相对上限）。默认 0.90。</summary>
        public double HighWatermarkRatio { get; set; } = 0.90d;

        /// <summary>清理的目标回落比例（相对上限），必须小于高水位。默认 0.70。</summary>
        public double LowWatermarkRatio { get; set; } = 0.70d;

        internal void Validate()
        {
            if (MaxCacheBytes <= 0)
                throw new InvalidOperationException("缓存上限必须大于 0。");
            if (LowWatermarkRatio < 0 || HighWatermarkRatio <= 0 ||
                LowWatermarkRatio >= HighWatermarkRatio || HighWatermarkRatio > 1)
                throw new InvalidOperationException("缓存水位必须满足 0 <= low < high <= 1。");
        }
    }

    /// <summary>
    /// 一次清理请求的输入快照：当前缓存占用、本次安装所需自由空间、卷现有自由空间。
    /// 规划器据此同时评估「磁盘缺口」与「缓存高水位」两个触发条件。
    /// </summary>
    public readonly struct CacheCleanupRequest
    {
        public CacheCleanupRequest(long currentCacheBytes, long requiredFreeBytes, long availableFreeBytes)
        {
            CurrentCacheBytes = Math.Max(0, currentCacheBytes);
            RequiredFreeBytes = Math.Max(0, requiredFreeBytes);
            AvailableFreeBytes = Math.Max(0, availableFreeBytes);
        }

        public long CurrentCacheBytes { get; }
        public long RequiredFreeBytes { get; }
        public long AvailableFreeBytes { get; }
    }

    /// <summary>清理计划：目标释放量与按确定顺序选出的待删条目。仅规划、不执行删除，便于测试与审计。</summary>
    public sealed class CacheCleanupPlan
    {
        internal CacheCleanupPlan(long targetBytes, IReadOnlyList<CacheEntry> entries)
        {
            TargetBytes = targetBytes;
            Entries = entries;
            PlannedBytes = entries.Sum(entry => Math.Max(0, entry.SizeBytes));
        }

        public long TargetBytes { get; }
        public long PlannedBytes { get; }
        public IReadOnlyList<CacheEntry> Entries { get; }
    }

    /// <summary>清理执行后的实际结果：请求释放量、真实释放量、成功与失败条目数。失败条目不阻断流程，仅上报。</summary>
    public readonly struct CacheCleanupReport
    {
        public CacheCleanupReport(long requestedBytes, long freedBytes, int deletedEntries, int failedEntries)
        {
            RequestedBytes = requestedBytes;
            FreedBytes = freedBytes;
            DeletedEntries = deletedEntries;
            FailedEntries = failedEntries;
        }

        public long RequestedBytes { get; }
        public long FreedBytes { get; }
        public int DeletedEntries { get; }
        public int FailedEntries { get; }
    }

    /// <summary>
    /// 内容缓存清理器抽象。由具体子系统（如热更槽管理）实现「枚举候选 → 保护事务槽 → 规划 → 删除」，
    /// 让空间准入编排（<see cref="StorageAdmission"/>）与具体缓存布局解耦、可注入测试替身。
    /// </summary>
    public interface IContentCacheCleaner
    {
        CacheCleanupReport Cleanup(CacheCleanupRequest request);
    }

    /// <summary>高低水位 + 低磁盘缺口双触发的确定性缓存删除规划器。</summary>
    public static class CacheCleanupPlanner
    {
        public static CacheCleanupPlan CreatePlan(
            CacheCleanupRequest request,
            IEnumerable<CacheEntry> candidates,
            CacheRetentionPolicy policy = null)
        {
            if (candidates == null) throw new ArgumentNullException(nameof(candidates));
            policy ??= new CacheRetentionPolicy();
            policy.Validate();

            long shortage = Math.Max(0, request.RequiredFreeBytes - request.AvailableFreeBytes);
            long high = (long)Math.Floor(policy.MaxCacheBytes * policy.HighWatermarkRatio);
            long low = (long)Math.Floor(policy.MaxCacheBytes * policy.LowWatermarkRatio);
            long quotaReduction = request.CurrentCacheBytes > high
                ? Math.Max(0, request.CurrentCacheBytes - low)
                : 0;
            long target = Math.Max(shortage, quotaReduction);

            if (target <= 0)
                return new CacheCleanupPlan(0, Array.Empty<CacheEntry>());

            var ordered = candidates
                .Where(entry => entry != null && !entry.IsProtected && entry.SizeBytes > 0 && !string.IsNullOrWhiteSpace(entry.Path))
                .OrderBy(entry => entry.Kind)
                .ThenBy(entry => entry.LastWriteUtc)
                .ThenBy(entry => entry.Path, StringComparer.Ordinal)
                .ToList();

            var selected = new List<CacheEntry>();
            long planned = 0;
            for (int i = 0; i < ordered.Count && planned < target; i++)
            {
                selected.Add(ordered[i]);
                planned = SaturatingAdd(planned, ordered[i].SizeBytes);
            }
            return new CacheCleanupPlan(target, selected);
        }

        private static long SaturatingAdd(long left, long right) =>
            left > long.MaxValue - right ? long.MaxValue : left + right;
    }

    /// <summary>空间门禁编排：仅在明确不足时触发一次安全清理，随后必须重新查询真实卷空间。</summary>
    public static class StorageAdmission
    {
        public static StoragePreflightResult Ensure(
            IStorageCapacityProvider capacityProvider,
            IContentCacheCleaner cacheCleaner,
            string targetPath,
            long payloadBytes,
            StorageBudgetPolicy budgetPolicy = null)
        {
            StoragePreflightResult first = StoragePreflight.Check(
                capacityProvider, targetPath, payloadBytes, budgetPolicy);

            // Unknown 时失败关闭且不做破坏性清理；空间可查询时同时执行“低磁盘缺口”与
            // “缓存高水位”策略。即使首次容量充足，也不能跳过长期运营所需的配额治理。
            if (first.Status == StorageCapacityStatus.Unknown || cacheCleaner == null)
                return first;

            cacheCleaner.Cleanup(new CacheCleanupRequest(
                currentCacheBytes: 0,
                requiredFreeBytes: first.RequiredBytes,
                availableFreeBytes: first.AvailableBytes));

            // 不能相信“计划释放量”，必须以删除完成后的真实卷查询作为最终准入事实。
            return StoragePreflight.Check(capacityProvider, targetPath, payloadBytes, budgetPolicy);
        }
    }
}
