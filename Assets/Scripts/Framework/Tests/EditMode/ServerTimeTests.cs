using NUnit.Framework;

namespace Framework.Tests
{
    /// <summary>
    /// ServerTime 服务器校时单元测试：样本采纳/拒绝规则与偏移计算。
    /// </summary>
    public class ServerTimeTests
    {
        [SetUp]
        public void SetUp()
        {
            // ServerTime 是静态状态，每条用例前重置，避免用例间互相污染
            ServerTime.Reset();
        }

        [TearDown]
        public void TearDown()
        {
            ServerTime.Reset();
        }

        [Test]
        public void 未同步时_回退本地时间且偏移为零()
        {
            Assert.IsFalse(ServerTime.IsSynchronized);
            Assert.AreEqual(0, ServerTime.OffsetMs);
            Assert.AreEqual(0, ServerTime.RttMs);

            long local = System.DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            Assert.LessOrEqual(System.Math.Abs(ServerTime.NowMs - local), 100);
        }

        [Test]
        public void 首个样本_建立偏移与RTT()
        {
            // 服务端领先本地 5000ms，RTT 200ms：offset = server + rtt/2 - recv
            ServerTime.AddSample(serverTimeMs: 15_000, sentLocalMs: 9_900, receivedLocalMs: 10_100);

            Assert.IsTrue(ServerTime.IsSynchronized);
            Assert.AreEqual(15_000 + 100 - 10_100, ServerTime.OffsetMs); // = 5000
            Assert.AreEqual(200, ServerTime.RttMs);
        }

        [Test]
        public void RTT明显劣化的样本_不更新偏移()
        {
            ServerTime.AddSample(15_000, 9_900, 10_100); // rtt=200, offset=5000
            long offsetBefore = ServerTime.OffsetMs;

            // rtt=2000 > 200*1.5，偏移不应被这个高噪声样本改写
            ServerTime.AddSample(99_000, 20_000, 22_000);

            Assert.AreEqual(offsetBefore, ServerTime.OffsetMs);
        }

        [Test]
        public void 更优RTT样本_更新偏移()
        {
            ServerTime.AddSample(15_000, 9_900, 10_100);  // rtt=200
            ServerTime.AddSample(25_050, 20_000, 20_100); // rtt=100，更优 → 采纳

            Assert.AreEqual(25_050 + 50 - 20_100, ServerTime.OffsetMs); // = 5000
            Assert.AreEqual(100, ServerTime.RttMs);
        }

        [Test]
        public void 非法样本_被忽略()
        {
            ServerTime.AddSample(0, 100, 200);            // 服务端时间非法
            ServerTime.AddSample(15_000, 300, 200);       // rtt < 0（时钟回拨）
            ServerTime.AddSample(15_000, 0, 20_000);      // rtt 超过上限

            Assert.IsFalse(ServerTime.IsSynchronized);
        }

        [Test]
        public void Reset后_回到未同步状态()
        {
            ServerTime.AddSample(15_000, 9_900, 10_100);
            Assert.IsTrue(ServerTime.IsSynchronized);

            ServerTime.Reset();

            Assert.IsFalse(ServerTime.IsSynchronized);
            Assert.AreEqual(0, ServerTime.OffsetMs);
        }
    }
}
