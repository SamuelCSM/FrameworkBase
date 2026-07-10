using System;

namespace Framework.Network
{
    /// <summary>
    /// 消息包工具类
    /// 用于构建和解析网络消息包
    /// 消息格式：Length(4字节) + MainId(1字节) + SubId(1字节) + SeqId(2字节) + Payload(N字节)
    /// SeqId 用于请求-响应匹配：客户端发起请求时分配一个非零 SeqId，服务端响应原样回传。服务端主动推送消息 SeqId 固定为 0。
    /// </summary>
    public static class MessagePacket
    {
        /// <summary>
        /// 消息头大小（字节）
        /// </summary>
        public const int HeaderSize = 8;

        /// <summary>
        /// 构建消息包
        /// </summary>
        /// <param name="mainId">主消息ID（模块ID）</param>
        /// <param name="subId">子消息ID（消息类型ID）</param>
        /// <param name="payload">消息体（Protobuf序列化后的数据）</param>
        /// <param name="seqId">请求序列号，0 表示推送/通知消息。</param>
        /// <returns>完整的消息包</returns>
        public static byte[] Pack(byte mainId, byte subId, byte[] payload, ushort seqId = 0)
        {
            if (payload == null)
            {
                payload = new byte[0];
            }

            int totalLength = HeaderSize + payload.Length;
            byte[] packet = new byte[totalLength];

            // 写入消息长度（4字节）
            byte[] lengthBytes = BitConverter.GetBytes(totalLength);
            Array.Copy(lengthBytes, 0, packet, 0, 4);

            // 写入主消息ID（1字节）
            packet[4] = mainId;

            // 写入子消息ID（1字节）
            packet[5] = subId;

            // 写入 SeqId（2字节，小端序）
            packet[6] = (byte)(seqId & 0xFF);
            packet[7] = (byte)((seqId >> 8) & 0xFF);

            // 写入消息体
            if (payload.Length > 0)
            {
                Array.Copy(payload, 0, packet, HeaderSize, payload.Length);
            }

            return packet;
        }

        /// <summary>
        /// 构建消息包（从IMessage对象）
        /// </summary>
        /// <param name="message">消息对象</param>
        /// <param name="payload">消息体（Protobuf序列化后的数据）</param>
        /// <param name="seqId">请求序列号，0 表示推送/通知消息。</param>
        /// <returns>完整的消息包</returns>
        public static byte[] Pack(INetMessage message, byte[] payload, ushort seqId = 0)
        {
            return Pack(message.GetMainId(), message.GetSubId(), payload, seqId);
        }

        /// <summary>
        /// 解析消息包（含 SeqId）
        /// </summary>
        /// <param name="packet">完整的消息包</param>
        /// <param name="mainId">输出：主消息ID</param>
        /// <param name="subId">输出：子消息ID</param>
        /// <param name="seqId">输出：请求序列号</param>
        /// <param name="payload">输出：消息体</param>
        /// <returns>是否解析成功</returns>
        public static bool Unpack(byte[] packet, out byte mainId, out byte subId, out ushort seqId, out byte[] payload)
        {
            return Unpack(packet, packet?.Length ?? 0, out mainId, out subId, out seqId, out payload);
        }

        /// <summary>
        /// 解析消息包（含 SeqId），帧长度由调用方显式给出。
        /// <para>
        /// 供池化缓冲场景使用：ArrayPool 租用的数组实际长度通常大于帧长，不能用 packet.Length 判定协议边界。
        /// payload 始终为精确长度的新数组，调用方获得所有权，与传入缓冲的生命周期无关。
        /// </para>
        /// </summary>
        /// <param name="packet">承载完整帧的缓冲区，实际数组长度允许大于 <paramref name="packetLength"/>。</param>
        /// <param name="packetLength">帧的真实字节数。</param>
        public static bool Unpack(byte[] packet, int packetLength, out byte mainId, out byte subId, out ushort seqId, out byte[] payload)
        {
            mainId = 0;
            subId = 0;
            seqId = 0;
            payload = null;

            if (packet == null || packetLength < HeaderSize || packetLength > packet.Length)
            {
                return false;
            }

            // 读取消息长度
            int length = BitConverter.ToInt32(packet, 0);
            if (length != packetLength)
            {
                return false;
            }

            // 读取主消息ID
            mainId = packet[4];

            // 读取子消息ID
            subId = packet[5];

            // 读取 SeqId（2字节，小端序）
            seqId = (ushort)(packet[6] | (packet[7] << 8));

            // 读取消息体
            int payloadLength = packetLength - HeaderSize;
            if (payloadLength > 0)
            {
                payload = new byte[payloadLength];
                Array.Copy(packet, HeaderSize, payload, 0, payloadLength);
            }
            else
            {
                payload = new byte[0];
            }

            return true;
        }

        /// <summary>
        /// 解析消息包（兼容旧接口，不输出 SeqId）
        /// </summary>
        /// <param name="packet">完整的消息包</param>
        /// <param name="mainId">输出：主消息ID</param>
        /// <param name="subId">输出：子消息ID</param>
        /// <param name="payload">输出：消息体</param>
        /// <returns>是否解析成功</returns>
        public static bool Unpack(byte[] packet, out byte mainId, out byte subId, out byte[] payload)
        {
            return Unpack(packet, out mainId, out subId, out _, out payload);
        }

        /// <summary>
        /// 获取消息包中的 SeqId（不解析整个包）
        /// </summary>
        /// <param name="packet">消息包</param>
        /// <returns>SeqId，失败返回 0</returns>
        public static ushort GetSeqId(byte[] packet)
        {
            if (packet == null || packet.Length < HeaderSize) return 0;
            return (ushort)(packet[6] | (packet[7] << 8));
        }

        /// <summary>
        /// 获取主消息ID（不解析整个包）
        /// </summary>
        /// <param name="packet">消息包</param>
        /// <returns>主消息ID，失败返回0</returns>
        public static byte GetMainId(byte[] packet)
        {
            if (packet == null || packet.Length < HeaderSize)
            {
                return 0;
            }

            return packet[4];
        }

        /// <summary>
        /// 获取子消息ID（不解析整个包）
        /// </summary>
        /// <param name="packet">消息包</param>
        /// <returns>子消息ID，失败返回0</returns>
        public static byte GetSubId(byte[] packet)
        {
            if (packet == null || packet.Length < HeaderSize)
            {
                return 0;
            }

            return packet[5];
        }

        /// <summary>
        /// 获取完整消息ID（主ID和子ID组合）
        /// </summary>
        /// <param name="packet">消息包</param>
        /// <returns>完整消息ID（高8位为主ID，低8位为子ID）</returns>
        public static ushort GetMessageId(byte[] packet)
        {
            if (packet == null || packet.Length < HeaderSize)
            {
                return 0;
            }

            byte mainId = packet[4];
            byte subId = packet[5];
            return CombineMessageId(mainId, subId);
        }

        /// <summary>
        /// 组合主ID和子ID为完整消息ID
        /// </summary>
        /// <param name="mainId">主消息ID</param>
        /// <param name="subId">子消息ID</param>
        /// <returns>完整消息ID（高8位为主ID，低8位为子ID）</returns>
        public static ushort CombineMessageId(byte mainId, byte subId)
        {
            return (ushort)((mainId << 8) | subId);
        }

        /// <summary>
        /// 拆分完整消息ID为主ID和子ID
        /// </summary>
        /// <param name="messageId">完整消息ID</param>
        /// <param name="mainId">输出：主消息ID</param>
        /// <param name="subId">输出：子消息ID</param>
        public static void SplitMessageId(ushort messageId, out byte mainId, out byte subId)
        {
            mainId = (byte)(messageId >> 8);
            subId = (byte)(messageId & 0xFF);
        }

        /// <summary>
        /// 获取消息长度（不解析整个包）
        /// </summary>
        /// <param name="packet">消息包</param>
        /// <returns>消息长度，失败返回0</returns>
        public static int GetMessageLength(byte[] packet)
        {
            if (packet == null || packet.Length < 4)
            {
                return 0;
            }

            return BitConverter.ToInt32(packet, 0);
        }

        /// <summary>
        /// 验证消息包是否有效
        /// </summary>
        /// <param name="packet">消息包</param>
        /// <returns>是否有效</returns>
        public static bool IsValid(byte[] packet)
        {
            if (packet == null || packet.Length < HeaderSize)
            {
                return false;
            }

            int length = BitConverter.ToInt32(packet, 0);
            return length == packet.Length && length >= HeaderSize;
        }
    }
}
