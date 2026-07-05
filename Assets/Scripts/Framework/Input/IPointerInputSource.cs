namespace Framework.Input
{
    /// <summary>
    /// 指针输入采样源，负责把设备原始输入转换为统一指针快照。
    /// </summary>
    public interface IPointerInputSource
    {
        /// <summary>每帧采样并刷新内部状态。</summary>
        void Collect();

        /// <summary>当前主指针快照，用于单指点击/拖拽。</summary>
        PointerSnapshot PrimaryPointer { get; }

        /// <summary>当前活跃触控数量；鼠标模式下为 0 或 1。</summary>
        int ActivePointerCount { get; }

        /// <summary>当前是否处于多指手势态（双指及以上）。</summary>
        bool IsMultiPointerActive { get; }
    }
}
