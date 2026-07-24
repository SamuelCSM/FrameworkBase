using Framework.Performance;
using NUnit.Framework;

namespace Framework.Tests
{
    /// <summary>
    /// 设备分级器单测：分级决定低端机的画质与内存策略，
    /// 误判高会让低端机 OOM/卡成盘，误判低只是画质保守——规则必须偏保守。
    /// </summary>
    public class DeviceTierClassifierTests
    {
        [Test]
        public void 内存踩低端线_判低端()
        {
            var tier = DeviceTierClassifier.Classify(new DeviceProfile(3072, 2048, 8));
            Assert.AreEqual(DeviceTier.Low, tier, "3GB 内存应判低端，其余维度再好也不救");
        }

        [Test]
        public void 显存踩低端线_判低端()
        {
            var tier = DeviceTierClassifier.Classify(new DeviceProfile(8192, 1024, 8));
            Assert.AreEqual(DeviceTier.Low, tier, "1GB 显存应判低端（大内存集显机常见形态）");
        }

        [Test]
        public void 全维度达标_判高端()
        {
            var tier = DeviceTierClassifier.Classify(new DeviceProfile(8192, 4096, 8));
            Assert.AreEqual(DeviceTier.High, tier);
        }

        [Test]
        public void 高端判定_未知维度不参与否决()
        {
            var tier = DeviceTierClassifier.Classify(new DeviceProfile(8192, 0, 0));
            Assert.AreEqual(DeviceTier.High, tier, "显存/核数取不到（置 0）不应把达标内存拖成中端");
        }

        [Test]
        public void 高端判定_已知维度拖后腿_降中端()
        {
            var tier = DeviceTierClassifier.Classify(new DeviceProfile(8192, 4096, 4));
            Assert.AreEqual(DeviceTier.Mid, tier, "4 核不达高端线，应降中端");
        }

        [Test]
        public void 中间地带_判中端()
        {
            var tier = DeviceTierClassifier.Classify(new DeviceProfile(4096, 2048, 8));
            Assert.AreEqual(DeviceTier.Mid, tier);
        }

        [Test]
        public void 系统内存未知_封顶中端()
        {
            var tier = DeviceTierClassifier.Classify(new DeviceProfile(0, 8192, 16));
            Assert.AreEqual(DeviceTier.Mid, tier, "内存未知不得判高端——高端是要开销的，证据不足宁可保守");
        }

        [Test]
        public void 自定义阈值_生效()
        {
            var thresholds = new DeviceTierThresholds
            {
                LowMaxSystemMemoryMb = 2048,
                HighMinSystemMemoryMb = 4096,
            };

            Assert.AreEqual(DeviceTier.Mid, DeviceTierClassifier.Classify(new DeviceProfile(3072, 2048, 8), thresholds),
                "低端线降到 2GB 后 3GB 不再判低端");
            Assert.AreEqual(DeviceTier.High, DeviceTierClassifier.Classify(new DeviceProfile(4096, 2048, 8), thresholds),
                "高端线降到 4GB 后 4GB 判高端");
        }

        [Test]
        public void 预加载并发度_低端串行_高端多路()
        {
            // 低端必须退化为串行（=1），这是内存安全的底线，不能被调优拉高
            Assert.AreEqual(1, DeviceTierResourceTuning.PreloadConcurrency(DeviceTier.Low), "低端必须串行保内存峰值");
            Assert.AreEqual(3, DeviceTierResourceTuning.PreloadConcurrency(DeviceTier.Mid));
            Assert.AreEqual(6, DeviceTierResourceTuning.PreloadConcurrency(DeviceTier.High));
        }

        [Test]
        public void 预加载并发度_随档位单调不减()
        {
            // 档位越高并发不应更低，否则限流方向反了
            int low = DeviceTierResourceTuning.PreloadConcurrency(DeviceTier.Low);
            int mid = DeviceTierResourceTuning.PreloadConcurrency(DeviceTier.Mid);
            int high = DeviceTierResourceTuning.PreloadConcurrency(DeviceTier.High);
            Assert.LessOrEqual(low, mid, "中端并发不应低于低端");
            Assert.LessOrEqual(mid, high, "高端并发不应低于中端");
            Assert.GreaterOrEqual(low, 1, "并发度恒 ≥1，保证至少串行");
        }

        [Test]
        public void 特效并发上限_低端削峰_随档位单调不减()
        {
            int low = DeviceTierResourceTuning.MaxConcurrentEffects(DeviceTier.Low);
            int mid = DeviceTierResourceTuning.MaxConcurrentEffects(DeviceTier.Mid);
            int high = DeviceTierResourceTuning.MaxConcurrentEffects(DeviceTier.High);
            Assert.LessOrEqual(low, mid, "中端特效上限不应低于低端");
            Assert.LessOrEqual(mid, high, "高端特效上限不应低于中端");
            Assert.GreaterOrEqual(low, 1, "上限恒 ≥1");
        }
    }
}
