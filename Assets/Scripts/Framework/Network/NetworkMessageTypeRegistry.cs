using System;
using System.Collections.Generic;
using Google.Protobuf;

namespace Framework.Network
{
    /// <summary>
    /// 网络协议类型注册表，负责记录协议号与运行时解析器的映射。
    /// 采用惰性显式登记：每次 <c>Subscribe&lt;T&gt;</c>/<c>RequestAsync&lt;TResp&gt;</c> 都以具体类型 T 调用 <see cref="Register{T}"/>，
    /// 解析器闭包内 <c>new T()</c> + <c>MergeFrom</c>，全程无反射（不用 Activator/MakeGenericMethod），保证 IL2CPP(AOT) 安全。
    /// </summary>
    internal sealed class NetworkMessageTypeRegistry
    {
        /// <summary>响应协议解析器字典，Key 为完整协议消息 ID。</summary>
        private readonly Dictionary<ushort, Func<byte[], IResponse>> _responseParsers =
            new Dictionary<ushort, Func<byte[], IResponse>>();

        /// <summary>普通协议解析器字典，Key 为完整协议消息 ID，用于协议日志还原消息字段。</summary>
        private readonly Dictionary<ushort, Func<byte[], INetMessage>> _messageParsers =
            new Dictionary<ushort, Func<byte[], INetMessage>>();

        /// <summary>协议显示名字典，Key 为完整协议消息 ID，用于未知 payload 时仍能打印协议名。</summary>
        private readonly Dictionary<ushort, string> _messageNames =
            new Dictionary<ushort, string>();

        /// <summary>类型注册锁，保护运行期订阅和请求并发登记。</summary>
        private readonly object _lock = new object();

        /// <summary>
        /// 登记协议类型。如果类型实现了 <see cref="IResponse"/>，则额外登记错误码解析器。
        /// 以具体类型 T 登记，解析走 <c>new T()</c> + <c>MergeFrom</c>，AOT 安全。
        /// </summary>
        /// <typeparam name="T">协议消息类型。</typeparam>
        public void Register<T>() where T : class, INetMessage, new()
        {
            T prototype = new T();
            byte mainId = prototype.GetMainId();
            byte subId = prototype.GetSubId();
            ushort messageId = MessagePacket.CombineMessageId(mainId, subId);
            string typeName = typeof(T).Name;
            bool isResponse = prototype is IResponse;

            lock (_lock)
            {
                _messageParsers[messageId] = ParseTyped<T>;
                _messageNames[messageId] = typeName;

                if (isResponse)
                {
                    _responseParsers[messageId] = payload => (IResponse)ParseTyped<T>(payload);
                }
            }
        }

        /// <summary>
        /// 以具体类型解析 Protobuf 消息。空 payload 视为字段默认值消息。全程无反射。
        /// </summary>
        /// <typeparam name="T">协议消息类型。</typeparam>
        /// <param name="payload">协议消息体字节数据。</param>
        /// <returns>解析出的协议消息对象。</returns>
        private static INetMessage ParseTyped<T>(byte[] payload) where T : class, INetMessage, new()
        {
            var message = new T();
            if (payload != null && payload.Length > 0)
            {
                message.MergeFrom(payload);
            }

            return message;
        }

        /// <summary>
        /// 尝试把协议消息解析为普通消息对象，主要用于协议日志字段展开。
        /// </summary>
        /// <param name="mainId">主消息 ID。</param>
        /// <param name="subId">子消息 ID。</param>
        /// <param name="payload">协议消息体字节数据。</param>
        /// <param name="message">解析出的协议消息对象。</param>
        /// <returns>存在解析器且解析成功时返回 true。</returns>
        public bool TryParseMessage(byte mainId, byte subId, byte[] payload, out INetMessage message)
        {
            message = null;
            ushort messageId = MessagePacket.CombineMessageId(mainId, subId);
            Func<byte[], INetMessage> parser;
            lock (_lock)
            {
                if (!_messageParsers.TryGetValue(messageId, out parser))
                {
                    return false;
                }
            }

            message = parser(payload);
            return message != null;
        }

        /// <summary>
        /// 获取协议显示名。未登记时返回主/子协议号组合。
        /// </summary>
        /// <param name="mainId">主消息 ID。</param>
        /// <param name="subId">子消息 ID。</param>
        /// <returns>协议类型名或未知协议描述。</returns>
        public string GetMessageName(byte mainId, byte subId)
        {
            ushort messageId = MessagePacket.CombineMessageId(mainId, subId);
            lock (_lock)
            {
                if (_messageNames.TryGetValue(messageId, out string name))
                {
                    return name;
                }
            }

            return $"Unknown_{mainId}_{subId}";
        }

        /// <summary>
        /// 尝试把协议消息解析为响应消息。
        /// </summary>
        /// <param name="mainId">主消息 ID。</param>
        /// <param name="subId">子消息 ID。</param>
        /// <param name="payload">协议消息体字节数据。</param>
        /// <param name="response">解析出的响应消息。</param>
        /// <returns>存在响应解析器且解析成功时返回 true。</returns>
        public bool TryParseResponse(byte mainId, byte subId, byte[] payload, out IResponse response)
        {
            response = null;
            ushort messageId = MessagePacket.CombineMessageId(mainId, subId);
            Func<byte[], IResponse> parser;
            lock (_lock)
            {
                if (!_responseParsers.TryGetValue(messageId, out parser))
                {
                    return false;
                }
            }

            response = parser(payload);
            return response != null;
        }

        /// <summary>
        /// 清空所有类型解析器，通常在网络管理器关闭时调用。
        /// </summary>
        public void Clear()
        {
            lock (_lock)
            {
                _responseParsers.Clear();
                _messageParsers.Clear();
                _messageNames.Clear();
            }
        }
    }
}
