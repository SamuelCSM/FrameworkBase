using System;
using Framework.Storage;

namespace Framework.HotUpdate
{
    /// <summary>
    /// 热更侧的 <see cref="IContentCacheCleaner"/> 实现：把通用清理编排转交
    /// <see cref="HotUpdateSlotManager.CleanupCache"/>，并在删除时把「提交进行中」信号传下去，
    /// 保证事务槽（Active/Pending/LKG/提交中）始终受保护。
    /// </summary>
    internal sealed class HotUpdateCacheCleaner : IContentCacheCleaner
    {
        private readonly CacheRetentionPolicy _policy;
        private readonly Func<bool> _isContentCommitInProgress;

        public HotUpdateCacheCleaner(
            CacheRetentionPolicy policy = null,
            Func<bool> isContentCommitInProgress = null)
        {
            _policy = policy ?? new CacheRetentionPolicy();
            _isContentCommitInProgress = isContentCommitInProgress;
        }

        public CacheCleanupReport Cleanup(CacheCleanupRequest request) =>
            HotUpdateSlotManager.CleanupCache(
                request,
                _policy,
                _isContentCommitInProgress?.Invoke() == true);
    }
}
