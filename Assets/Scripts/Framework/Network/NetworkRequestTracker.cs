using System;
using System.Collections.Generic;
using Cysharp.Threading.Tasks;

namespace Framework.Network
{
    /// <summary>
    /// 网络请求跟踪器，管理所有进行中的请求-响应关联。
    /// 职责：
    ///   1. 分配和回收 seqId
    ///   2. 跟踪 pending 请求的超时
    ///   3. 驱动等待 UI 的显示/隐藏
    /// </summary>
    internal sealed class NetworkRequestTracker
    {
        /// <summary>进行中的请求记录。</summary>
        private sealed class PendingRequest
        {
            /// <summary>请求序列号。</summary>
            public ushort SeqId;

            /// <summary>结果回调（收到响应时调用，参数为 payload 字节数组）。</summary>
            public Action<byte[]> OnResponse;

            /// <summary>全局错误码拦截后的回调。</summary>
            public Action OnIntercepted;

            /// <summary>超时/取消回调。</summary>
            public Action OnTimeout;

            /// <summary>请求发出的时间戳（秒）。</summary>
            public float StartTime;

            /// <summary>超时时间（秒）。</summary>
            public float TimeoutSeconds;

            /// <summary>显示 Loading 的延迟（秒），负数表示不显示。</summary>
            public float ShowLoadingDelay;

            /// <summary>是否已经显示了 Loading。</summary>
            public bool LoadingShown;

            /// <summary>是否已完成（响应/超时/取消）。</summary>
            public bool Completed;
        }

        /// <summary>当前在飞请求字典，Key = seqId。</summary>
        private readonly Dictionary<ushort, PendingRequest> _pendingRequests =
            new Dictionary<ushort, PendingRequest>();

        /// <summary>本帧超时请求序列号缓冲，避免 Update 中临时分配列表。</summary>
        private readonly List<ushort> _timeoutSeqIds = new List<ushort>(16);

        /// <summary>seqId 自增计数器（0 保留给推送消息）。</summary>
        private ushort _nextSeqId;

        /// <summary>当前累计时间（由 Update 驱动）。</summary>
        private float _currentTime;

        /// <summary>当前正在显示 Loading 的请求数量。</summary>
        private int _loadingRefCount;

        /// <summary>等待 UI 显示回调（由外部设置）。</summary>
        public Action OnShowWaiting;

        /// <summary>等待 UI 隐藏回调（由外部设置）。</summary>
        public Action OnHideWaiting;

        /// <summary>超时提示回调（由外部设置）。参数：提示文案。</summary>
        public Action<string> OnShowTimeoutTip;

        /// <summary>
        /// 分配一个新的 seqId 并注册待响应请求。
        /// </summary>
        /// <param name="onResponse">收到响应时的回调。</param>
        /// <param name="onTimeout">超时回调。</param>
        /// <param name="config">请求配置。</param>
        /// <param name="onIntercepted">响应被全局错误码拦截后的回调。</param>
        /// <returns>分配的 seqId。</returns>
        public ushort Register(Action<byte[]> onResponse, Action onTimeout, NetworkRequestConfig config, Action onIntercepted = null)
        {
            ushort seqId = AllocateSeqId();
            var pending = new PendingRequest
            {
                SeqId = seqId,
                OnResponse = onResponse,
                OnIntercepted = onIntercepted,
                OnTimeout = onTimeout,
                StartTime = _currentTime,
                TimeoutSeconds = config.TimeoutMs / 1000f,
                ShowLoadingDelay = config.ShowLoadingDelayMs / 1000f,
                LoadingShown = false,
                Completed = false,
            };

            _pendingRequests[seqId] = pending;

            // 如果 delay = 0，立即显示 Loading
            if (pending.ShowLoadingDelay == 0f)
            {
                ShowLoading(pending);
            }

            return seqId;
        }

        /// <summary>
        /// 收到响应时调用，匹配 seqId 并触发回调。
        /// </summary>
        /// <param name="seqId">响应包中的 seqId。</param>
        /// <param name="payload">响应消息体。</param>
        /// <returns>是否匹配到 pending 请求（true = 已消费，不再走多播分发）。</returns>
        public bool TryComplete(ushort seqId, byte[] payload)
        {
            if (seqId == 0)
            {
                // seqId=0 是服务端主动推送，不走请求-响应匹配
                return false;
            }

            if (!_pendingRequests.TryGetValue(seqId, out PendingRequest pending))
            {
                return false;
            }

            if (pending.Completed)
            {
                return true; // 已经完成（可能超时了），丢弃这个迟到的响应
            }

            pending.Completed = true;
            _pendingRequests.Remove(seqId);
            HideLoading(pending);
            pending.OnResponse?.Invoke(payload);
            return true;
        }

        /// <summary>
        /// 主动放弃指定 pending 请求（用于发送失败等场景）：移除记录并隐藏其 Loading，
        /// 不触发任何完成回调，由调用方自行决定如何向业务收尾（通常直接返回 null）。
        /// </summary>
        /// <param name="seqId">请求序列号。</param>
        /// <returns>存在并成功移除时返回 true。</returns>
        public bool Cancel(ushort seqId)
        {
            if (seqId == 0 || !_pendingRequests.TryGetValue(seqId, out PendingRequest pending))
            {
                return false;
            }

            if (!pending.Completed)
            {
                pending.Completed = true;
                HideLoading(pending);
            }

            _pendingRequests.Remove(seqId);
            return true;
        }

        /// <summary>
        /// 判断指定 seqId 是否仍存在未完成的在飞请求。
        /// 供分发前识别已超时/已完成的迟到或重复响应并丢弃，避免其绕过 RequestAsync 单方面刷新业务缓存。
        /// </summary>
        /// <param name="seqId">响应包中的 seqId。</param>
        /// <returns>存在未完成 pending 请求时返回 true。</returns>
        public bool HasPending(ushort seqId)
        {
            return seqId != 0
                && _pendingRequests.TryGetValue(seqId, out PendingRequest pending)
                && !pending.Completed;
        }

        /// <summary>
        /// 将指定请求标记为已被全局错误拦截器消费。
        /// </summary>
        /// <param name="seqId">响应包中的 seqId。</param>
        /// <returns>成功释放 pending 请求时返回 true。</returns>
        public bool TryMarkIntercepted(ushort seqId)
        {
            if (seqId == 0 || !_pendingRequests.TryGetValue(seqId, out PendingRequest pending))
            {
                return false;
            }

            if (pending.Completed)
            {
                return true;
            }

            pending.Completed = true;
            _pendingRequests.Remove(seqId);
            HideLoading(pending);
            pending.OnIntercepted?.Invoke();
            return true;
        }

        /// <summary>
        /// 每帧 Update，检查超时和显示 Loading。
        /// </summary>
        /// <param name="deltaTime">帧间隔时间。</param>
        public void Update(float deltaTime)
        {
            _currentTime += deltaTime;

            if (_pendingRequests.Count == 0)
            {
                return;
            }

            // 收集需要处理的 seqId（避免在迭代中修改字典）
            _timeoutSeqIds.Clear();

            foreach (var kvp in _pendingRequests)
            {
                PendingRequest pending = kvp.Value;
                if (pending.Completed)
                {
                    continue;
                }

                float elapsed = _currentTime - pending.StartTime;

                // 检查是否需要显示 Loading
                if (!pending.LoadingShown && pending.ShowLoadingDelay >= 0f && elapsed >= pending.ShowLoadingDelay)
                {
                    ShowLoading(pending);
                }

                // 检查超时
                if (elapsed >= pending.TimeoutSeconds)
                {
                    _timeoutSeqIds.Add(kvp.Key);
                }
            }

            // 处理超时
            if (_timeoutSeqIds.Count > 0)
            {
                for (int i = 0; i < _timeoutSeqIds.Count; i++)
                {
                    TimeoutRequest(_timeoutSeqIds[i]);
                }

                _timeoutSeqIds.Clear();
            }
        }

        /// <summary>
        /// 取消所有进行中的请求（断线时调用）。
        /// </summary>
        public void CancelAll()
        {
            foreach (var kvp in _pendingRequests)
            {
                PendingRequest pending = kvp.Value;
                if (!pending.Completed)
                {
                    pending.Completed = true;
                    HideLoading(pending);
                    pending.OnTimeout?.Invoke();
                }
            }

            _pendingRequests.Clear();

            if (_loadingRefCount > 0)
            {
                _loadingRefCount = 0;
                OnHideWaiting?.Invoke();
            }
        }

        /// <summary>
        /// 获取当前 pending 请求数量。
        /// </summary>
        public int PendingCount => _pendingRequests.Count;

        /// <summary>
        /// 分配一个新的 seqId（跳过 0）。
        /// </summary>
        private ushort AllocateSeqId()
        {
            _nextSeqId++;
            if (_nextSeqId == 0)
            {
                _nextSeqId = 1; // 跳过 0，0 保留给推送消息
            }

            return _nextSeqId;
        }

        /// <summary>
        /// 请求超时处理。
        /// </summary>
        /// <param name="seqId">超时的 seqId。</param>
        private void TimeoutRequest(ushort seqId)
        {
            if (!_pendingRequests.TryGetValue(seqId, out PendingRequest pending))
            {
                return;
            }

            if (pending.Completed)
            {
                return;
            }

            pending.Completed = true;
            _pendingRequests.Remove(seqId);
            HideLoading(pending);
            pending.OnTimeout?.Invoke();

            GameLog.Warning($"[NetworkRequestTracker] 请求超时: seqId={seqId}");
        }

        /// <summary>
        /// 增加 Loading 引用计数，首次时显示等待 UI。
        /// </summary>
        /// <param name="pending">请求记录。</param>
        private void ShowLoading(PendingRequest pending)
        {
            if (pending.LoadingShown)
            {
                return;
            }

            pending.LoadingShown = true;
            _loadingRefCount++;
            if (_loadingRefCount == 1)
            {
                OnShowWaiting?.Invoke();
            }
        }

        /// <summary>
        /// 减少 Loading 引用计数，归零时隐藏等待 UI。
        /// </summary>
        /// <param name="pending">请求记录。</param>
        private void HideLoading(PendingRequest pending)
        {
            if (!pending.LoadingShown)
            {
                return;
            }

            pending.LoadingShown = false;
            _loadingRefCount--;
            if (_loadingRefCount <= 0)
            {
                _loadingRefCount = 0;
                OnHideWaiting?.Invoke();
            }
        }
    }
}
