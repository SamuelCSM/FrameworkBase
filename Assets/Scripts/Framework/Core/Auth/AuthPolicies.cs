using System;
using System.Threading;
using Cysharp.Threading.Tasks;

namespace Framework.Core.Auth
{
    /// <summary>统一错误码映射：异常/运行态 -> Auth 聚合错误码。</summary>
    public static class AuthErrorMapper
    {
        public static string MapException(Exception ex, bool isTimeout)
        {
            if (ex is OperationCanceledException)
                return TelemetryErrorCodes.Auth.LoginCancelled;
            if (isTimeout)
                return TelemetryErrorCodes.Auth.LoginTimeout;

            string message = ex?.Message ?? string.Empty;
            if (message.IndexOf("network", StringComparison.OrdinalIgnoreCase) >= 0 ||
                message.IndexOf("socket", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return TelemetryErrorCodes.Auth.NetworkOffline;
            }

            return TelemetryErrorCodes.Auth.Unknown;
        }
    }

    /// <summary>按错误码给出统一弹窗策略。</summary>
    public static class AuthPopupPolicy
    {
        public static AuthPopupDecision Resolve(string errorCode, string fallbackMessage)
        {
            if (string.Equals(errorCode, TelemetryErrorCodes.Auth.LoginCancelled, StringComparison.Ordinal))
            {
                return new AuthPopupDecision
                {
                    ErrorCode = errorCode,
                    Message = "登录已取消",
                    ShowRetry = false,
                    ShowExit = false
                };
            }

            if (string.Equals(errorCode, TelemetryErrorCodes.Auth.LoginTimeout, StringComparison.Ordinal) ||
                string.Equals(errorCode, TelemetryErrorCodes.Auth.NetworkOffline, StringComparison.Ordinal))
            {
                return new AuthPopupDecision
                {
                    ErrorCode = errorCode,
                    Message = "网络异常，是否重试登录？",
                    ShowRetry = true,
                    ShowExit = true
                };
            }

            if (string.Equals(errorCode, TelemetryErrorCodes.Auth.InvalidCredential, StringComparison.Ordinal))
            {
                return new AuthPopupDecision
                {
                    ErrorCode = errorCode,
                    Message = "账号或密码错误，请检查后重试",
                    ShowRetry = true,
                    ShowExit = false
                };
            }

            return new AuthPopupDecision
            {
                ErrorCode = string.IsNullOrEmpty(errorCode) ? TelemetryErrorCodes.Auth.Unknown : errorCode,
                Message = string.IsNullOrEmpty(fallbackMessage) ? "登录失败，请稍后重试" : fallbackMessage,
                ShowRetry = true,
                ShowExit = true
            };
        }
    }

    /// <summary>
    /// 默认弹窗呈现器（框架兜底版）。
    /// 无业务 UI 接入时先记录策略日志，避免状态机中断。
    /// </summary>
    public sealed class LogOnlyAuthPopupPresenter : IAuthPopupPresenter
    {
        public UniTask PresentAsync(AuthPopupDecision decision, Func<UniTask> retryHandler, Action exitHandler)
        {
            Logger.Warning($"[AuthPopup] code={decision.ErrorCode}, msg={decision.Message}, retry={decision.ShowRetry}, exit={decision.ShowExit}");
            return UniTask.CompletedTask;
        }
    }

    /// <summary>无服务端阶段的本地 Mock 登录后端。</summary>
    public sealed class MockAuthBackend : IAuthBackend
    {
        public int SimulatedDelayMs = 400;
        public bool SimulateNetworkOffline;
        public bool SimulateTimeout;

        public async UniTask<LoginResult> LoginAsync(LoginRequestContext context, CancellationToken cancellationToken)
        {
            await UniTask.Delay(SimulatedDelayMs, cancellationToken: cancellationToken);

            if (SimulateTimeout)
            {
                await UniTask.Delay(TimeSpan.FromMilliseconds(Math.Max(1, context.TimeoutMs + 100)), cancellationToken: cancellationToken);
            }

            if (SimulateNetworkOffline)
            {
                return LoginResult.Fail(TelemetryErrorCodes.Auth.NetworkOffline, "mock network offline");
            }

            if (context.Mode == LoginMode.Guest)
            {
                return LoginResult.Ok("guest_local");
            }

            if (string.IsNullOrEmpty(context.Account) || string.IsNullOrEmpty(context.Password))
            {
                return LoginResult.Fail(TelemetryErrorCodes.Auth.InvalidCredential, "account/password required");
            }

            return LoginResult.Ok(context.Account);
        }
    }
}
