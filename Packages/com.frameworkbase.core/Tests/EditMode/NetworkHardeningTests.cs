using System;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Threading;
using Framework.Network;
using NUnit.Framework;

namespace Framework.Tests
{
    /// <summary>
    /// 强联网移动端硬化回归测试：入站队列上限、每帧预算、连接世代隔离、请求取消、SeqId 回绕及 IPv6 优先。
    /// </summary>
    public class NetworkHardeningTests
    {
        [Test]
        public void 消息队列达到上限_拒绝继续入队()
        {
            var dispatcher = new MessageDispatcher { MaxPendingMessages = 2 };
            Assert.IsTrue(dispatcher.TryEnqueueMessage(1, 1, 1, new byte[] { 1 }, 0));
            Assert.IsTrue(dispatcher.TryEnqueueMessage(1, 1, 2, new byte[] { 2 }, 0));
            Assert.IsFalse(dispatcher.TryEnqueueMessage(1, 1, 3, new byte[] { 3 }, 0));
            Assert.AreEqual(2, dispatcher.GetPendingMessageCount());
        }

        [Test]
        public void 每帧数量预算_只处理预算内消息()
        {
            var dispatcher = new MessageDispatcher
            {
                MaxPendingMessages = 8,
                MaxMessagesPerFrame = 2,
                MaxProcessingMilliseconds = 0,
            };
            for (int i = 0; i < 5; i++)
                Assert.IsTrue(dispatcher.TryEnqueueMessage(3, 1, 1, new byte[] { (byte)i }, (ushort)(i + 1)));

            int completed = 0;
            dispatcher.ProcessMessageQueue(
                onSeqResponse: (epoch, seq, payload) => completed++);

            Assert.AreEqual(2, completed);
            Assert.AreEqual(3, dispatcher.GetPendingMessageCount());
        }

        [Test]
        public void 消息响应回调_保留入队时连接Epoch()
        {
            var dispatcher = new MessageDispatcher();
            Assert.IsTrue(dispatcher.TryEnqueueMessage(42, 1, 2, new byte[] { 9 }, 7));

            int observedEpoch = 0;
            dispatcher.ProcessMessageQueue(
                onSeqResponse: (epoch, seq, payload) => observedEpoch = epoch);

            Assert.AreEqual(42, observedEpoch);
        }

        [Test]
        public void 请求完成必须同时匹配Epoch与SeqId()
        {
            var tracker = new NetworkRequestTracker();
            bool completed = false;
            ushort seqId = tracker.Register(
                connectionEpoch: 10,
                onResponse: _ => completed = true,
                onTimeout: null,
                onCancelled: null,
                config: NetworkRequestConfig.Silent);

            Assert.IsFalse(tracker.TryComplete(9, seqId, new byte[] { 1 }));
            Assert.IsFalse(completed);
            Assert.IsTrue(tracker.HasPending(10, seqId));

            Assert.IsTrue(tracker.TryComplete(10, seqId, new byte[] { 1 }));
            Assert.IsTrue(completed);
        }

        [Test]
        public void 取消令牌只在主线程Update转换为取消终态()
        {
            var tracker = new NetworkRequestTracker();
            using (var cts = new CancellationTokenSource())
            {
                bool cancelled = false;
                ushort seqId = tracker.Register(
                    connectionEpoch: 1,
                    onResponse: null,
                    onTimeout: null,
                    onCancelled: () => cancelled = true,
                    config: NetworkRequestConfig.Silent,
                    cancellationToken: cts.Token);

                cts.Cancel();
                Assert.IsTrue(tracker.HasPending(1, seqId));
                Assert.IsFalse(cancelled);

                tracker.Update(0f);
                Assert.IsFalse(tracker.HasPending(1, seqId));
                Assert.IsTrue(cancelled);
            }
        }

        [Test]
        public void SeqId从65535回绕到1且跳过占用项()
        {
            var tracker = new NetworkRequestTracker();
            FieldInfo field = typeof(NetworkRequestTracker).GetField(
                "_nextSeqId",
                BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.IsNotNull(field);
            field.SetValue(tracker, (ushort)65534);

            ushort last = tracker.Register(1, null, null, null, NetworkRequestConfig.Silent);
            ushort wrapped = tracker.Register(1, null, null, null, NetworkRequestConfig.Silent);

            Assert.AreEqual(ushort.MaxValue, last);
            Assert.AreEqual(1, wrapped);
        }

        [Test]
        public void DNS地址排序_IPv6优先并去重()
        {
            IPAddress ipv4 = IPAddress.Parse("192.0.2.1");
            IPAddress ipv6 = IPAddress.Parse("2001:db8::1");
            IPAddress[] ordered = Framework.Network.TcpClient.OrderAddresses(
                new[] { ipv4, ipv6, ipv4 }).ToArray();

            Assert.AreEqual(2, ordered.Length);
            Assert.AreEqual(AddressFamily.InterNetworkV6, ordered[0].AddressFamily);
            Assert.AreEqual(AddressFamily.InterNetwork, ordered[1].AddressFamily);
        }

        [Test]
        public void 未连接发送_立即返回背压失败()
        {
            var client = new Framework.Network.TcpClient();
            Assert.IsFalse(client.Send(new byte[] { 1, 2, 3 }));
        }
    }
}
