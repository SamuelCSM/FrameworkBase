using System;
using System.Collections.Generic;
using System.Linq;

namespace Framework.Storage
{
    public enum CacheEntryKind
    {
        Temporary = 0,
        OrphanStaging = 1,
        ObsoleteRelease = 2,
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

    public sealed class CacheRetentionPolicy
    {
        public long MaxCacheBytes { get; set; } = 512L * 1024L * 1024L;
        public double HighWatermarkRatio { get; set; } = 0.90d;
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
