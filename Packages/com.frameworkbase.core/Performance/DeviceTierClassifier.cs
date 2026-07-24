using System;

namespace Framework.Performance
{
    /// <summary>
    /// 设备档位。档位是给业务与画质映射用的粗粒度标签，不追求精确评分——
    /// 线上真正的校准依据是 perf_window 大盘按档位分组后的实测卡顿率。
    /// </summary>
    public enum DeviceTier
    {
        /// <summary>低端：优先保流畅与内存安全（最低画质、关可选特效）。</summary>
        Low = 0,

        /// <summary>中端：默认档。</summary>
        Mid = 1,

        /// <summary>高端：可开满画质与高刷。</summary>
        High = 2,
    }

    /// <summary>
    /// 分级输入的设备画像（纯数据，可单测）。字段取不到时置 0（视为未知）。
    /// </summary>
    public readonly struct DeviceProfile
    {
        /// <summary>系统内存（MB）。</summary>
        public readonly int SystemMemoryMb;

        /// <summary>显存（MB）。</summary>
        public readonly int GraphicsMemoryMb;

        /// <summary>逻辑核心数。</summary>
        public readonly int ProcessorCount;

        public DeviceProfile(int systemMemoryMb, int graphicsMemoryMb, int processorCount)
        {
            SystemMemoryMb = systemMemoryMb;
            GraphicsMemoryMb = graphicsMemoryMb;
            ProcessorCount = processorCount;
        }
    }

    /// <summary>
    /// 分级阈值。默认值面向 2026 年移动端存量盘：≤3GB 内存或 ≤1GB 显存判低端；
    /// ≥6GB 内存且显存/核数不拖后腿判高端。项目可按目标市场调整后传入。
    /// </summary>
    public sealed class DeviceTierThresholds
    {
        /// <summary>系统内存不高于该值（MB）判低端。</summary>
        public int LowMaxSystemMemoryMb = 3072;

        /// <summary>显存不高于该值（MB）判低端。</summary>
        public int LowMaxGraphicsMemoryMb = 1024;

        /// <summary>高端要求的最低系统内存（MB）。</summary>
        public int HighMinSystemMemoryMb = 6144;

        /// <summary>高端要求的最低显存（MB），未知维度不参与否决。</summary>
        public int HighMinGraphicsMemoryMb = 2048;

        /// <summary>高端要求的最低逻辑核心数，未知维度不参与否决。</summary>
        public int HighMinProcessorCount = 8;
    }

    /// <summary>
    /// 设备分级器（纯逻辑，可单测）：按内存/显存/核数把设备粗分三档。
    /// 规则刻意保守：任一已知维度踩低端线即判低端（宁可画质保守也别 OOM/卡成盘）；
    /// 判高端要求内存已知且所有已知维度都达标；系统内存未知时封顶中端。
    /// </summary>
    public static class DeviceTierClassifier
    {
        private static readonly DeviceTierThresholds Default = new DeviceTierThresholds();

        public static DeviceTier Classify(in DeviceProfile profile, DeviceTierThresholds thresholds = null)
        {
            var t = thresholds ?? Default;

            bool sysKnown = profile.SystemMemoryMb > 0;
            bool gfxKnown = profile.GraphicsMemoryMb > 0;
            bool cpuKnown = profile.ProcessorCount > 0;

            // 任一已知维度踩低端线 → 低端
            if (sysKnown && profile.SystemMemoryMb <= t.LowMaxSystemMemoryMb)
                return DeviceTier.Low;
            if (gfxKnown && profile.GraphicsMemoryMb <= t.LowMaxGraphicsMemoryMb)
                return DeviceTier.Low;

            // 高端：内存必须已知且达标，其余已知维度不得拖后腿
            bool high = sysKnown
                && profile.SystemMemoryMb >= t.HighMinSystemMemoryMb
                && (!gfxKnown || profile.GraphicsMemoryMb >= t.HighMinGraphicsMemoryMb)
                && (!cpuKnown || profile.ProcessorCount >= t.HighMinProcessorCount);

            return high ? DeviceTier.High : DeviceTier.Mid;
        }
    }

    /// <summary>
    /// 设备分级 → 资源加载调优参数映射（纯逻辑，可单测）。
    /// 低端窄路优先保内存安全、削 IO/解压峰值；高端多路提吞吐。
    /// 这里只给保守默认，项目可经 <see cref="ResourceManager.PreloadConcurrencyOverride"/> 覆盖。
    /// 真正的校准依据同样是 perf_window 大盘按档位分组后的实测表现，而非拍脑袋。
    /// </summary>
    public static class DeviceTierResourceTuning
    {
        /// <summary>
        /// 批量预加载的建议并发度（同时在途的 Addressables 加载操作数）。
        /// Low=1（串行，最小内存峰值、最稳）、Mid=3、High=6。返回值恒 ≥1。
        /// </summary>
        /// <param name="tier">当前设备档位。</param>
        /// <returns>并发度（≥1）。</returns>
        public static int PreloadConcurrency(DeviceTier tier)
        {
            switch (tier)
            {
                case DeviceTier.Low:
                    return 1;
                case DeviceTier.High:
                    return 6;
                default:
                    return 3;
            }
        }

        /// <summary>
        /// 同屏特效并发上限（池化特效同时存活数）。Low=16 削峰保低端内存/填充率，Mid=32，High=64。返回 ≥1。
        /// 超上限的新特效被丢弃——表现降级（少几个特效）远优于低端机卡顿/OOM。
        /// </summary>
        /// <param name="tier">当前设备档位。</param>
        /// <returns>并发上限（≥1）。</returns>
        public static int MaxConcurrentEffects(DeviceTier tier)
        {
            switch (tier)
            {
                case DeviceTier.Low:
                    return 16;
                case DeviceTier.High:
                    return 64;
                default:
                    return 32;
            }
        }
    }
}
