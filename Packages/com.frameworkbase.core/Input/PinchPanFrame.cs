using UnityEngine;

namespace Framework.Input
{
    /// <summary>
    /// 双指缩放/平移或 PC 等效手势的单帧采样结果。
    /// </summary>
    public readonly struct PinchPanFrame
    {
        /// <summary>无手势活动。</summary>
        public static readonly PinchPanFrame None = new PinchPanFrame(false, 1f, Vector2.zero);

        /// <summary>
        /// 创建双指/滚轮手势帧。
        /// </summary>
        /// <param name="isActive">本帧是否存在有效手势。</param>
        /// <param name="zoomFactor">本帧缩放倍率，1 表示不变，大于 1 表示放大画面（拉近）。</param>
        /// <param name="panDelta">本帧屏幕空间平移增量。</param>
        public PinchPanFrame(bool isActive, float zoomFactor, Vector2 panDelta)
        {
            IsActive = isActive;
            ZoomFactor = zoomFactor;
            PanDelta = panDelta;
        }

        /// <summary>本帧是否存在有效手势。</summary>
        public bool IsActive { get; }

        /// <summary>本帧缩放倍率，1 表示不变，大于 1 表示放大画面（拉近）。</summary>
        public float ZoomFactor { get; }

        /// <summary>本帧屏幕空间平移增量。</summary>
        public Vector2 PanDelta { get; }
    }
}
