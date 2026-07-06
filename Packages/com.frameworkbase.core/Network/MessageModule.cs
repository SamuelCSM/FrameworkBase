namespace Framework.Network
{
    /// <summary>
    /// 协议主 ID 常量。
    /// 主号 001 为框架保留（系统协议通道）；业务项目从 002 起占号，
    /// 在业务层自建常量表统一登记，避免不同模块私自定义重复主号。
    /// </summary>
    public static class MessageModule
    {
        /// <summary>框架系统协议（保留主号 001），当前用于心跳：001_001 请求 / 001_001 响应。</summary>
        public const byte System = 1;
    }
}
