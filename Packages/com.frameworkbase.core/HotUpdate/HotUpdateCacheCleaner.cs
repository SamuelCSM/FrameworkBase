using System;
using Framework.Storage;

namespace Framework.HotUpdate
{
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
