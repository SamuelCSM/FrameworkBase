using System;
using System.Collections.Generic;
using System.Threading;

namespace Framework.Network
{
    /// <summary>
    /// 网络请求生命周期跟踪器，使用 ConnectionEpoch 与 ushort SeqId 组成请求身份。
    /// <para>
    /// SeqId 在单次连接内循环复用，因此不能单独作为响应归属依据。ConnectionEpoch 随每次新连接递增，
    /// 可阻止旧 Socket 收包线程、代理缓存或延迟响应错误完成新连接上恰好复用相同 SeqId 的请求。
    /// </para>
    /// <para>
    /// 本类型预期只在 Unity 主线程调用。CancellationToken 仅在 <see cref="Update"/> 中轮询消费，
    /// 不直接注册跨线程回调，从而避免后台线程并发修改字典或触发 UI 回调。
    /// </para>
    /// </summary>
    internal sealed class NetworkRequestTracker
    {
        /// <summary>
        /// 单个在途请求的完整状态。所有完成路径必须先设置 Completed 并从字典移除，再调用外部回调，
        /// 以防回调重入导致同一请求被二次完成。
        /// </summary>
        private sealed class PendingRequest
        {
            public int ConnectionEpoch;
            public ushort SeqId;
            public Action<byte[]> OnResponse;
            public Action OnIntercepted;
            public Action OnTimeout;
            public Action OnCancelled;
            public float StartTime;
            public float TimeoutSeconds;
            public float ShowLoadingDelay;
            public bool LoadingShown;
            public bool Completed;
            public CancellationToken CancellationToken;
        }

        private readonly Dictionary<ushort, PendingRequest> _pendingRequests =
            new Dictionary<ushort, PendingRequest>();
        private readonly List<ushort> _timeoutSeqIds = new List<ushort>(16);
        private readonly List<ushort> _cancelSeqIds = new List<ushort>(16);
        private ushort _nextSeqId;
        private float _currentTime;
        private int _loadingRefCount;

        /// <summary>
        /// 第一个请求进入等待展示状态时触发；通常由上层 UI 适配器显示全局等待遮罩。
        /// </summary>
        public Action OnShowWaiting;

        /// <summary>
        /// 最后一个已展示等待状态的请求结束时触发；使用引用计数避免并发请求互相提前关闭遮罩。
        /// </summary>
        public Action OnHideWaiting;

        /// <summary>
        /// 兼容旧接口的超时提示入口。具体文案、频控及交互应由 NetworkManager 或 UI 模板处理。
        /// </summary>
        public Action<string> OnShowTimeoutTip;

        /// <summary>
        /// 当前仍在等待响应、拦截、取消或超时的请求数量。
        /// </summary>
        public int PendingCount => _pendingRequests.Count;

        /// <summary>
        /// 注册一个属于指定连接世代的请求，并分配 1～65535 范围内当前未占用的 SeqId。
        /// </summary>
        /// <param name="connectionEpoch">发送该请求时的连接世代。</param>
        /// <param name="onResponse">收到同世代匹配响应后的成功回调。</param>
        /// <param name="onTimeout">超过请求超时时间后的回调。</param>
        /// <param name="onCancelled">取消令牌被触发后的回调。</param>
        /// <param name="config">超时和等待遮罩策略。</param>
        /// <param name="cancellationToken">由主线程 Update 轮询的取消令牌。</param>
        /// <param name="onIntercepted">响应被统一拦截器消费后的回调。</param>
        /// <returns>当前请求占用的非零 SeqId。</returns>
        /// <exception cref="ArgumentNullException">请求配置为空。</exception>
        /// <exception cref="InvalidOperationException">全部 65535 个 SeqId 均处于占用状态。</exception>
        public ushort Register(
            int connectionEpoch,
            Action<byte[]> onResponse,
            Action onTimeout,
            Action onCancelled,
            NetworkRequestConfig config,
            CancellationToken cancellationToken = default,
            Action onIntercepted = null)
        {
            if (config == null) throw new ArgumentNullException(nameof(config));
            ushort seqId = AllocateSeqId();
            var pending = new PendingRequest
            {
                ConnectionEpoch = connectionEpoch,
                SeqId = seqId,
                OnResponse = onResponse,
                OnIntercepted = onIntercepted,
                OnTimeout = onTimeout,
                OnCancelled = onCancelled,
                StartTime = _currentTime,
                TimeoutSeconds = Math.Max(0.001f, config.TimeoutMs / 1000f),
                ShowLoadingDelay = config.ShowLoadingDelayMs / 1000f,
                CancellationToken = cancellationToken,
            };
            _pendingRequests.Add(seqId, pending);
            if (pending.ShowLoadingDelay == 0f) ShowLoading(pending);
            return seqId;
        }

        /// <summary>
        /// 兼容旧调用的注册重载。旧链路没有连接世代和独立取消语义，因此固定使用 Epoch 0，
        /// 并沿用超时回调作为取消回调；新代码应使用完整参数重载。
        /// </summary>
        public ushort Register(
            Action<byte[]> onResponse,
            Action onTimeout,
            NetworkRequestConfig config,
            Action onIntercepted = null)
        {
            return Register(0, onResponse, onTimeout, onTimeout, config, default, onIntercepted);
        }

        /// <summary>
        /// 仅当 Epoch 与 SeqId 同时匹配时完成请求；旧连接响应或未知响应返回 <see langword="false"/>。
        /// </summary>
        public bool TryComplete(int connectionEpoch, ushort seqId, byte[] payload)
        {
            if (!TryGetPending(connectionEpoch, seqId, out PendingRequest pending)) return false;
            CompleteAndRemove(pending);
            pending.OnResponse?.Invoke(payload);
            return true;
        }

        /// <summary>
        /// 兼容无连接世代的旧调用，固定按 Epoch 0 匹配。
        /// </summary>
        public bool TryComplete(ushort seqId, byte[] payload) => TryComplete(0, seqId, payload);

        /// <summary>
        /// 静默移除指定请求，不触发超时、取消或成功回调；用于发送失败后由调用方自行完成错误语义。
        /// </summary>
        public bool Cancel(ushort seqId)
        {
            if (!_pendingRequests.TryGetValue(seqId, out PendingRequest pending)) return false;
            CompleteAndRemove(pending);
            return true;
        }

        /// <summary>
        /// 判断指定连接世代与 SeqId 对应的请求是否仍处于等待状态。
        /// </summary>
        public bool HasPending(int connectionEpoch, ushort seqId)
        {
            return TryGetPending(connectionEpoch, seqId, out _);
        }

        /// <summary>
        /// 兼容旧代码，仅按 SeqId 判断请求是否存在，不提供跨连接隔离保证。
        /// </summary>
        public bool HasPending(ushort seqId)
        {
            return seqId != 0 && _pendingRequests.TryGetValue(seqId, out PendingRequest pending) && !pending.Completed;
        }

        /// <summary>
        /// 将请求标记为已被统一响应拦截器消费，并触发独立拦截回调。
        /// </summary>
        public bool TryMarkIntercepted(int connectionEpoch, ushort seqId)
        {
            if (!TryGetPending(connectionEpoch, seqId, out PendingRequest pending)) return false;
            CompleteAndRemove(pending);
            pending.OnIntercepted?.Invoke();
            return true;
        }

        /// <summary>
        /// 兼容无连接世代的旧调用，固定按 Epoch 0 标记拦截。
        /// </summary>
        public bool TryMarkIntercepted(ushort seqId) => TryMarkIntercepted(0, seqId);

        /// <summary>
        /// 在 Unity 主线程推进请求计时、取消令牌、等待遮罩与超时状态。
        /// <para>
        /// 遍历期间只收集待处理 SeqId，遍历结束后再修改字典，避免集合枚举失效；
        /// 取消优先于超时，确保同一帧同时满足两个条件时只产生一种终态。
        /// </para>
        /// </summary>
        public void Update(float deltaTime)
        {
            _currentTime += Math.Max(0, deltaTime);
            if (_pendingRequests.Count == 0) return;

            _timeoutSeqIds.Clear();
            _cancelSeqIds.Clear();
            foreach (KeyValuePair<ushort, PendingRequest> pair in _pendingRequests)
            {
                PendingRequest pending = pair.Value;
                if (pending.Completed) continue;
                if (pending.CancellationToken.IsCancellationRequested)
                {
                    _cancelSeqIds.Add(pair.Key);
                    continue;
                }

                float elapsed = _currentTime - pending.StartTime;
                if (!pending.LoadingShown && pending.ShowLoadingDelay >= 0f && elapsed >= pending.ShowLoadingDelay)
                    ShowLoading(pending);
                if (elapsed >= pending.TimeoutSeconds)
                    _timeoutSeqIds.Add(pair.Key);
            }

            foreach (ushort seqId in _cancelSeqIds) CancelRequest(seqId);
            foreach (ushort seqId in _timeoutSeqIds) TimeoutRequest(seqId);
        }

        /// <summary>
        /// 取消并清理全部在途请求；每个请求触发取消回调，并保证等待遮罩引用计数最终归零。
        /// </summary>
        public void CancelAll()
        {
            var requests = new List<PendingRequest>(_pendingRequests.Values);
            _pendingRequests.Clear();
            foreach (PendingRequest pending in requests)
            {
                if (pending.Completed) continue;
                pending.Completed = true;
                HideLoading(pending);
                pending.OnCancelled?.Invoke();
            }
            ResetLoading();
        }

        /// <summary>
        /// 循环寻找未被占用的非零 SeqId。0 保留给无需请求响应匹配的单向消息。
        /// </summary>
        private ushort AllocateSeqId()
        {
            for (int i = 0; i < ushort.MaxValue; i++)
            {
                _nextSeqId++;
                if (_nextSeqId == 0) _nextSeqId = 1;
                if (!_pendingRequests.ContainsKey(_nextSeqId)) return _nextSeqId;
            }
            throw new InvalidOperationException("网络请求 SeqId 已全部占用，拒绝继续注册请求。");
        }

        /// <summary>
        /// 获取仍有效且连接世代匹配的请求，防止旧连接数据完成新连接请求。
        /// </summary>
        private bool TryGetPending(int connectionEpoch, ushort seqId, out PendingRequest pending)
        {
            pending = null;
            if (seqId == 0 || !_pendingRequests.TryGetValue(seqId, out PendingRequest candidate) || candidate.Completed)
                return false;
            if (candidate.ConnectionEpoch != connectionEpoch) return false;
            pending = candidate;
            return true;
        }

        /// <summary>
        /// 将指定请求转换为超时终态。必须先移除再调用外部回调，以保证回调重入安全。
        /// </summary>
        private void TimeoutRequest(ushort seqId)
        {
            if (!_pendingRequests.TryGetValue(seqId, out PendingRequest pending)) return;
            CompleteAndRemove(pending);
            pending.OnTimeout?.Invoke();
            GameLog.Warning($"[NetworkRequestTracker] 请求超时：epoch={pending.ConnectionEpoch} seq={seqId}");
        }

        /// <summary>
        /// 将指定请求转换为取消终态，并触发独立取消回调。
        /// </summary>
        private void CancelRequest(ushort seqId)
        {
            if (!_pendingRequests.TryGetValue(seqId, out PendingRequest pending)) return;
            CompleteAndRemove(pending);
            pending.OnCancelled?.Invoke();
        }

        /// <summary>
        /// 执行所有终态共用的原子式本地清理：标记完成、移除索引并释放等待遮罩引用。
        /// </summary>
        private void CompleteAndRemove(PendingRequest pending)
        {
            pending.Completed = true;
            _pendingRequests.Remove(pending.SeqId);
            HideLoading(pending);
        }

        /// <summary>
        /// 为请求增加一次等待遮罩引用，仅在引用从 0 变为 1 时通知上层显示。
        /// </summary>
        private void ShowLoading(PendingRequest pending)
        {
            if (pending.LoadingShown) return;
            pending.LoadingShown = true;
            _loadingRefCount++;
            if (_loadingRefCount == 1) OnShowWaiting?.Invoke();
        }

        /// <summary>
        /// 释放请求持有的等待遮罩引用；最后一个引用释放时通知上层隐藏。
        /// </summary>
        private void HideLoading(PendingRequest pending)
        {
            if (!pending.LoadingShown) return;
            pending.LoadingShown = false;
            _loadingRefCount--;
            if (_loadingRefCount <= 0) ResetLoading();
        }

        /// <summary>
        /// 强制将等待遮罩引用计数归零，并在确有可见引用时通知上层隐藏。
        /// </summary>
        private void ResetLoading()
        {
            if (_loadingRefCount > 0) OnHideWaiting?.Invoke();
            _loadingRefCount = 0;
        }
    }
}
