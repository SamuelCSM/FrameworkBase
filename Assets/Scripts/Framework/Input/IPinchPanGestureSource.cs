namespace Framework.Input
{
    /// <summary>
    /// 双指缩放/平移手势采样源。
    /// </summary>
    public interface IPinchPanGestureSource
    {
        /// <summary>每帧采样并刷新内部状态。</summary>
        void Collect();

        /// <summary>本帧手势采样结果。</summary>
        PinchPanFrame CurrentFrame { get; }
    }
}
