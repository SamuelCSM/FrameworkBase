namespace Framework.Core.Auth
{
    /// <summary>
    /// 当前登录会话（内存态）。后续 GS 业务协议可携带 SessionToken。
    /// </summary>
    public static class AuthSession
    {
        public static string UserId { get; private set; } = string.Empty;
        public static string SessionToken { get; private set; } = string.Empty;

        /// <summary>会话令牌过期时刻（Unix 毫秒；0 = 服务端未提供）。语义见 <see cref="LoginResult.SessionTokenExpiresAtMs"/>。</summary>
        public static long SessionTokenExpiresAtMs { get; private set; }

        public static bool IsLoggedIn => !string.IsNullOrEmpty(UserId);

        public static void Apply(LoginResult result)
        {
            UserId = result.Success ? result.UserId ?? string.Empty : string.Empty;
            SessionToken = result.Success ? result.SessionToken ?? string.Empty : string.Empty;
            SessionTokenExpiresAtMs = result.Success ? result.SessionTokenExpiresAtMs : 0;
        }

        public static void Clear()
        {
            UserId = string.Empty;
            SessionToken = string.Empty;
            SessionTokenExpiresAtMs = 0;
        }
    }
}
