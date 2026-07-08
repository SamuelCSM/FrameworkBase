namespace Framework.Telemetry.Bugly
{
    /// <summary>
    /// Bugly 初始化参数。骨架默认从代码构造；真实项目建议改为从
    /// <c>Resources</c> / <c>AppConfig</c> 读取，避免把 AppId 写死在代码里。
    /// </summary>
    public sealed class BuglyOptions
    {
        /// <summary>Bugly 应用 AppId（Bugly 后台创建产品后获得）。留空时后端不装载原生捕获、仅告警。</summary>
        public string AppId = string.Empty;

        /// <summary>是否调试模式（true 时 Bugly 输出详细日志，正式包应为 false）。</summary>
        public bool IsDebug = false;

        /// <summary>渠道标识（随崩溃报告上报，便于分渠道排查）。</summary>
        public string Channel = string.Empty;

        /// <summary>区域：<see cref="BuglyRegion.China"/> 或 <see cref="BuglyRegion.Global"/>（决定上报域名）。</summary>
        public BuglyRegion Region = BuglyRegion.China;

        /// <summary>AppId 是否已配置。</summary>
        public bool IsConfigured => !string.IsNullOrEmpty(AppId);
    }

    /// <summary>Bugly 上报区域。国内版与国际版（Bugly Global）后台与域名不同。</summary>
    public enum BuglyRegion
    {
        /// <summary>国内版（bugly.qq.com）。</summary>
        China,

        /// <summary>国际版（bugly.io）。</summary>
        Global,
    }
}
