using System;

namespace Framework.Core.Auth
{
    /// <summary>登录模式。</summary>
    public enum LoginMode
    {
        Guest,
        Account
    }

    /// <summary>登录状态机状态。</summary>
    public enum LoginFlowState
    {
        Idle,
        Preparing,
        Connecting,
        Authenticating,
        Success,
        Failed,
        Cancelled
    }

    /// <summary>登录请求上下文。</summary>
    public struct LoginRequestContext
    {
        public LoginMode Mode;
        public string Account;
        public string Password;

        /// <summary>
        /// 会话令牌：非空时后端走令牌重绑路径（断线重连静默恢复身份），不携带密码。
        /// 由 <see cref="AuthManager.ReauthenticateAsync"/> 从 <see cref="AuthSession"/> 回填。
        /// </summary>
        public string SessionToken;

        public int TimeoutMs;
    }

    /// <summary>登录执行结果。</summary>
    public struct LoginResult
    {
        public bool Success;
        public string UserId;
        /// <summary>会话 Token（GS/未来 LS 下发；Mock 为空）。</summary>
        public string SessionToken;
        public string ErrorCode;
        public string ErrorMessage;

        public static LoginResult Ok(string userId, string sessionToken = "")
        {
            return new LoginResult
            {
                Success = true,
                UserId = userId ?? string.Empty,
                SessionToken = sessionToken ?? string.Empty,
                ErrorCode = string.Empty,
                ErrorMessage = string.Empty
            };
        }

        public static LoginResult Fail(string errorCode, string errorMessage)
        {
            return new LoginResult
            {
                Success = false,
                UserId = string.Empty,
                ErrorCode = errorCode ?? string.Empty,
                ErrorMessage = errorMessage ?? string.Empty
            };
        }
    }

    /// <summary>错误弹窗决策。</summary>
    public struct AuthPopupDecision
    {
        public string ErrorCode;
        public string Message;
        public bool ShowRetry;
        public bool ShowExit;
    }

    /// <summary>登录状态变化快照。</summary>
    public struct LoginStateSnapshot
    {
        public LoginFlowState State;
        public string Reason;
        public string ErrorCode;
        public DateTime AtUtc;
    }
}
