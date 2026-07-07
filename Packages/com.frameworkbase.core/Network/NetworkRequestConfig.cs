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

        /// <summary>
        /// 断线/重连期间是否入队等待补发（默认 false：未连接直接失败返回 null）。
        /// <para>
        /// 只对<b>幂等</b>请求开启（查询、拉取列表、上报确认等重发无副作用的消息）；
        /// 扣费、下单类非幂等请求禁止开启——断线窗口里服务端可能已处理过第一次，
        /// 重发的一致性只有业务层能判断。入队项在重连+重鉴权成功后按 FIFO 补发，
        /// 超过 <see cref="QueueTtlMs"/> 仍未能发出则按失败收尾（返回 null）。
        /// </para>
        /// </summary>
        public bool QueueWhileDisconnected { get; set; } = false;

        /// <summary>入队等待补发的最长时间（毫秒），到期未发出按失败收尾。默认 30000。</summary>
        public int QueueTtlMs { get; set; } = 30000;

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
