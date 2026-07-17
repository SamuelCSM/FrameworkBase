using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Profiling;

namespace Framework.Performance
{
    /// <summary>
    /// 线上性能采样器：全构建生效（含正式包），把运行时性能聚合成低频埋点事件上报。
    /// 与 <see cref="PerfHud"/>（Editor / Development Build 专用的屏幕叠加层）互补——
    /// HUD 给开发者看瞬时值，本组件给线上大盘喂聚合值。
    ///
    /// 每个窗口（默认 60s）产出一条 <c>perf_window</c> 事件：
    /// 平均 FPS / 最差帧 / 卡顿帧数（>100ms）/ 严重卡顿帧数（>500ms）/
    /// 托管与 Native 内存峰值 / GC 次数增量 / 当前场景。
    /// 事件量恒定约 1 条/分钟/玩家，走 AnalyticsManager 既有的批量与隐私合规管道。
    ///
    /// 由 GameEntry 自动挂载（Inspector 可关）；运行时可经 <see cref="Enabled"/> 开关，
    /// 供业务按 RemoteConfig 灰度控制采样人群。
    /// </summary>
    public class PerfSampler : MonoBehaviour
    {
        /// <summary>上报事件名。</summary>
        public const string EventName = "perf_window";

        /// <summary>运行时采样开关（业务可按 RemoteConfig 灰度控制；关闭期间窗口停止累计）。</summary>
        public static bool Enabled = true;

        private const float MemorySampleIntervalSeconds = 5f;
        private const long Mb = 1024 * 1024;

        private readonly PerfWindowAggregator _aggregator = new PerfWindowAggregator();
        private float _memoryTimer;
        private int _gcBaseline;
        private bool _skipNextFrame;

        private void Awake()
        {
            _gcBaseline = GC.CollectionCount(0);
        }

        private void OnApplicationPause(bool paused)
        {
            if (paused)
            {
                // 挂起期间的时间账不应记进窗口：丢弃当前半截窗口
                _aggregator.Reset();
                _memoryTimer = 0f;
            }
            else
            {
                // 恢复后的首帧 deltaTime 含整段挂起时长，会被误判为严重卡顿，跳过
                _skipNextFrame = true;
            }
        }

        private void Update()
        {
            if (!Enabled)
                return;

            if (_skipNextFrame)
            {
                _skipNextFrame = false;
                return;
            }

            float dt = Time.unscaledDeltaTime;

            _memoryTimer += dt;
            if (_memoryTimer >= MemorySampleIntervalSeconds)
            {
                _memoryTimer = 0f;
                _aggregator.SampleMemory(GC.GetTotalMemory(false), Profiler.GetTotalAllocatedMemoryLong());
            }

            if (_aggregator.Tick(dt))
                Report(_aggregator.LastReport);
        }

        private void Report(in PerfWindowReport report)
        {
            var analytics = Core.GameEntry.Analytics;
            if (analytics == null)
                return;

            // GC 增量按上报间隔差分：窗口被 Reset 丢弃时增量并入下一窗口，总量不失真
            int gcNow = GC.CollectionCount(0);
            int gcDelta = gcNow - _gcBaseline;
            _gcBaseline = gcNow;

            analytics.Track(EventName, new Dictionary<string, object>
            {
                { "window", report.WindowIndex },
                { "duration_s", Math.Round(report.DurationSeconds, 1) },
                { "frames", report.Frames },
                { "avg_fps", Math.Round(report.AvgFps, 1) },
                { "worst_ms", Math.Round(report.WorstFrameMs, 1) },
                { "jank", report.JankCount },
                { "severe_jank", report.SevereJankCount },
                { "managed_peak_mb", report.ManagedPeakBytes / Mb },
                { "native_peak_mb", report.NativePeakBytes / Mb },
                { "gc_count", gcDelta },
                { "scene", UnityEngine.SceneManagement.SceneManager.GetActiveScene().name },
            });
        }
    }
}
