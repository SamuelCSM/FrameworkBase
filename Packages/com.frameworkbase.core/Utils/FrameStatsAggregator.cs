using UnityEngine;

namespace Framework
{
    /// <summary>
    /// 帧统计聚合器（纯逻辑，可单测）：按固定时间窗口聚合 FPS 与最差帧耗时。
    /// 均值 FPS 会掩盖卡顿——一帧 100ms 的尖刺在 0.5s 均值里只掉几帧，
    /// 玩家却明确感到顿挫，所以窗口内的最差帧耗时必须单独暴露。
    /// </summary>
    public sealed class FrameStatsAggregator
    {
        private readonly float _windowSeconds;
        private float _elapsed;
        private int _frames;
        private float _worstMs;

        /// <summary>最近一个完整窗口的平均 FPS。</summary>
        public float Fps { get; private set; }

        /// <summary>最近一个完整窗口内的最差单帧耗时（毫秒）。</summary>
        public float WorstFrameMs { get; private set; }

        /// <param name="windowSeconds">聚合窗口长度（秒），下限 0.1。</param>
        public FrameStatsAggregator(float windowSeconds = 0.5f)
        {
            _windowSeconds = Mathf.Max(0.1f, windowSeconds);
        }

        /// <summary>
        /// 每帧喂入 deltaTime（建议 unscaledDeltaTime，暂停/慢动作时仍反映真实帧率）。
        /// 返回 true 表示本帧结算了一个窗口，Fps / WorstFrameMs 已更新为该窗口的值。
        /// 非正 deltaTime（暂停恢复的脏帧等）忽略不计。
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

            if (_elapsed < _windowSeconds)
                return false;

            Fps = _frames / _elapsed;
            WorstFrameMs = _worstMs;

            // 窗口翻转：尖刺不残留到下一窗口
            _elapsed = 0f;
            _frames = 0;
            _worstMs = 0f;
            return true;
        }
    }
}
