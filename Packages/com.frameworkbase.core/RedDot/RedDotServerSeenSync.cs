using System;
using System.Collections.Generic;
using System.Threading;
using Cysharp.Threading.Tasks;
using Framework.Foundation;

namespace Framework.RedDot
{
    /// <summary>
    /// 服务端已看同步后端抽象。业务实现拉取/上报的具体协议、鉴权与重试；框架只负责
    /// "取 max 版本"的冲突合并与拉取/回推的编排时机，不在此处硬编码任何网络细节。
    /// </summary>
    public interface IRedDotSeenSyncBackend
    {
        /// <summary>拉取当前账号服务端已保存的 ServerAccount 已看版本。</summary>
        UniTask<IReadOnlyList<RedDotSeenRecord>> PullAsync(CancellationToken cancellationToken);

        /// <summary>上报当前账号的 ServerAccount 已看版本（服务端应按 max 版本合并入库）。</summary>
        UniTask PushAsync(IReadOnlyList<RedDotSeenRecord> records, CancellationToken cancellationToken);
    }

    /// <summary>
    /// ServerAccount 已看版本的同步编排：
    /// <list type="bullet">
    /// <item>登录：拉取服务端记录，与本地按 max 版本合并；本地领先时把合并结果回推。</item>
    /// <item>会话中：<see cref="RedDotService.ServerSeenChanged"/> 触发去抖上报（去抖为 0 时改由外部驱动）。</item>
    /// <item>登出：由业务在清账号态前捕获快照并 <see cref="PushSnapshotAsync"/> 最终回推。</item>
    /// </list>
    /// 冲突策略固定为"取 max 版本"，与服务端入库策略对齐——已看进度只增不减，天然幂等。
    /// </summary>
    public sealed class RedDotServerSeenSync : IDisposable
    {
        private readonly RedDotService _service;
        private readonly IRedDotSeenSyncBackend _backend;
        private readonly int _debounceMilliseconds;
        private readonly Action<Exception> _errorSink;

        private CancellationTokenSource _sessionCts;
        private CancellationTokenSource _debounceCts;
        private bool _hasPendingPush;
        private bool _subscribed;
        private bool _disposed;

        /// <param name="service">已初始化的红点服务。</param>
        /// <param name="backend">业务提供的拉取/上报后端。</param>
        /// <param name="debounceMilliseconds">会话内自动上报去抖窗口；小于等于 0 表示不自动上报、仅外部 Flush。</param>
        /// <param name="errorSink">同步异常诊断出口；为空则静默吞掉，交由后端自行记录。</param>
        public RedDotServerSeenSync(
            RedDotService service,
            IRedDotSeenSyncBackend backend,
            int debounceMilliseconds = 2000,
            Action<Exception> errorSink = null)
        {
            _service = service ?? throw new ArgumentNullException(nameof(service));
            _backend = backend ?? throw new ArgumentNullException(nameof(backend));
            _debounceMilliseconds = debounceMilliseconds;
            _errorSink = errorSink;
        }

        /// <summary>存在本地已看进度尚未回推服务端。</summary>
        public bool HasPendingPush => _hasPendingPush;

        /// <summary>
        /// 登录编排：拉取→取 max 合并→本地领先则回推→订阅会话内变更。后端拉取异常被隔离上报，
        /// 视为空服务端记录，不阻断进入游戏。
        /// </summary>
        public async UniTask BeginAsync(CancellationToken cancellationToken = default)
        {
            EnsureNotDisposed();
            if (!_service.IsInitialized) return;

            _sessionCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

            IReadOnlyList<RedDotSeenRecord> pulled;
            try
            {
                pulled = await _backend.PullAsync(_sessionCts.Token) ?? Array.Empty<RedDotSeenRecord>();
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                Report(ex);
                pulled = Array.Empty<RedDotSeenRecord>();
            }

            _service.MergeSeen(RedDotSeenSaveMode.ServerAccount, pulled);

            // 本地（含历史迁移/离线确认）版本高于服务端时，把合并后的完整视图回推，抹平差异。
            IReadOnlyList<RedDotSeenRecord> merged = _service.ExportSeen(RedDotSeenSaveMode.ServerAccount);
            if (LocalAhead(merged, pulled))
                await PushSnapshotAsync(merged, _sessionCts.Token);

            if (!_subscribed)
            {
                _service.ServerSeenChanged += OnServerSeenChanged;
                _subscribed = true;
            }
        }

        /// <summary>
        /// 立即回推当前 ServerAccount 已看视图（若有待推进度）。可由外部按节奏或在切后台时调用；
        /// 上报失败会重新置为待推，等待下次机会。
        /// </summary>
        public async UniTask FlushAsync(CancellationToken cancellationToken = default)
        {
            if (_disposed || !_hasPendingPush) return;
            _hasPendingPush = false;
            IReadOnlyList<RedDotSeenRecord> records = _service.ExportSeen(RedDotSeenSaveMode.ServerAccount);
            await PushSnapshotAsync(records, cancellationToken, markPendingOnFailure: true);
        }

        /// <summary>
        /// 回推一份外部捕获的快照；用于登出流程在清空账号态之前抓取 ServerAccount 视图后最终回推。
        /// 与运行态解耦，不受随后 ResetAccountState 影响。
        /// </summary>
        public async UniTask PushSnapshotAsync(
            IReadOnlyList<RedDotSeenRecord> records,
            CancellationToken cancellationToken = default,
            bool markPendingOnFailure = false)
        {
            if (records == null) return;
            try
            {
                await _backend.PushAsync(records, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                if (markPendingOnFailure) _hasPendingPush = true;
            }
            catch (Exception ex)
            {
                if (markPendingOnFailure) _hasPendingPush = true;
                Report(ex);
            }
        }

        private void OnServerSeenChanged()
        {
            _hasPendingPush = true;
            if (_debounceMilliseconds <= 0 || _disposed) return;

            // 每次变更重启去抖窗口：安静期结束后一次性回推，合并连续确认，避免逐条上报。
            _debounceCts?.Cancel();
            _debounceCts?.Dispose();
            _debounceCts = CancellationTokenSource.CreateLinkedTokenSource(_sessionCts.Token);
            DebouncedPushAsync(_debounceCts.Token).Forget();
        }

        private async UniTaskVoid DebouncedPushAsync(CancellationToken cancellationToken)
        {
            try
            {
                await UniTask.Delay(_debounceMilliseconds, cancellationToken: cancellationToken);
                await FlushAsync(cancellationToken);
            }
            catch (OperationCanceledException)
            {
                // 会话结束或被新一轮去抖取代，忽略。
            }
        }

        /// <summary>逐 Signal 判断本地视图是否存在高于服务端的版本（据此决定是否回推）。</summary>
        private static bool LocalAhead(
            IReadOnlyList<RedDotSeenRecord> local,
            IReadOnlyList<RedDotSeenRecord> server)
        {
            var serverVersions = new Dictionary<int, int>(server.Count);
            for (int i = 0; i < server.Count; i++)
            {
                RedDotSeenRecord record = server[i];
                if (record != null) serverVersions[record.SignalId] = record.LastSeenVersion;
            }

            for (int i = 0; i < local.Count; i++)
            {
                RedDotSeenRecord record = local[i];
                if (record == null) continue;
                if (!serverVersions.TryGetValue(record.SignalId, out int version) ||
                    record.LastSeenVersion > version)
                    return true;
            }
            return false;
        }

        private void Report(Exception exception)
        {
            try { _errorSink?.Invoke(exception); }
            catch { /* 诊断出口自身异常没有更下游的去处。 */ }
        }

        private void EnsureNotDisposed()
        {
            if (_disposed) throw new ObjectDisposedException(nameof(RedDotServerSeenSync));
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            if (_subscribed)
            {
                _service.ServerSeenChanged -= OnServerSeenChanged;
                _subscribed = false;
            }
            _debounceCts?.Cancel();
            _debounceCts?.Dispose();
            _debounceCts = null;
            _sessionCts?.Cancel();
            _sessionCts?.Dispose();
            _sessionCts = null;
        }
    }
}
