namespace Framework.Core
{
    /// <summary>LaunchFlow 结束结果，用于决定是否进入登录界面。</summary>
    public enum LaunchFlowOutcome
    {
        /// <summary>启动失败，停留在 Loading 重试循环。</summary>
        Failed,
        /// <summary>启动完成且已销毁 Loading，可进入登录。</summary>
        ReadyForLogin,
        /// <summary>整包强更闸门阻断，停留在 Loading 强更面板。</summary>
        BlockedOnForceUpdate
    }
}
