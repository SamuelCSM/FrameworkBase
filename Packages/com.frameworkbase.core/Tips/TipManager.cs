using System;
using System.Collections.Generic;
using Framework.Core;
using UnityEngine;

namespace Framework
{
    /// <summary>
    /// 全局轻提示管理器，负责请求去重、排队、限流和展示层派发。
    /// </summary>
    public sealed class TipManager : FrameworkComponent<TipManager>
    {
        /// <summary>默认同屏最大提示数量。</summary>
        private const int DefaultMaxVisibleCount = 3;

        /// <summary>默认待展示队列最大数量。</summary>
        private const int DefaultMaxPendingCount = 20;

        /// <summary>默认去重窗口时长。</summary>
        private const float DefaultDedupeSeconds = 1.5f;

        /// <summary>待展示提示队列，按优先级和入队顺序排列。</summary>
        private readonly List<TipRequest> pendingRequests = new List<TipRequest>();

        /// <summary>去重键到最近入队时间的映射。</summary>
        private readonly Dictionary<string, float> lastDedupeTimes = new Dictionary<string, float>(StringComparer.Ordinal);

        /// <summary>下一个请求编号。</summary>
        private long nextRequestId = 1;

        /// <summary>当前展示层正在播放的提示数量。</summary>
        private int visibleCount;

        /// <summary>是否已经订阅网络提示事件。</summary>
        private bool networkEventsRegistered;

        /// <summary>展示层收到该事件后应创建一个提示表现。</summary>
        public event Action<TipRequest> OnTipReadyToDisplay;

        /// <summary>清理提示事件，参数表示是否包含系统级提示。</summary>
        public event Action<bool> OnTipsCleared;

        /// <summary>同屏最大提示数量。</summary>
        public int MaxVisibleCount { get; set; } = DefaultMaxVisibleCount;

        /// <summary>待展示队列最大数量。</summary>
        public int MaxPendingCount { get; set; } = DefaultMaxPendingCount;

        /// <summary>
        /// 初始化轻提示管理器，并接入框架网络提示事件。
        /// </summary>
        public override void OnInit()
        {
            RegisterNetworkEvents();
            GameLog.Log("[TipManager] 初始化完成");
        }

        /// <summary>
        /// 关闭轻提示管理器，释放事件订阅和队列。
        /// </summary>
        public override void OnShutdown()
        {
            UnregisterNetworkEvents();
            pendingRequests.Clear();
            lastDedupeTimes.Clear();
            visibleCount = 0;
            OnTipReadyToDisplay = null;
            OnTipsCleared = null;
            GameLog.Log("[TipManager] 已关闭");
        }

        /// <summary>
        /// 展示原始文本提示，适合服务端返回内容、玩家名和动态数字。
        /// </summary>
        /// <param name="text">原始提示文本。</param>
        /// <param name="style">提示视觉类型。</param>
        /// <param name="priority">调度优先级。</param>
        /// <param name="duration">停留时长，单位秒；小于等于 0 时使用默认值。</param>
        /// <param name="dedupeKey">去重键；为空时自动生成。</param>
        /// <returns>成功入队时返回请求对象，否则返回 null。</returns>
        public TipRequest ShowRaw(
            string text,
            TipStyle style = TipStyle.Normal,
            TipPriority priority = TipPriority.Normal,
            float duration = 0f,
            string dedupeKey = null)
        {
            return Enqueue(new TipRequest
            {
                TextOrKey = text,
                IsLanguageKey = false,
                Style = style,
                Priority = priority,
                Duration = duration,
                DedupeKey = dedupeKey,
            });
        }

        /// <summary>
        /// 在指定通道展示原始文本提示，适合需要固定展示区域的玩法内反馈。
        /// </summary>
        /// <param name="text">原始提示文本。</param>
        /// <param name="style">提示视觉类型。</param>
        /// <param name="priority">调度优先级。</param>
        /// <param name="channel">展示通道。</param>
        /// <param name="duration">停留时长，单位秒；小于等于 0 时使用默认值。</param>
        /// <param name="dedupeKey">去重键；为空时自动生成。</param>
        /// <returns>成功入队时返回请求对象，否则返回 null。</returns>
        public TipRequest ShowRaw(
            string text,
            TipStyle style,
            TipPriority priority,
            TipChannel channel,
            float duration = 0f,
            string dedupeKey = null)
        {
            return Enqueue(new TipRequest
            {
                TextOrKey = text,
                IsLanguageKey = false,
                Style = style,
                Priority = priority,
                Channel = channel,
                Duration = duration,
                DedupeKey = dedupeKey,
            });
        }

        /// <summary>
        /// 展示多语言提示，适合代码主动控制的 #1_xxx 文案。
        /// </summary>
        /// <param name="key">多语言 key。</param>
        /// <param name="args">格式化参数。</param>
        /// <returns>成功入队时返回请求对象，否则返回 null。</returns>
        public TipRequest ShowLang(string key, params object[] args)
        {
            return ShowLang(key, TipStyle.Normal, TipPriority.Normal, 0f, null, args);
        }

        /// <summary>
        /// 展示指定类型的多语言提示。
        /// </summary>
        /// <param name="key">多语言 key。</param>
        /// <param name="style">提示视觉类型。</param>
        /// <param name="args">格式化参数。</param>
        /// <returns>成功入队时返回请求对象，否则返回 null。</returns>
        public TipRequest ShowLang(string key, TipStyle style, params object[] args)
        {
            return ShowLang(key, style, TipPriority.Normal, 0f, null, args);
        }

        /// <summary>
        /// 展示完整参数的多语言提示。
        /// </summary>
        /// <param name="key">多语言 key。</param>
        /// <param name="style">提示视觉类型。</param>
        /// <param name="priority">调度优先级。</param>
        /// <param name="duration">停留时长，单位秒；小于等于 0 时使用默认值。</param>
        /// <param name="dedupeKey">去重键；为空时自动生成。</param>
        /// <param name="args">格式化参数。</param>
        /// <returns>成功入队时返回请求对象，否则返回 null。</returns>
        public TipRequest ShowLang(
            string key,
            TipStyle style,
            TipPriority priority,
            float duration,
            string dedupeKey,
            params object[] args)
        {
            return Enqueue(new TipRequest
            {
                TextOrKey = key,
                IsLanguageKey = true,
                FormatArgs = args,
                Style = style,
                Priority = priority,
                Duration = duration,
                DedupeKey = dedupeKey,
            });
        }

        /// <summary>
        /// 在指定通道展示完整参数的多语言提示。
        /// </summary>
        /// <param name="key">多语言 key。</param>
        /// <param name="style">提示视觉类型。</param>
        /// <param name="priority">调度优先级。</param>
        /// <param name="channel">展示通道。</param>
        /// <param name="duration">停留时长，单位秒；小于等于 0 时使用默认值。</param>
        /// <param name="dedupeKey">去重键；为空时自动生成。</param>
        /// <param name="args">格式化参数。</param>
        /// <returns>成功入队时返回请求对象，否则返回 null。</returns>
        public TipRequest ShowLang(
            string key,
            TipStyle style,
            TipPriority priority,
            TipChannel channel,
            float duration,
            string dedupeKey,
            params object[] args)
        {
            return Enqueue(new TipRequest
            {
                TextOrKey = key,
                IsLanguageKey = true,
                FormatArgs = args,
                Style = style,
                Priority = priority,
                Channel = channel,
                Duration = duration,
                DedupeKey = dedupeKey,
            });
        }

        /// <summary>
        /// 展示成功类型的多语言提示。
        /// </summary>
        /// <param name="key">多语言 key。</param>
        /// <param name="args">格式化参数。</param>
        /// <returns>成功入队时返回请求对象，否则返回 null。</returns>
        public TipRequest ShowSuccess(string key, params object[] args)
        {
            return ShowLang(key, TipStyle.Success, TipPriority.Normal, 0f, null, args);
        }

        /// <summary>
        /// 展示警告类型的多语言提示。
        /// </summary>
        /// <param name="key">多语言 key。</param>
        /// <param name="args">格式化参数。</param>
        /// <returns>成功入队时返回请求对象，否则返回 null。</returns>
        public TipRequest ShowWarning(string key, params object[] args)
        {
            return ShowLang(key, TipStyle.Warning, TipPriority.Normal, 0f, null, args);
        }

        /// <summary>
        /// 在指定通道展示警告类型的多语言提示。
        /// </summary>
        /// <param name="key">多语言 key。</param>
        /// <param name="channel">展示通道。</param>
        /// <param name="args">格式化参数。</param>
        /// <returns>成功入队时返回请求对象，否则返回 null。</returns>
        public TipRequest ShowWarning(string key, TipChannel channel, params object[] args)
        {
            return ShowLang(key, TipStyle.Warning, TipPriority.Normal, channel, 0f, null, args);
        }

        /// <summary>
        /// 展示错误类型的多语言提示。
        /// </summary>
        /// <param name="key">多语言 key。</param>
        /// <param name="args">格式化参数。</param>
        /// <returns>成功入队时返回请求对象，否则返回 null。</returns>
        public TipRequest ShowError(string key, params object[] args)
        {
            return ShowLang(key, TipStyle.Error, TipPriority.High, 0f, null, args);
        }

        /// <summary>
        /// 展示层准备就绪时调用，用于把启动期积压提示继续派发。
        /// </summary>
        public void MarkDisplayHostReady()
        {
            PumpQueue();
        }

        /// <summary>
        /// 展示层完成一个提示后调用，驱动后续队列出队。
        /// </summary>
        /// <param name="request">已完成展示的提示请求。</param>
        public void NotifyDisplayComplete(TipRequest request)
        {
            visibleCount = Mathf.Max(0, visibleCount - 1);
            PumpQueue();
        }

        /// <summary>
        /// 清理非系统级待展示提示，并通知展示层停止普通提示动画。
        /// </summary>
        public void ClearTransient()
        {
            Clear(includeSystem: false);
        }

        /// <summary>
        /// 清理所有待展示和展示中的提示。
        /// </summary>
        public void ClearAll()
        {
            Clear(includeSystem: true);
        }

        /// <summary>
        /// 将提示请求入队并尝试派发到展示层。
        /// </summary>
        /// <param name="request">提示请求。</param>
        /// <returns>成功入队时返回请求对象，否则返回 null。</returns>
        private TipRequest Enqueue(TipRequest request)
        {
            if (request == null || string.IsNullOrWhiteSpace(request.TextOrKey))
            {
                return null;
            }

            request.RequestId = nextRequestId++;
            request.CreatedUtc = DateTime.UtcNow;
            request.Duration = NormalizeDuration(request.Style, request.Duration);
            request.DedupeSeconds = request.DedupeSeconds > 0f ? request.DedupeSeconds : DefaultDedupeSeconds;
            request.DedupeKey = string.IsNullOrWhiteSpace(request.DedupeKey)
                ? BuildDefaultDedupeKey(request)
                : request.DedupeKey.Trim();

            if (IsDuplicated(request))
            {
                return null;
            }

            if (!EnsureQueueCapacity(request.Priority))
            {
                GameLog.Warning($"[TipManager] 队列已满，丢弃提示：{request.TextOrKey}");
                return null;
            }

            InsertByPriority(request);
            lastDedupeTimes[request.DedupeKey] = Time.realtimeSinceStartup;
            PumpQueue();
            return request;
        }

        /// <summary>
        /// 按优先级和入队顺序插入待展示队列。
        /// </summary>
        /// <param name="request">提示请求。</param>
        private void InsertByPriority(TipRequest request)
        {
            int insertIndex = pendingRequests.Count;
            for (int i = 0; i < pendingRequests.Count; i++)
            {
                if (request.Priority > pendingRequests[i].Priority)
                {
                    insertIndex = i;
                    break;
                }
            }

            pendingRequests.Insert(insertIndex, request);
        }

        /// <summary>
        /// 确保待展示队列有容量，必要时丢弃低优先级提示。
        /// </summary>
        /// <param name="incomingPriority">新请求优先级。</param>
        /// <returns>仍可入队时返回 true。</returns>
        private bool EnsureQueueCapacity(TipPriority incomingPriority)
        {
            if (pendingRequests.Count < MaxPendingCount)
            {
                return true;
            }

            int dropIndex = -1;
            for (int i = pendingRequests.Count - 1; i >= 0; i--)
            {
                if (pendingRequests[i].Priority <= incomingPriority)
                {
                    dropIndex = i;
                    break;
                }
            }

            if (dropIndex < 0)
            {
                return false;
            }

            pendingRequests.RemoveAt(dropIndex);
            return true;
        }

        /// <summary>
        /// 尝试把待展示队列派发到展示层。
        /// </summary>
        private void PumpQueue()
        {
            Action<TipRequest> handler = OnTipReadyToDisplay;
            if (handler == null)
            {
                return;
            }

            while (visibleCount < MaxVisibleCount && pendingRequests.Count > 0)
            {
                TipRequest request = pendingRequests[0];
                pendingRequests.RemoveAt(0);
                visibleCount++;

                try
                {
                    handler.Invoke(request);
                }
                catch (Exception ex)
                {
                    visibleCount = Mathf.Max(0, visibleCount - 1);
                    GameLog.Error($"[TipManager] 展示层派发异常：{ex.Message}");
                    break;
                }
            }
        }

        /// <summary>
        /// 判断提示是否处于去重窗口内。
        /// </summary>
        /// <param name="request">提示请求。</param>
        /// <returns>重复提示返回 true。</returns>
        private bool IsDuplicated(TipRequest request)
        {
            if (string.IsNullOrEmpty(request.DedupeKey))
            {
                return false;
            }

            if (!lastDedupeTimes.TryGetValue(request.DedupeKey, out float lastTime))
            {
                return false;
            }

            return Time.realtimeSinceStartup - lastTime < request.DedupeSeconds;
        }

        /// <summary>
        /// 按提示类型归一化默认停留时长。
        /// </summary>
        /// <param name="style">提示视觉类型。</param>
        /// <param name="duration">外部传入时长。</param>
        /// <returns>最终停留时长。</returns>
        private static float NormalizeDuration(TipStyle style, float duration)
        {
            if (duration > 0f)
            {
                return duration;
            }

            return style == TipStyle.Error || style == TipStyle.Warning ? 2.5f : 2f;
        }

        /// <summary>
        /// 构造默认去重键。
        /// </summary>
        /// <param name="request">提示请求。</param>
        /// <returns>默认去重键。</returns>
        private static string BuildDefaultDedupeKey(TipRequest request)
        {
            return $"{request.Channel}:{request.Style}:{request.IsLanguageKey}:{request.TextOrKey}";
        }

        /// <summary>
        /// 清理待展示队列和展示层。
        /// </summary>
        /// <param name="includeSystem">是否包含系统级提示。</param>
        private void Clear(bool includeSystem)
        {
            for (int i = pendingRequests.Count - 1; i >= 0; i--)
            {
                TipRequest request = pendingRequests[i];
                bool isSystem = request.Priority == TipPriority.System || request.Channel == TipChannel.System;
                if (includeSystem || !isSystem)
                {
                    pendingRequests.RemoveAt(i);
                }
            }

            visibleCount = 0;
            OnTipsCleared?.Invoke(includeSystem);
        }

        /// <summary>
        /// 订阅网络层提示事件。
        /// </summary>
        private void RegisterNetworkEvents()
        {
            if (networkEventsRegistered || GameEntry.Network == null)
            {
                return;
            }

            GameEntry.Network.OnRequestTimeout += OnNetworkRequestTimeout;
            GameEntry.Network.OnError += OnNetworkError;
            networkEventsRegistered = true;
        }

        /// <summary>
        /// 取消网络层提示事件订阅。
        /// </summary>
        private void UnregisterNetworkEvents()
        {
            if (!networkEventsRegistered || GameEntry.Network == null)
            {
                return;
            }

            GameEntry.Network.OnRequestTimeout -= OnNetworkRequestTimeout;
            GameEntry.Network.OnError -= OnNetworkError;
            networkEventsRegistered = false;
        }

        /// <summary>
        /// 网络请求超时回调。
        /// </summary>
        /// <param name="message">超时提示文案。</param>
        private void OnNetworkRequestTimeout(string message)
        {
            ShowRaw(message, TipStyle.Warning, TipPriority.High, 2.5f, $"network_timeout:{message}");
        }

        /// <summary>
        /// 网络错误回调。
        /// </summary>
        /// <param name="message">错误提示文案。</param>
        private void OnNetworkError(string message)
        {
            ShowRaw(message, TipStyle.Error, TipPriority.High, 2.5f, $"network_error:{message}");
        }
    }
}
