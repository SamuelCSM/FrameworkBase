namespace Framework
{
    /// <summary>
    /// 游戏内通用消息 ID 表。
    /// 所有业务广播消息必须在这里统一占号，避免不同模块私自定义重复 ID。
    /// </summary>
    public enum GameMessage
    {
        // ==================== Framework 系统消息：9000-9999 ====================

        /// <summary>系统低内存警告（Application.lowMemory），无参数；订阅方应清空可重建缓存。</summary>
        LowMemoryWarning = 9004,

        /// <summary>语言切换，参数：string language。</summary>
        LanguageChanged = 9005,

        /// <summary>服务端要求强制登出（会话失效/顶号/封禁），参数：int errorCode；业务订阅后跳登录界面。</summary>
        ServerForceLogout = 9006,

        /// <summary>停服维护通知，参数：int errorCode；业务订阅后进维护页/公告。</summary>
        ServerMaintenance = 9007,

        // ==================== 玩家与登录消息：10000-10999 ====================

        /// <summary>玩家登录成功。</summary>
        PlayerLoginSuccess = 10001,

        /// <summary>玩家登录失败，参数：string errorCode。</summary>
        PlayerLoginFailed = 10002,

        /// <summary>玩家登出。</summary>
        PlayerLogout = 10003,

        // ==================== 业务消息：20000 起 ====================
        // 业务项目在此从 20000 开始追加自己的广播消息占号（建议按模块划分号段），
        // 框架层不使用该区间，占号统一登记在本枚举避免模块间冲突。
    }
}
