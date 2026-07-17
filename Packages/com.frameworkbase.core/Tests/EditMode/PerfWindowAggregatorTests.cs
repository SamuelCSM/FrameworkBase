using Framework.Performance;
using NUnit.Framework;

namespace Framework.Tests
{
    /// <summary>
    /// 线上性能窗口聚合器单测：这是线上大盘卡顿率/内存水位的数字来源，
    /// 口径算错会让整个团队对线上健康度做出错误判断。
    /// </summary>
    public class PerfWindowAggregatorTests
    {
        [Test]
        public void 稳定帧率_窗口结算指标正确()
        {
            var agg = new PerfWindowAggregator(windowSeconds: 1f);
            const float dt = 1f / 50f;

            bool flushed = false;
            int fed = 0;
            for (int i = 0; i < 100 && !flushed; i++)
            {
                flushed = agg.Tick(dt);
                fed++;
            }

            Assert.IsTrue(flushed, "累计超过窗口长度应结算");
            var r = agg.LastReport;
            Assert.AreEqual(1, r.WindowIndex);
            Assert.AreEqual(fed, r.Frames);
            Assert.AreEqual(50f, r.AvgFps, 0.5f);
            Assert.AreEqual(dt * 1000f, r.WorstFrameMs, 0.1f);
            Assert.AreEqual(0, r.JankCount, "稳定帧不应计入卡顿");
            Assert.AreEqual(0, r.SevereJankCount);
        }

        [Test]
        public void 卡顿计数_严重卡顿同时计入两档()
        {
            var agg = new PerfWindowAggregator(windowSeconds: 1f, jankThresholdMs: 100f, severeJankThresholdMs: 500f);

            agg.Tick(0.12f); // 120ms：卡顿
            agg.Tick(0.6f);  // 600ms：严重卡顿（也计入卡顿总数）
            while (!agg.Tick(1f / 60f)) { }

            var r = agg.LastReport;
            Assert.AreEqual(2, r.JankCount, "卡顿计数应含严重卡顿帧");
            Assert.AreEqual(1, r.SevereJankCount);
            Assert.AreEqual(600f, r.WorstFrameMs, 0.1f);
        }

        [Test]
        public void 内存峰值_窗口内取最大_翻转清零()
        {
            var agg = new PerfWindowAggregator(windowSeconds: 1f);

            agg.SampleMemory(100, 300);
            agg.SampleMemory(200, 150);
            agg.SampleMemory(50, 250);
            while (!agg.Tick(1f / 60f)) { }

            var r = agg.LastReport;
            Assert.AreEqual(200, r.ManagedPeakBytes, "托管峰值取窗口内最大");
            Assert.AreEqual(300, r.NativePeakBytes, "Native 峰值独立取最大");

            // 第二窗口无采样：峰值不得残留
            while (!agg.Tick(1f / 60f)) { }
            Assert.AreEqual(0, agg.LastReport.ManagedPeakBytes, "上一窗口的峰值不得残留");
        }

        [Test]
        public void 窗口翻转_序号递增_计数不残留()
        {
            var agg = new PerfWindowAggregator(windowSeconds: 1f);

            agg.Tick(0.2f); // 第一窗口含卡顿
            while (!agg.Tick(1f / 60f)) { }
            Assert.AreEqual(1, agg.LastReport.WindowIndex);
            Assert.AreEqual(1, agg.LastReport.JankCount);

            while (!agg.Tick(1f / 60f)) { }
            Assert.AreEqual(2, agg.LastReport.WindowIndex, "窗口序号应递增");
            Assert.AreEqual(0, agg.LastReport.JankCount, "上一窗口的卡顿计数不得残留");
        }

        [Test]
        public void Reset_丢弃当前窗口_不产出报告_序号不消耗()
        {
            var agg = new PerfWindowAggregator(windowSeconds: 1f);

            agg.Tick(0.9f); // 接近满窗
            agg.SampleMemory(999, 999);
            agg.Reset();

            // Reset 后重新累计满一个窗口
            bool flushed = false;
            for (int i = 0; i < 100 && !flushed; i++)
                flushed = agg.Tick(1f / 50f);

            Assert.IsTrue(flushed);
            var r = agg.LastReport;
            Assert.AreEqual(1, r.WindowIndex, "被丢弃的半截窗口不应消耗序号");
            Assert.AreEqual(0, r.ManagedPeakBytes, "Reset 应清掉半截窗口的内存峰值");
            Assert.Less(r.WorstFrameMs, 100f, "Reset 前的 900ms 帧不得残留");
        }

        [Test]
        public void 非法帧_忽略不计()
        {
            var agg = new PerfWindowAggregator(windowSeconds: 1f);

            Assert.IsFalse(agg.Tick(0f));
            Assert.IsFalse(agg.Tick(-1f));

            bool flushed = false;
            for (int i = 0; i < 100 && !flushed; i++)
                flushed = agg.Tick(1f / 50f);
            Assert.IsTrue(flushed);
            Assert.AreEqual(50f, agg.LastReport.AvgFps, 0.5f, "非法帧不得污染均值");
        }

        [Test]
        public void 阈值防呆_严重阈值低于卡顿阈值时被抬升()
        {
            var agg = new PerfWindowAggregator(windowSeconds: 1f, jankThresholdMs: 100f, severeJankThresholdMs: 50f);

            agg.Tick(0.08f); // 80ms：低于卡顿阈值，也不该按 50ms 的错误严重阈值计数
            while (!agg.Tick(1f / 60f)) { }

            var r = agg.LastReport;
            Assert.AreEqual(0, r.JankCount);
            Assert.AreEqual(0, r.SevereJankCount, "严重阈值应被抬升到卡顿阈值，不得低于它");
        }
    }
}
