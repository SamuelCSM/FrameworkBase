using System;
using System.Collections.Generic;
using System.Diagnostics;

namespace Framework.Network
{
    /// <summary>
    /// 网络消息分发器，负责把协议消息分发给一个或多个订阅者。
    /// </summary>
    public class MessageDispatcher
    {
        /// <summary>
        /// 原始消息处理器委托。
        /// </summary>
        /// <param name="payload">协议消息体字节数据。</param>
        public delegate void MessageHandler(byte[] payload);

        /// <summary>
        /// 消息订阅者字典，Key 为完整消息 ID，Value 为该协议上的订阅者列表。
        /// </summary>
        private readonly Dictionary<ushort, List<HandlerEntry>> _handlers =
            new Dictionary<ushort, List<HandlerEntry>>();

        /// <summary>
        /// 订阅表读写锁，用于支持分发过程中安全增删监听。
        /// </summary>
        private readonly object _handlerLock = new object();

        /// <summary>
        /// 主线程消息队列。
        /// </summary>
        private readonly Queue<PendingMessage> _messageQueue = new Queue<PendingMessage>();

        /// <summary>
        /// 主线程消息队列锁。
        /// </summary>
        private readonly object _queueLock = new object();

        /// <summary>
        /// 后台收包线程允许积压到主线程队列的最大消息数；达到上限后拒绝入队，由 NetworkManager 主动断线以保护内存。
        /// </summary>
        public int MaxPendingMessages { get; set; } = 2048;

        /// <summary>
        /// 单帧最多分发的消息数量，与耗时预算共同构成主线程保护上限。
        /// </summary>
        public int MaxMessagesPerFrame { get; set; } = 256;

        /// <summary>
        /// 单帧消息分发最大耗时（毫秒）；小于等于 0 表示只使用数量预算。
        /// </summary>
        public double MaxProcessingMilliseconds { get; set; } = 4.0;

        /// <summary>
        /// 订阅快照列表池，避免每条消息分发时 ToArray 产生 GC。
        /// </summary>
        private readonly Stack<List<HandlerEntry>> _handlerSnapshotPool = new Stack<List<HandlerEntry>>();

        /// <summary>
        /// 递增订阅序号，用于同优先级下保持注册顺序稳定。
        /// </summary>
        private long _nextSubscriptionId;

        /// <summary>
        /// 待切回主线程处理的网络消息。
        /// </summary>
        private struct PendingMessage
        {
            /// <summary>
            /// 产生该消息的连接世代，用于拒绝旧连接延迟到达的数据。
            /// </summary>
            public int ConnectionEpoch;

            /// <summary>主消息 ID。</summary>
            public byte MainId;

            /// <summary>子消息 ID。</summary>
            public byte SubId;

            /// <summary>请求序列号（0 = 推送）。</summary>
            public ushort SeqId;

            /// <summary>协议消息体字节数据。</summary>
            public byte[] Payload;
        }

        /// <summary>
        /// 单个协议订阅者记录。
        /// </summary>
        private sealed class HandlerEntry
        {
            /// <summary>订阅唯一序号。</summary>
            public long Id;

            /// <summary>订阅优先级，值越大越先触发。</summary>
            public int Priority;

            /// <summary>协议回调函数。</summary>
            public MessageHandler Handler;

            /// <summary>订阅是否仍然有效，可能被其他线程释放订阅时改写。</summary>
            public volatile bool IsActive = true;
        }

        /// <summary>
        /// 订阅类型化网络消息，协议号从消息类型自身读取，并自动完成 Protobuf 反序列化。
        /// </summary>
        /// <typeparam name="T">反序列化后的协议消息类型。</typeparam>
        /// <param name="handler">类型化消息处理器。</param>
        /// <param name="priority">订阅优先级，值越大越先触发。</param>
        /// <returns>用于释放本次订阅的句柄。</returns>
        public MessageSubscription Subscribe<T>(Action<T> handler, int priority = 0)
            where T : class, INetMessage, new()
        {
            if (handler == null)
            {
                GameLog.Error("消息订阅处理器不能为空");
                return MessageSubscription.CreateDisposed();
            }

            T prototype = new T();
            byte mainId = prototype.GetMainId();
            byte subId = prototype.GetSubId();
            return Subscribe(mainId, subId, payload =>
            {
                try
                {
                    T message = ProtobufUtil.Deserialize<T>(payload);
                    handler(message);
                }
                catch (Exception ex)
                {
                    // 反序列化失败时外层多为包装异常，真因在 InnerException（如 IL2CPP 下缺 AOT 实例化）；一并打出避免被吞。
                    Exception root = ex.InnerException ?? ex;
                    GameLog.Error($"处理类型化消息失败: 主ID={mainId}, 子ID={subId}, 类型={typeof(T).Name}, 错误={ex.Message}, 内层={root.GetType().Name}:{root.Message}\n{root.StackTrace}");
                }
            }, priority);
        }

        /// <summary>
        /// 清除所有消息处理器。
        /// </summary>
        public void ClearAllHandlers()
        {
            lock (_handlerLock)
            {
                foreach (List<HandlerEntry> entries in _handlers.Values)
                {
                    for (int i = 0; i < entries.Count; i++)
                    {
                        entries[i].IsActive = false;
                    }
                }

                _handlers.Clear();
            }

            GameLog.Debug("已清除所有消息处理器");
        }

        /// <summary>
        /// 分发消息，立即在当前线程处理。
        /// </summary>
        /// <param name="mainId">主消息 ID。</param>
        /// <param name="subId">子消息 ID。</param>
        /// <param name="payload">协议消息体字节数据。</param>
        /// <param name="warnIfMissing">没有订阅者时是否输出警告。</param>
        public void DispatchMessage(byte mainId, byte subId, byte[] payload, bool warnIfMissing = true)
        {
            ushort msgId = MessagePacket.CombineMessageId(mainId, subId);
            List<HandlerEntry> snapshot = RentHandlerSnapshot();

            lock (_handlerLock)
            {
                if (!_handlers.TryGetValue(msgId, out List<HandlerEntry> entries) || entries.Count == 0)
                {
                    if (warnIfMissing)
                    {
                        GameLog.Warning($"未注册的消息: 主ID={mainId}, 子ID={subId}");
                    }

                    ReturnHandlerSnapshot(snapshot);
                    return;
                }

                for (int i = 0; i < entries.Count; i++)
                {
                    snapshot.Add(entries[i]);
                }
            }

            try
            {
                for (int i = 0; i < snapshot.Count; i++)
                {
                    HandlerEntry entry = snapshot[i];
                    if (!entry.IsActive)
                    {
                        continue;
                    }

                    try
                    {
                        entry.Handler(payload);
                    }
                    catch (Exception ex)
                    {
                        GameLog.Error($"处理消息时发生异常: 主ID={mainId}, 子ID={subId}, 订阅Id={entry.Id}, 错误={ex.Message}\n{ex.StackTrace}");
                    }
                }
            }
            finally
            {
                ReturnHandlerSnapshot(snapshot);
            }
        }

        /// <summary>
        /// 将消息加入主线程队列，用于从网络接收线程转发到 Unity 主线程处理。
        /// </summary>
        /// <param name="mainId">主消息 ID。</param>
        /// <param name="subId">子消息 ID。</param>
        /// <param name="payload">协议消息体字节数据。</param>
        /// <param name="seqId">请求序列号（0 = 推送）。</param>
        public void EnqueueMessage(byte mainId, byte subId, byte[] payload, ushort seqId = 0)
        {
            TryEnqueueMessage(connectionEpoch: 0, mainId, subId, payload, seqId);
        }

        public bool TryEnqueueMessage(
            int connectionEpoch,
            byte mainId,
            byte subId,
            byte[] payload,
            ushort seqId = 0)
        {
            lock (_queueLock)
            {
                if (_messageQueue.Count >= Math.Max(1, MaxPendingMessages))
                    return false;

                _messageQueue.Enqueue(new PendingMessage
                {
                    ConnectionEpoch = connectionEpoch,
                    MainId = mainId,
                    SubId = subId,
                    SeqId = seqId,
                    Payload = payload
                });
                return true;
            }
        }

        /// <summary>
        /// 处理主线程消息队列，应在 Unity 主线程的 Update 中调用。
        /// 处理顺序：先执行统一拦截，再走多播 Subscribe 分发，最后通知 seqId 回调。
        /// </summary>
        /// <param name="shouldIntercept">业务分发前的统一拦截回调，返回 true 表示消息已被消费。</param>
        /// <param name="onSeqResponse">seqId 响应回调，参数：seqId, payload。在 Subscribe 分发完成后调用。</param>
        /// <param name="onBeforeProcess">消息进入业务处理前的观察回调，常用于协议日志。</param>
        public void ProcessMessageQueue(
            Func<int, byte, byte, ushort, byte[], bool> shouldIntercept = null,
            Action<int, ushort, byte[]> onSeqResponse = null,
            Action<byte, byte, ushort, byte[]> onBeforeProcess = null)
        {
            int processed = 0;
            int maxMessages = Math.Max(1, MaxMessagesPerFrame);
            long startTicks = Stopwatch.GetTimestamp();

            while (processed < maxMessages)
            {
                PendingMessage msg;
                lock (_queueLock)
                {
                    if (_messageQueue.Count == 0) break;
                    msg = _messageQueue.Dequeue();
                }

                onBeforeProcess?.Invoke(msg.MainId, msg.SubId, msg.SeqId, msg.Payload);
                if (shouldIntercept == null ||
                    !shouldIntercept(msg.ConnectionEpoch, msg.MainId, msg.SubId, msg.SeqId, msg.Payload))
                {
                    DispatchMessage(msg.MainId, msg.SubId, msg.Payload, msg.SeqId == 0);
                    if (msg.SeqId > 0)
                        onSeqResponse?.Invoke(msg.ConnectionEpoch, msg.SeqId, msg.Payload);
                }

                processed++;
                if (MaxProcessingMilliseconds > 0)
                {
                    double elapsedMs = (Stopwatch.GetTimestamp() - startTicks) * 1000.0 / Stopwatch.Frequency;
                    if (elapsedMs >= MaxProcessingMilliseconds) break;
                }
            }
        }

        /// <summary>
        /// 获取待处理消息数量。
        /// </summary>
        /// <returns>主线程队列中等待处理的消息数量。</returns>
        public int GetPendingMessageCount()
        {
            lock (_queueLock)
            {
                return _messageQueue.Count;
            }
        }

        /// <summary>
        /// 检查某个协议是否至少有一个处理器。
        /// </summary>
        /// <param name="mainId">主消息 ID。</param>
        /// <param name="subId">子消息 ID。</param>
        /// <returns>存在处理器时返回 true。</returns>
        public bool HasHandler(byte mainId, byte subId)
        {
            ushort msgId = MessagePacket.CombineMessageId(mainId, subId);
            lock (_handlerLock)
            {
                return _handlers.TryGetValue(msgId, out List<HandlerEntry> entries) && entries.Count > 0;
            }
        }

        /// <summary>
        /// 获取已注册协议号数量。
        /// </summary>
        /// <returns>至少存在一个处理器的协议号数量。</returns>
        public int GetHandlerCount()
        {
            lock (_handlerLock)
            {
                return _handlers.Count;
            }
        }

        /// <summary>
        /// 获取某个协议上的订阅者数量。
        /// </summary>
        /// <param name="mainId">主消息 ID。</param>
        /// <param name="subId">子消息 ID。</param>
        /// <returns>该协议当前订阅者数量。</returns>
        public int GetSubscriberCount(byte mainId, byte subId)
        {
            ushort msgId = MessagePacket.CombineMessageId(mainId, subId);
            lock (_handlerLock)
            {
                return _handlers.TryGetValue(msgId, out List<HandlerEntry> entries) ? entries.Count : 0;
            }
        }

        /// <summary>
        /// 按优先级和注册顺序比较订阅者。
        /// </summary>
        /// <param name="left">左侧订阅者。</param>
        /// <param name="right">右侧订阅者。</param>
        /// <returns>排序比较结果。</returns>
        private static int CompareHandlerEntry(HandlerEntry left, HandlerEntry right)
        {
            int priorityResult = right.Priority.CompareTo(left.Priority);
            return priorityResult != 0 ? priorityResult : left.Id.CompareTo(right.Id);
        }

        /// <summary>
        /// 租用订阅快照列表。
        /// </summary>
        /// <returns>已清空的订阅快照列表。</returns>
        private List<HandlerEntry> RentHandlerSnapshot()
        {
            lock (_handlerSnapshotPool)
            {
                if (_handlerSnapshotPool.Count > 0)
                {
                    return _handlerSnapshotPool.Pop();
                }
            }

            return new List<HandlerEntry>(4);
        }

        /// <summary>
        /// 归还订阅快照列表。
        /// </summary>
        /// <param name="snapshot">订阅快照列表。</param>
        private void ReturnHandlerSnapshot(List<HandlerEntry> snapshot)
        {
            if (snapshot == null)
            {
                return;
            }

            snapshot.Clear();
            lock (_handlerSnapshotPool)
            {
                _handlerSnapshotPool.Push(snapshot);
            }
        }

        /// <summary>
        /// 订阅原始网络消息，供类型化订阅内部使用。
        /// </summary>
        /// <param name="mainId">主消息 ID。</param>
        /// <param name="subId">子消息 ID。</param>
        /// <param name="handler">原始字节处理器。</param>
        /// <param name="priority">订阅优先级，值越大越先触发。</param>
        /// <returns>用于释放本次订阅的句柄。</returns>
        private MessageSubscription Subscribe(byte mainId, byte subId, MessageHandler handler, int priority = 0)
        {
            if (handler == null)
            {
                GameLog.Error("消息订阅处理器不能为空");
                return MessageSubscription.CreateDisposed();
            }

            ushort msgId = MessagePacket.CombineMessageId(mainId, subId);
            HandlerEntry entry;
            lock (_handlerLock)
            {
                entry = new HandlerEntry
                {
                    Id = ++_nextSubscriptionId,
                    Priority = priority,
                    Handler = handler
                };

                if (!_handlers.TryGetValue(msgId, out List<HandlerEntry> entries))
                {
                    entries = new List<HandlerEntry>();
                    _handlers[msgId] = entries;
                }

                entries.Add(entry);
                entries.Sort(CompareHandlerEntry);
            }

            GameLog.Debug($"订阅消息: 主ID={mainId}, 子ID={subId}, Priority={priority}");
            return new MessageSubscription(() => RemoveHandler(msgId, entry));
        }

        /// <summary>
        /// 移除指定订阅者。
        /// </summary>
        /// <param name="msgId">完整协议消息 ID。</param>
        /// <param name="entry">要移除的订阅者。</param>
        private void RemoveHandler(ushort msgId, HandlerEntry entry)
        {
            lock (_handlerLock)
            {
                entry.IsActive = false;
                if (!_handlers.TryGetValue(msgId, out List<HandlerEntry> entries))
                {
                    return;
                }

                entries.Remove(entry);
                if (entries.Count == 0)
                {
                    _handlers.Remove(msgId);
                }
            }
        }

    }
}
