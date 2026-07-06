namespace Framework
{
    /// <summary>
    /// 轻提示展示通道，用于后续扩展 Toast、顶栏横幅和居中提示。
    /// </summary>
    public enum TipChannel
    {
        /// <summary>常规 Toast 飘字通道。</summary>
        Toast = 0,

        /// <summary>顶部横幅通道，适合应用内通知。</summary>
        Banner = 1,

        /// <summary>屏幕中心提示通道，适合局内强反馈。</summary>
        Center = 2,

        /// <summary>系统通道，适合网络和账号等关键提示。</summary>
        System = 3,
    }
}
