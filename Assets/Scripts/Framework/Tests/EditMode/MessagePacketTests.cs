using Framework.Network;
using NUnit.Framework;

namespace Framework.Tests
{
    /// <summary>
    /// MessagePacket 协议封包/拆包单元测试。
    /// 覆盖与服务端 ClientSession 必须一致的帧格式约定：
    /// 长度头 4 字节且含头长、SeqId 小端、MainId/SubId 位置，
    /// 这些也是 TcpClient 拆帧逻辑所依赖的不变量。
    /// </summary>
    public class MessagePacketTests
    {
        /// <summary>Pack 后再 Unpack 应原样还原 mainId/subId/seqId/payload。</summary>
        [Test]
        public void Pack_Then_Unpack_RoundtripsAllFields()
        {
            byte[] payload = { 10, 20, 30, 40, 50 };
            byte[] packet = MessagePacket.Pack(mainId: 2, subId: 3, payload: payload, seqId: 258);

            bool ok = MessagePacket.Unpack(packet, out byte mainId, out byte subId, out ushort seqId, out byte[] outPayload);

            Assert.IsTrue(ok, "合法包应解析成功");
            Assert.AreEqual(2, mainId);
            Assert.AreEqual(3, subId);
            Assert.AreEqual(258, seqId);
            Assert.AreEqual(payload, outPayload);
        }

        /// <summary>长度头必须等于整包长度（含 8 字节头），与服务端拆帧约定一致。</summary>
        [Test]
        public void Pack_LengthHeader_IncludesHeaderAndEqualsPacketLength()
        {
            byte[] payload = { 1, 2, 3 };
            byte[] packet = MessagePacket.Pack(1, 1, payload);

            Assert.AreEqual(MessagePacket.HeaderSize + payload.Length, packet.Length);
            Assert.AreEqual(packet.Length, MessagePacket.GetMessageLength(packet));
        }

        /// <summary>SeqId 以小端序写入第 6、7 字节。</summary>
        [Test]
        public void Pack_SeqId_IsLittleEndian()
        {
            byte[] packet = MessagePacket.Pack(0, 0, null, seqId: 0x0102);

            Assert.AreEqual(0x02, packet[6], "低字节在前");
            Assert.AreEqual(0x01, packet[7], "高字节在后");
            Assert.AreEqual(0x0102, MessagePacket.GetSeqId(packet));
        }

        /// <summary>payload 为 null 时应只产生 8 字节头包，且 payload 还原为空数组。</summary>
        [Test]
        public void Pack_NullPayload_ProducesHeaderOnlyPacket()
        {
            byte[] packet = MessagePacket.Pack(5, 6, null);

            Assert.AreEqual(MessagePacket.HeaderSize, packet.Length);

            MessagePacket.Unpack(packet, out _, out _, out _, out byte[] outPayload);
            Assert.IsNotNull(outPayload);
            Assert.AreEqual(0, outPayload.Length);
        }

        /// <summary>不足头长度的包应判定为无效并拒绝解析。</summary>
        [Test]
        public void Unpack_TooShortPacket_ReturnsFalse()
        {
            Assert.IsFalse(MessagePacket.Unpack(new byte[] { 1, 2, 3 }, out _, out _, out _, out _));
            Assert.IsFalse(MessagePacket.IsValid(new byte[] { 1, 2, 3 }));
        }

        /// <summary>长度头与实际长度不符的包应判定为无效。</summary>
        [Test]
        public void Unpack_LengthMismatch_ReturnsFalse()
        {
            byte[] packet = MessagePacket.Pack(1, 1, new byte[] { 9, 9 });
            packet[0] = 0xFF; // 篡改长度头

            Assert.IsFalse(MessagePacket.Unpack(packet, out _, out _, out _, out _));
            Assert.IsFalse(MessagePacket.IsValid(packet));
        }

        /// <summary>合法包应通过 IsValid 校验。</summary>
        [Test]
        public void IsValid_WellFormedPacket_ReturnsTrue()
        {
            byte[] packet = MessagePacket.Pack(7, 8, new byte[] { 1 });
            Assert.IsTrue(MessagePacket.IsValid(packet));
        }

        /// <summary>主/子 ID 组合与拆分应互为逆运算。</summary>
        [Test]
        public void CombineThenSplit_MessageId_Roundtrips()
        {
            ushort combined = MessagePacket.CombineMessageId(0xAB, 0xCD);
            Assert.AreEqual(0xABCD, combined);

            MessagePacket.SplitMessageId(combined, out byte mainId, out byte subId);
            Assert.AreEqual(0xAB, mainId);
            Assert.AreEqual(0xCD, subId);
        }

        /// <summary>免解析读取头部字段（MainId/SubId/MessageId）应与整包解析一致。</summary>
        [Test]
        public void PeekHeaderHelpers_MatchFullUnpack()
        {
            byte[] packet = MessagePacket.Pack(12, 34, new byte[] { 1, 2 }, seqId: 7);

            Assert.AreEqual(12, MessagePacket.GetMainId(packet));
            Assert.AreEqual(34, MessagePacket.GetSubId(packet));
            Assert.AreEqual(MessagePacket.CombineMessageId(12, 34), MessagePacket.GetMessageId(packet));
        }
    }
}
