namespace Framework
{
    /// <summary>
    /// 框架内置消息 ID 表。
    /// 业务热更程序集请自建枚举或常量，并通过 EventManager 的 int messageId 重载订阅/发布。
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

        /// <summary>隐私同意状态变化，参数：int acceptedPolicyVersion（0 = 撤回/未同意）。</summary>
        PrivacyConsentChanged = 9008,

        // ==================== 玩家与登录消息：10000-10999 ====================

        /// <summary>玩家登录成功。</summary>
        PlayerLoginSuccess = 10001,

        /// <summary>玩家登录失败，参数：string errorCode。</summary>
        PlayerLoginFailed = 10002,

        /// <summary>玩家登出。</summary>
        PlayerLogout = 10003,
    }
}
