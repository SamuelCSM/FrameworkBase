using System;
using System.Collections.Generic;
using System.Reflection;

namespace Framework.Network
{
    /// <summary>
    /// 网络协议类型注册表，负责记录协议号与运行时解析器的映射。
    /// </summary>
    internal sealed class NetworkMessageTypeRegistry
    {
        /// <summary>响应协议解析器字典，Key 为完整协议消息 ID。</summary>
        private readonly Dictionary<ushort, Func<byte[], IResponse>> _responseParsers =
            new Dictionary<ushort, Func<byte[], IResponse>>();

        /// <summary>普通协议解析器字典，Key 为完整协议消息 ID，用于协议日志还原消息字段。</summary>
        private readonly Dictionary<ushort, Func<byte[], IMessage>> _messageParsers =
            new Dictionary<ushort, Func<byte[], IMessage>>();

        /// <summary>协议显示名字典，Key 为完整协议消息 ID，用于未知 payload 时仍能打印协议名。</summary>
        private readonly Dictionary<ushort, string> _messageNames =
            new Dictionary<ushort, string>();

        /// <summary>Protobuf 泛型反序列化方法缓存，减少协议日志解析时的反射查找。</summary>
        private static readonly MethodInfo DeserializeMethod =
            typeof(ProtobufUtil).GetMethod(nameof(ProtobufUtil.Deserialize), BindingFlags.Public | BindingFlags.Static);

        /// <summary>类型注册锁，保护运行期订阅和请求并发登记。</summary>
        private readonly object _lock = new object();

        /// <summary>
        /// 登记协议类型。如果类型实现了 <see cref="IResponse"/>，则额外登记错误码解析器。
        /// </summary>
        /// <typeparam name="T">协议消息类型。</typeparam>
        public void Register<T>() where T : class, IMessage, new()
        {
            T prototype = new T();
            RegisterType(typeof(T), prototype, true);
        }

        /// <summary>
        /// 扫描当前已加载程序集中的协议类型，提前登记日志解析器。
        /// 后续热更程序集加载出的类型仍会在 Subscribe/Request 时补充登记。
        /// </summary>
        public void RegisterLoadedMessageTypes()
        {
            Assembly[] assemblies = AppDomain.CurrentDomain.GetAssemblies();
            for (int i = 0; i < assemblies.Length; i++)
            {
                Type[] types;
                try
                {
                    types = assemblies[i].GetTypes();
                }
                catch (ReflectionTypeLoadException ex)
                {
                    types = ex.Types;
                }
                catch
                {
                    continue;
                }

                if (types == null)
                {
                    continue;
                }

                for (int j = 0; j < types.Length; j++)
                {
                    RegisterType(types[j], null, false);
                }
            }
        }

        /// <summary>
        /// 尝试把协议消息解析为普通消息对象，主要用于协议日志字段展开。
        /// </summary>
        /// <param name="mainId">主消息 ID。</param>
        /// <param name="subId">子消息 ID。</param>
        /// <param name="payload">协议消息体字节数据。</param>
        /// <param name="message">解析出的协议消息对象。</param>
        /// <returns>存在解析器且解析成功时返回 true。</returns>
        public bool TryParseMessage(byte mainId, byte subId, byte[] payload, out IMessage message)
        {
            message = null;
            ushort messageId = MessagePacket.CombineMessageId(mainId, subId);
            Func<byte[], IMessage> parser;
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
        /// 登记单个协议类型，并建立协议号到类型解析器的映射。
        /// </summary>
        /// <param name="type">协议类型。</param>
        /// <param name="prototype">已创建的协议实例，可避免重复构造。</param>
        /// <param name="force">是否覆盖既有协议号映射，显式注册需要覆盖自动扫描结果。</param>
        private void RegisterType(Type type, IMessage prototype, bool force)
        {
            if (type == null
                || !typeof(IMessage).IsAssignableFrom(type)
                || type.IsInterface
                || type.IsAbstract
                || type.GetConstructor(Type.EmptyTypes) == null)
            {
                return;
            }

            IMessage instance = prototype;
            if (instance == null)
            {
                try
                {
                    instance = (IMessage)Activator.CreateInstance(type);
                }
                catch
                {
                    return;
                }
            }

            byte mainId = instance.GetMainId();
            byte subId = instance.GetSubId();
            ushort messageId = MessagePacket.CombineMessageId(mainId, subId);

            lock (_lock)
            {
                if (force || !_messageParsers.ContainsKey(messageId) || IsServerToClientMessage(type))
                {
                    _messageParsers[messageId] = payload => ParseMessage(type, payload);
                    _messageNames[messageId] = type.Name;
                }

                if (typeof(IResponse).IsAssignableFrom(type) && (force || !_responseParsers.ContainsKey(messageId)))
                {
                    _responseParsers[messageId] = payload => ParseMessage(type, payload) as IResponse;
                }
            }
        }

        /// <summary>
        /// 按运行时类型解析 Protobuf 消息。空 payload 视为字段默认值消息。
        /// </summary>
        /// <param name="type">协议类型。</param>
        /// <param name="payload">协议消息体字节数据。</param>
        /// <returns>解析出的协议消息对象。</returns>
        private static IMessage ParseMessage(Type type, byte[] payload)
        {
            if (payload == null || payload.Length == 0)
            {
                return (IMessage)Activator.CreateInstance(type);
            }

            if (DeserializeMethod == null)
            {
                return null;
            }

            return DeserializeMethod.MakeGenericMethod(type).Invoke(null, new object[] { payload }) as IMessage;
        }

        /// <summary>
        /// 判断协议类型是否为服务端下发到客户端的消息，客户端 RECV 日志同协议号时优先使用该方向解析。
        /// </summary>
        /// <param name="type">协议类型。</param>
        /// <returns>属于服务端下行命名空间时返回 true。</returns>
        private static bool IsServerToClientMessage(Type type)
        {
            return type != null
                   && type.Namespace != null
                   && type.Namespace.IndexOf(".GS2GC", StringComparison.Ordinal) >= 0;
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
