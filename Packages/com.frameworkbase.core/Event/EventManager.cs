using System;
using System.Collections.Generic;
using Framework.Core;

namespace Framework
{
    /// <summary>
    /// 事件管理器，提供项目内通用消息订阅、取消订阅和发布能力。
    /// </summary>
    public class EventManager : FrameworkComponent
    {
        /// <summary>
        /// 事件监听器字典，Key 为统一消息 ID。
        /// </summary>
        private readonly Dictionary<int, List<EventListener>> eventListeners =
            new Dictionary<int, List<EventListener>>();

        /// <summary>
        /// 正在触发的事件集合，用于阻止同一事件递归触发。
        /// </summary>
        private readonly HashSet<int> triggeringEvents = new HashSet<int>();

        /// <summary>
        /// 需要重新排序的事件集合，新增订阅或优先级变化后标记。
        /// </summary>
        private readonly HashSet<int> needSortEvents = new HashSet<int>();

        /// <summary>
        /// 递增订阅序号，用于同优先级时保持订阅顺序稳定。
        /// </summary>
        private long nextListenerId;

        /// <summary>
        /// 派发快照列表池，避免每次 Publish 都对监听器列表 ToArray 产生 GC。
        /// EventManager 仅在主线程使用，故无需加锁。
        /// </summary>
        private readonly Stack<List<EventListener>> snapshotPool = new Stack<List<EventListener>>();

        /// <summary>
        /// 初始化事件管理器。
        /// </summary>
        public override void OnInit()
        {
            base.OnInit();
            GameLog.Log("[EventManager] 事件管理器初始化完成");
        }

        /// <summary>
        /// 关闭时清理全部事件订阅。
        /// </summary>
        public override void OnShutdown()
        {
            Clear();
            GameLog.Log("[EventManager] 事件管理器已关闭");
            base.OnShutdown();
        }

        /// <summary>
        /// 订阅无参数消息。
        /// </summary>
        /// <param name="message">消息枚举。</param>
        /// <param name="callback">消息回调。</param>
        /// <param name="priority">优先级，数值越大越先触发。</param>
        /// <returns>用于取消本次订阅的句柄。</returns>
        public EventSubscription Subscribe(GameMessage message, Action callback, int priority = 0)
        {
            return Subscribe((int)message, callback, priority);
        }

        /// <summary>
        /// 订阅无参数消息。业务热更程序集可自建枚举/常量后转为 int 传入。
        /// </summary>
        /// <param name="messageId">统一消息 ID。</param>
        /// <param name="callback">消息回调。</param>
        /// <param name="priority">优先级，数值越大越先触发。</param>
        /// <returns>用于取消本次订阅的句柄。</returns>
        public EventSubscription Subscribe(int messageId, Action callback, int priority = 0)
        {
            return SubscribeInternal(messageId, callback, priority);
        }

        /// <summary>
        /// 订阅一个参数的消息。
        /// </summary>
        /// <typeparam name="T">参数类型。</typeparam>
        /// <param name="message">消息枚举。</param>
        /// <param name="callback">消息回调。</param>
        /// <param name="priority">优先级，数值越大越先触发。</param>
        /// <returns>用于取消本次订阅的句柄。</returns>
        public EventSubscription Subscribe<T>(GameMessage message, Action<T> callback, int priority = 0)
        {
            return Subscribe((int)message, callback, priority);
        }

        /// <summary>
        /// 订阅一个参数的消息。业务热更程序集可自建枚举/常量后转为 int 传入。
        /// </summary>
        /// <typeparam name="T">参数类型。</typeparam>
        /// <param name="messageId">统一消息 ID。</param>
        /// <param name="callback">消息回调。</param>
        /// <param name="priority">优先级，数值越大越先触发。</param>
        /// <returns>用于取消本次订阅的句柄。</returns>
        public EventSubscription Subscribe<T>(int messageId, Action<T> callback, int priority = 0)
        {
            return SubscribeInternal(messageId, callback, priority);
        }

        /// <summary>
        /// 订阅两个参数的消息。
        /// </summary>
        /// <typeparam name="T1">第一个参数类型。</typeparam>
        /// <typeparam name="T2">第二个参数类型。</typeparam>
        /// <param name="message">消息枚举。</param>
        /// <param name="callback">消息回调。</param>
        /// <param name="priority">优先级，数值越大越先触发。</param>
        /// <returns>用于取消本次订阅的句柄。</returns>
        public EventSubscription Subscribe<T1, T2>(GameMessage message, Action<T1, T2> callback, int priority = 0)
        {
            return Subscribe((int)message, callback, priority);
        }

        /// <summary>
        /// 订阅两个参数的消息。业务热更程序集可自建枚举/常量后转为 int 传入。
        /// </summary>
        /// <typeparam name="T1">第一个参数类型。</typeparam>
        /// <typeparam name="T2">第二个参数类型。</typeparam>
        /// <param name="messageId">统一消息 ID。</param>
        /// <param name="callback">消息回调。</param>
        /// <param name="priority">优先级，数值越大越先触发。</param>
        /// <returns>用于取消本次订阅的句柄。</returns>
        public EventSubscription Subscribe<T1, T2>(int messageId, Action<T1, T2> callback, int priority = 0)
        {
            return SubscribeInternal(messageId, callback, priority);
        }

        /// <summary>
        /// 订阅 object[] 自由参数消息，用于少量兼容广播场景。
        /// </summary>
        /// <param name="message">消息枚举。</param>
        /// <param name="callback">消息回调。</param>
        /// <param name="priority">优先级，数值越大越先触发。</param>
        /// <returns>用于取消本次订阅的句柄。</returns>
        public EventSubscription SubscribeArgs(GameMessage message, Action<object[]> callback, int priority = 0)
        {
            return SubscribeArgs((int)message, callback, priority);
        }

        /// <summary>
        /// 订阅 object[] 自由参数消息，用于少量兼容广播场景。
        /// </summary>
        /// <param name="messageId">统一消息 ID。</param>
        /// <param name="callback">消息回调。</param>
        /// <param name="priority">优先级，数值越大越先触发。</param>
        /// <returns>用于取消本次订阅的句柄。</returns>
        public EventSubscription SubscribeArgs(int messageId, Action<object[]> callback, int priority = 0)
        {
            return SubscribeInternal(messageId, callback, priority);
        }

        /// <summary>
        /// 发布无参数消息。
        /// </summary>
        /// <param name="message">消息枚举。</param>
        public void Publish(GameMessage message)
        {
            Publish((int)message);
        }

        /// <summary>
        /// 发布无参数消息。业务热更程序集可自建枚举/常量后转为 int 传入。
        /// </summary>
        /// <param name="messageId">统一消息 ID。</param>
        public void Publish(int messageId)
        {
            PublishInternal(messageId, action => ((Action)action).Invoke(), typeof(Action));
        }

        /// <summary>
        /// 发布一个参数的消息。
        /// </summary>
        /// <typeparam name="T">参数类型。</typeparam>
        /// <param name="message">消息枚举。</param>
        /// <param name="arg">消息参数。</param>
        public void Publish<T>(GameMessage message, T arg)
        {
            Publish((int)message, arg);
        }

        /// <summary>
        /// 发布一个参数的消息。业务热更程序集可自建枚举/常量后转为 int 传入。
        /// </summary>
        /// <typeparam name="T">参数类型。</typeparam>
        /// <param name="messageId">统一消息 ID。</param>
        /// <param name="arg">消息参数。</param>
        public void Publish<T>(int messageId, T arg)
        {
            PublishInternal(messageId, action => ((Action<T>)action).Invoke(arg), typeof(Action<T>));
        }

        /// <summary>
        /// 发布两个参数的消息。
        /// </summary>
        /// <typeparam name="T1">第一个参数类型。</typeparam>
        /// <typeparam name="T2">第二个参数类型。</typeparam>
        /// <param name="message">消息枚举。</param>
        /// <param name="arg1">第一个消息参数。</param>
        /// <param name="arg2">第二个消息参数。</param>
        public void Publish<T1, T2>(GameMessage message, T1 arg1, T2 arg2)
        {
            Publish((int)message, arg1, arg2);
        }

        /// <summary>
        /// 发布两个参数的消息。业务热更程序集可自建枚举/常量后转为 int 传入。
        /// </summary>
        /// <typeparam name="T1">第一个参数类型。</typeparam>
        /// <typeparam name="T2">第二个参数类型。</typeparam>
        /// <param name="messageId">统一消息 ID。</param>
        /// <param name="arg1">第一个消息参数。</param>
        /// <param name="arg2">第二个消息参数。</param>
        public void Publish<T1, T2>(int messageId, T1 arg1, T2 arg2)
        {
            PublishInternal(
                messageId,
                action => ((Action<T1, T2>)action).Invoke(arg1, arg2),
                typeof(Action<T1, T2>));
        }

        /// <summary>
        /// 发布 object[] 自由参数消息。
        /// </summary>
        /// <param name="message">消息枚举。</param>
        /// <param name="args">自由参数列表。</param>
        public void PublishArgs(GameMessage message, params object[] args)
        {
            PublishArgs((int)message, args);
        }

        /// <summary>
        /// 发布 object[] 自由参数消息。
        /// </summary>
        /// <param name="messageId">统一消息 ID。</param>
        /// <param name="args">自由参数列表。</param>
        public void PublishArgs(int messageId, params object[] args)
        {
            PublishInternal(messageId, action => ((Action<object[]>)action).Invoke(args), typeof(Action<object[]>));
        }

        /// <summary>
        /// 清除所有事件监听器。
        /// </summary>
        public void Clear()
        {
            foreach (List<EventListener> listeners in eventListeners.Values)
            {
                for (int i = 0; i < listeners.Count; i++)
                {
                    listeners[i].IsActive = false;
                }
            }

            eventListeners.Clear();
            triggeringEvents.Clear();
            needSortEvents.Clear();
            snapshotPool.Clear();
            GameLog.Debug("[EventManager] 清除所有事件监听器");
        }

        /// <summary>
        /// 添加事件订阅。
        /// </summary>
        /// <param name="messageId">统一消息 ID。</param>
        /// <param name="callback">事件回调。</param>
        /// <param name="priority">优先级，数值越大越先触发。</param>
        /// <returns>用于取消本次订阅的句柄。</returns>
        private EventSubscription SubscribeInternal(int messageId, Delegate callback, int priority)
        {
            if (callback == null)
            {
                GameLog.Warning($"[EventManager] 订阅消息失败，回调为 null，消息: {GetMessageName(messageId)}");
                return EventSubscription.CreateDisposed();
            }

            var listener = new EventListener
            {
                Id = ++nextListenerId,
                MessageId = messageId,
                Callback = callback,
                Priority = priority
            };

            if (!eventListeners.TryGetValue(messageId, out List<EventListener> listeners))
            {
                listeners = new List<EventListener>();
                eventListeners[messageId] = listeners;
            }

            listeners.Add(listener);
            needSortEvents.Add(messageId);
            return new EventSubscription(() => RemoveListener(messageId, listener));
        }

        /// <summary>
        /// 发布事件。
        /// </summary>
        /// <param name="messageId">统一消息 ID。</param>
        /// <param name="invoker">回调调用器。</param>
        /// <param name="expectedDelegateType">期望的委托类型。</param>
        private void PublishInternal(int messageId, Action<Delegate> invoker, Type expectedDelegateType)
        {
            if (triggeringEvents.Contains(messageId))
            {
                GameLog.Warning($"[EventManager] 检测到消息循环触发，已阻止: {GetMessageName(messageId)}");
                return;
            }

            if (!eventListeners.TryGetValue(messageId, out List<EventListener> listeners) || listeners.Count == 0)
            {
                return;
            }

            triggeringEvents.Add(messageId);
            SortListenersIfNeeded(messageId);

            // 用池化的快照列表代替 ToArray，遍历期间允许监听器增删而不影响本次派发
            List<EventListener> snapshot = RentSnapshot();
            snapshot.AddRange(listeners);
            try
            {
                for (int i = 0; i < snapshot.Count; i++)
                {
                    EventListener listener = snapshot[i];
                    if (!listener.IsActive)
                    {
                        continue;
                    }

                    if (listener.Callback.GetType() != expectedDelegateType)
                    {
                        GameLog.Warning($"[EventManager] 跳过消息回调，参数签名不匹配: {GetMessageName(messageId)}, 期望={expectedDelegateType.Name}, 实际={listener.Callback.GetType().Name}");
                        continue;
                    }

                    try
                    {
                        invoker(listener.Callback);
                    }
                    catch (Exception ex)
                    {
                        GameLog.Error($"[EventManager] 触发消息回调异常: {GetMessageName(messageId)}, 错误: {ex.Message}\n{ex.StackTrace}");
                    }
                }
            }
            finally
            {
                ReturnSnapshot(snapshot);
                triggeringEvents.Remove(messageId);
            }
        }

        /// <summary>
        /// 移除指定订阅。
        /// </summary>
        /// <param name="messageId">统一消息 ID。</param>
        /// <param name="listener">要移除的监听器。</param>
        private void RemoveListener(int messageId, EventListener listener)
        {
            listener.IsActive = false;
            if (!eventListeners.TryGetValue(messageId, out List<EventListener> listeners))
            {
                return;
            }

            listeners.Remove(listener);
            if (listeners.Count == 0)
            {
                eventListeners.Remove(messageId);
                needSortEvents.Remove(messageId);
            }

        }

        /// <summary>
        /// 在需要时按优先级和订阅顺序排序。
        /// </summary>
        /// <param name="messageId">统一消息 ID。</param>
        private void SortListenersIfNeeded(int messageId)
        {
            if (!needSortEvents.Contains(messageId))
            {
                return;
            }

            if (eventListeners.TryGetValue(messageId, out List<EventListener> listeners))
            {
                listeners.Sort(CompareListener);
            }

            needSortEvents.Remove(messageId);
        }

        /// <summary>
        /// 租用一个已清空的派发快照列表。
        /// </summary>
        /// <returns>可写入的快照列表。</returns>
        private List<EventListener> RentSnapshot()
        {
            return snapshotPool.Count > 0 ? snapshotPool.Pop() : new List<EventListener>(8);
        }

        /// <summary>
        /// 归还派发快照列表以供复用。
        /// </summary>
        /// <param name="snapshot">使用完毕的快照列表。</param>
        private void ReturnSnapshot(List<EventListener> snapshot)
        {
            if (snapshot == null)
            {
                return;
            }

            snapshot.Clear();
            snapshotPool.Push(snapshot);
        }

        /// <summary>
        /// 比较两个监听器的触发顺序。
        /// </summary>
        /// <param name="left">左侧监听器。</param>
        /// <param name="right">右侧监听器。</param>
        /// <returns>排序比较结果。</returns>
        private static int CompareListener(EventListener left, EventListener right)
        {
            int priorityResult = right.Priority.CompareTo(left.Priority);
            return priorityResult != 0 ? priorityResult : left.Id.CompareTo(right.Id);
        }

        /// <summary>
        /// 获取消息名称，用于日志输出。
        /// </summary>
        /// <param name="messageId">统一消息 ID。</param>
        /// <returns>消息名称。</returns>
        private static string GetMessageName(int messageId)
        {
            string enumName = Enum.GetName(typeof(GameMessage), messageId);
            return string.IsNullOrEmpty(enumName)
                ? $"UnknownMessage({messageId})"
                : $"{nameof(GameMessage)}.{enumName} ({messageId})";
        }

        /// <summary>
        /// 事件监听器记录。
        /// </summary>
        private sealed class EventListener
        {
            /// <summary>订阅唯一序号。</summary>
            public long Id;

            /// <summary>统一消息 ID。</summary>
            public int MessageId;

            /// <summary>回调函数。</summary>
            public Delegate Callback;

            /// <summary>优先级，数值越大越先触发。</summary>
            public int Priority;

            /// <summary>监听器是否仍然有效。</summary>
            public bool IsActive = true;
        }
    }
}
