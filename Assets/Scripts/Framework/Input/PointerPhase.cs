namespace Framework.Input
{
    /// <summary>
    /// 统一指针相位，描述单帧内指针状态变化。
    /// </summary>
    public enum PointerPhase
    {
        /// <summary>本帧无有效指针事件。</summary>
        None = 0,

        /// <summary>指针刚按下。</summary>
        Began = 1,

        /// <summary>指针移动中。</summary>
        Moved = 2,

        /// <summary>指针抬起。</summary>
        Ended = 3,

        /// <summary>指针被取消（如来电、系统手势打断）。</summary>
        Canceled = 4,
    }
}
