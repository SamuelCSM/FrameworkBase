using NUnit.Framework;

namespace Framework.Tests
{
    /// <summary>
    /// 帧统计聚合器单测：窗口均值 FPS、最差帧捕捉、窗口翻转不残留、非法帧忽略。
    /// 这是性能 HUD 的数字来源，算错会误导整个团队的优化方向。
    /// </summary>
    public class FrameStatsAggregatorTests
    {
        [Test]
        public void 稳定帧率_窗口均值正确()
        {
            var agg = new FrameStatsAggregator(0.5f);
            const float dt = 1f / 60f;

            bool flushed = false;
            for (int i = 0; i < 60 && !flushed; i++)
                flushed = agg.Tick(dt);

            Assert.IsTrue(flushed, "累计超过窗口长度应结算");
            Assert.AreEqual(60f, agg.Fps, 0.5f);
            Assert.AreEqual(dt * 1000f, agg.WorstFrameMs, 0.1f);
        }

        [Test]
        public void 卡顿尖刺_最差帧被捕捉()
        {
            var agg = new FrameStatsAggregator(0.5f);

            for (int i = 0; i < 20; i++)
                agg.Tick(1f / 60f);
            agg.Tick(0.1f); // 100ms 尖刺
            bool flushed = false;
            for (int i = 0; i < 20 && !flushed; i++)
                flushed = agg.Tick(1f / 60f);

            Assert.IsTrue(flushed);
            Assert.AreEqual(100f, agg.WorstFrameMs, 0.1f, "均值掩盖不了的尖刺必须单独暴露");
            Assert.Less(agg.Fps, 60f, "尖刺应拉低窗口均值");
        }

        [Test]
        public void 窗口未满_不结算()
        {
            var agg = new FrameStatsAggregator(0.5f);

            for (int i = 0; i < 10; i++)
                Assert.IsFalse(agg.Tick(0.01f), "累计 0.1s 未达 0.5s 窗口不应结算");

            Assert.AreEqual(0f, agg.Fps, "未结算前读数保持初值");
        }

        [Test]
        public void 窗口翻转_尖刺不残留()
        {
            var agg = new FrameStatsAggregator(0.5f);

            // 第一窗口含尖刺
            agg.Tick(0.2f);
            while (!agg.Tick(1f / 60f)) { }
            Assert.AreEqual(200f, agg.WorstFrameMs, 0.1f);

            // 第二窗口全稳定帧
            while (!agg.Tick(1f / 60f)) { }
            Assert.AreEqual(1000f / 60f, agg.WorstFrameMs, 0.5f, "上一窗口的尖刺不得残留");
        }

        [Test]
        public void 非法帧_忽略不计()
        {
            var agg = new FrameStatsAggregator(0.5f);

            Assert.IsFalse(agg.Tick(0f));
            Assert.IsFalse(agg.Tick(-1f));

            // 只有合法帧参与统计
            bool flushed = false;
            for (int i = 0; i < 60 && !flushed; i++)
                flushed = agg.Tick(1f / 60f);
            Assert.IsTrue(flushed);
            Assert.AreEqual(60f, agg.Fps, 0.5f, "非法帧不得污染均值");
        }
    }
}
