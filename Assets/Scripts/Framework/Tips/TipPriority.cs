namespace Framework
{
    /// <summary>
    /// 轻提示调度优先级，用于队列满载和插队策略。
    /// </summary>
    public enum TipPriority
    {
        /// <summary>低优先级提示，队列满时优先丢弃。</summary>
        Low = 0,

        /// <summary>普通业务提示。</summary>
        Normal = 1,

        /// <summary>重要错误或关键反馈提示。</summary>
        High = 2,

        /// <summary>系统级提示，保留给启动、网络、账号等关键链路。</summary>
        System = 3,
    }
}
