namespace Framework.HotUpdate
{
    /// <summary>
    /// FrameworkBase 运行时兼容性标识。
    /// <para>
    /// 该版本用于热更新清单的最低框架版本准入，防止服务端向缺少必要运行时 API 的旧客户端投放新补丁。
    /// 它描述的是底层框架契约版本，不等同于游戏的 <c>Application.version</c>、资源版本或热更新代码版本。
    /// </para>
    /// </summary>
    public static class FrameworkRuntimeInfo
    {
        /// <summary>
        /// 当前 FrameworkBase Runtime 的语义化版本；发布框架包或产生不兼容运行时变更时必须同步维护。
        /// </summary>
        public const string Version = "0.17.0";

        /// <summary>
        /// 当前客户端能够解析并执行安全准入的最高热更新清单协议版本。
        /// </summary>
        public const int UpdateManifestVersion = 2;
    }
}
