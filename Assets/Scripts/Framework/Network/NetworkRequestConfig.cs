namespace Framework.Network
{
    /// <summary>
    /// 网络请求配置，控制单次 RequestAsync 的行为。
    /// </summary>
    public sealed class NetworkRequestConfig
    {
        /// <summary>请求超时时间（毫秒），超时后取消等待并返回 null。默认 15000。</summary>
        public int TimeoutMs { get; set; } = 15000;

        /// <summary>
        /// 显示等待 UI 的延迟（毫秒）。
        /// 请求发出后若超过此时间仍未收到响应，才显示转圈。
        /// 设为 0 表示立即显示，设为负数表示不显示。默认 1000。
        /// </summary>
        public int ShowLoadingDelayMs { get; set; } = 1000;

        /// <summary>是否在超时后弹出提示 Toast/弹窗。默认 true。</summary>
        public bool ShowTimeoutTip { get; set; } = true;

        /// <summary>超时提示文案，为 null 时使用默认文案。</summary>
        public string TimeoutMessage { get; set; }

        /// <summary>默认配置实例。</summary>
        public static NetworkRequestConfig Default { get; } = new NetworkRequestConfig();

        /// <summary>静默请求（不显示 Loading、不弹超时提示）。</summary>
        public static NetworkRequestConfig Silent { get; } = new NetworkRequestConfig
        {
            ShowLoadingDelayMs = -1,
            ShowTimeoutTip = false,
        };
    }
}
