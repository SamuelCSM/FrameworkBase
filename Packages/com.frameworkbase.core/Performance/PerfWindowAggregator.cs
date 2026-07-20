using System;

namespace Framework.Performance
{
    /// <summary>
    /// 单个性能窗口的结算快照。由 <see cref="PerfWindowAggregator"/> 在窗口翻转时产出，
    /// 字段为该窗口内的聚合值，翻转后互不残留。
    /// </summary>
    public readonly struct PerfWindowReport
    {
        /// <summary>窗口序号（会话内从 1 起递增）。序号 1 通常含启动/加载噪声，漏斗分析时可单独看。</summary>
        public readonly int WindowIndex;

        /// <summary>窗口实际时长（秒）。最后一帧可能溢出配置窗口长度，以实际值为准算率。</summary>
        public readonly float DurationSeconds;

        /// <summary>窗口内合法帧数。</summary>
        public readonly int Frames;

        /// <summary>窗口平均 FPS。</summary>
        public readonly float AvgFps;

        /// <summary>窗口内最差单帧耗时（毫秒）。均值掩盖卡顿，最差帧才是玩家体感。</summary>
        public readonly float WorstFrameMs;

        /// <summary>卡顿帧数（帧耗时超过卡顿阈值，含严重卡顿帧）。</summary>
        public readonly int JankCount;

        /// <summary>严重卡顿帧数（帧耗时超过严重阈值，玩家体感为"冻住"）。</summary>
        public readonly int SevereJankCount;

        /// <summary>窗口内采样到的托管堆峰值（字节）。窗口内无内存采样时为 0。</summary>
        public readonly long ManagedPeakBytes;

        /// <summary>窗口内采样到的 Native 已分配内存峰值（字节）。窗口内无内存采样时为 0。</summary>
        public readonly long NativePeakBytes;

        public PerfWindowReport(
            int windowIndex, float durationSeconds, int frames, float avgFps, float worstFrameMs,
            int jankCount, int severeJankCount, long managedPeakBytes, long nativePeakBytes)
        {
            WindowIndex = windowIndex;
            DurationSeconds = durationSeconds;
            Frames = frames;
            AvgFps = avgFps;
            WorstFrameMs = worstFrameMs;
            JankCount = jankCount;
            SevereJankCount = severeJankCount;
            ManagedPeakBytes = managedPeakBytes;
            NativePeakBytes = nativePeakBytes;
        }
    }

    /// <summary>
    /// 线上性能窗口聚合器（纯逻辑，可单测）：按固定时间窗口聚合帧统计与内存峰值，
    /// 供 <see cref="PerfSampler"/> 低频上报。与 <see cref="FrameStatsAggregator"/>（0.5s 窗口、
    /// 服务 HUD 实时显示）的分工：本类窗口以分钟计，产出的是可入埋点管道做线上大盘的聚合值——
    /// 卡顿帧计数而非瞬时读数，事件量恒定（约 1 条/窗口/玩家），与帧率无关。
    /// <para>
    /// 卡顿阈值默认 100ms（跌破 10 FPS，任何目标帧率下玩家都有体感）；严重阈值默认 500ms
    /// （体感"冻住"，与 ANR 仅一步之遥）。两档均为绝对值而非相对目标帧率——大盘口径必须
    /// 跨设备可比，相对阈值会让高刷设备"更容易卡"，污染对比。
    /// </para>
    /// </summary>
    public sealed class PerfWindowAggregator
    {
        private readonly float _windowSeconds;
        private readonly float _jankThresholdMs;
        private readonly float _severeThresholdMs;

        private int _windowIndex;
        private float _elapsed;
        private int _frames;
        private float _worstMs;
        private int _jank;
        private int _severeJank;
        private long _managedPeak;
        private long _nativePeak;

        /// <summary>最近一次窗口翻转的结算快照（<see cref="Tick"/> 返回 true 后读取）。</summary>
        public PerfWindowReport LastReport { get; private set; }

        /// <param name="windowSeconds">聚合窗口长度（秒），下限 1。</param>
        /// <param name="jankThresholdMs">卡顿帧阈值（毫秒），下限 1。</param>
        /// <param name="severeJankThresholdMs">严重卡顿帧阈值（毫秒），低于卡顿阈值时被抬升至卡顿阈值。</param>
        public PerfWindowAggregator(
            float windowSeconds = 60f,
            float jankThresholdMs = 100f,
            float severeJankThresholdMs = 500f)
        {
            _windowSeconds = Math.Max(1f, windowSeconds);
            _jankThresholdMs = Math.Max(1f, jankThresholdMs);
            _severeThresholdMs = Math.Max(_jankThresholdMs, severeJankThresholdMs);
        }

        /// <summary>
        /// 每帧喂入 deltaTime（建议 unscaledDeltaTime）。返回 true 表示本帧结算了一个窗口，
        /// <see cref="LastReport"/> 已更新。非正 deltaTime 忽略不计。
        /// </summary>
        public bool Tick(float deltaTime)
        {
            if (deltaTime <= 0f)
                return false;

            _elapsed += deltaTime;
            _frames++;

            float frameMs = deltaTime * 1000f;
            if (frameMs > _worstMs)
                _worstMs = frameMs;
            if (frameMs >= _jankThresholdMs)
            {
                _jank++;
                if (frameMs >= _severeThresholdMs)
                    _severeJank++;
            }

            if (_elapsed < _windowSeconds)
                return false;

            _windowIndex++;
            LastReport = new PerfWindowReport(
                _windowIndex, _elapsed, _frames, _frames / _elapsed, _worstMs,
                _jank, _severeJank, _managedPeak, _nativePeak);

            ResetAccumulators();
            return true;
        }

        /// <summary>
        /// 喂入一次内存采样（字节），窗口内取峰值。采样频率由调用方控制（建议 5s 级——
        /// 逐帧读内存本身就是开销）。非正值忽略。
        /// </summary>
        public void SampleMemory(long managedBytes, long nativeBytes)
        {
            if (managedBytes > _managedPeak)
                _managedPeak = managedBytes;
            if (nativeBytes > _nativePeak)
                _nativePeak = nativeBytes;
        }

        /// <summary>
        /// 丢弃当前未满的窗口（不产出报告，窗口序号不消耗）。
        /// 用于切后台恢复等场景：挂起期间的时间账不应记进任何窗口。
        /// </summary>
        public void Reset()
        {
            ResetAccumulators();
        }

        private void ResetAccumulators()
        {
            _elapsed = 0f;
            _frames = 0;
            _worstMs = 0f;
            _jank = 0;
            _severeJank = 0;
            _managedPeak = 0;
            _nativePeak = 0;
        }
    }
}
