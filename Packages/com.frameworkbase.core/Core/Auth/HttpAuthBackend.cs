using System;
using System.Text;
using System.Threading;
using Cysharp.Threading.Tasks;
using Framework.Http;
using Framework.Serialization;
using UnityEngine;

namespace Framework.Core.Auth
{
    /// <summary>
    /// 框架参考实现：基于 HTTP 的真实登录后端。
    /// <para>
    /// 定位：让主干在<b>无业务代码</b>时也具备一条可端到端验证的真实登录链路（区别于 <see cref="MockAuthBackend"/>）。
    /// 使用<b>中性 JSON 契约</b>（非任何具体游戏协议），走 <see cref="Framework.Http.IHttpClient"/> +
    /// <see cref="Framework.Serialization.IJsonSerializer"/> 抽象，因此不引入传输/序列化厂商依赖。
    /// </para>
    /// <para>
    /// 请求体：<c>{ "mode":"guest|account", "account", "password", "sessionToken", "deviceId" }</c>；
    /// 响应体：<c>{ "success":bool, "userId", "sessionToken", "errorCode", "errorMessage" }</c>。
    /// 令牌重绑（断线重连 / 冷启动恢复）时 <c>password</c> 为空、仅携带 <c>sessionToken</c>。
    /// </para>
    /// <para>
    /// 契约与真实服务端不一致时，业务应实现自有 <see cref="IAuthBackend"/> 并经
    /// <see cref="AuthManager.SetBackend"/> 注入替换；本实现是参考默认，不是强制协议。
    /// </para>
    /// </summary>
    public sealed class HttpAuthBackend : IAuthBackend
    {
        [Serializable]
        private sealed class LoginRequestDto
        {
            public string mode;
            public string account;
            public string password;
            public string sessionToken;
            public string deviceId;
        }

        [Serializable]
        private sealed class LoginResponseDto
        {
            public bool success;
            public string userId;
            public string sessionToken;
            public string errorCode;
            public string errorMessage;
        }

        private readonly string _url;
        private readonly IHttpClient _httpClient;
        private readonly IJsonSerializer _json;

        /// <summary>
        /// 构造 HTTP 登录后端。
        /// </summary>
        /// <param name="url">登录服务地址（POST）。prod 环境应为 HTTPS。</param>
        /// <param name="httpClient">HTTP 客户端；为空时使用 <see cref="HttpClients.Shared"/>。</param>
        /// <param name="json">JSON 序列化器；为空时使用 <see cref="JsonSerializers.Shared"/>。</param>
        public HttpAuthBackend(string url, IHttpClient httpClient = null, IJsonSerializer json = null)
        {
            _url = url ?? string.Empty;
            _httpClient = httpClient ?? HttpClients.Shared;
            _json = json ?? JsonSerializers.Shared;
        }

        public async UniTask<LoginResult> LoginAsync(LoginRequestContext context, CancellationToken cancellationToken)
        {
            if (string.IsNullOrEmpty(_url))
                return LoginResult.Fail(TelemetryErrorCodes.Auth.Unknown, "auth server url not configured");

            var dto = new LoginRequestDto
            {
                mode = context.Mode == LoginMode.Guest ? "guest" : "account",
                account = context.Account ?? string.Empty,
                password = context.Password ?? string.Empty,
                sessionToken = context.SessionToken ?? string.Empty,
                deviceId = SystemInfo.deviceUniqueIdentifier,
            };

            // TimeoutMs 上取整为秒交给传输层；<=0 时按 10 秒兜底。
            int timeoutSeconds = context.TimeoutMs > 0 ? (context.TimeoutMs + 999) / 1000 : 10;
            string body = _json.ToJson(dto);

            HttpResponse response;
            try
            {
                HttpRequest request = HttpRequest
                    .Post(_url, Encoding.UTF8.GetBytes(body), "application/json")
                    .WithTimeout(timeoutSeconds);
                response = await _httpClient.SendAsync(request).AttachExternalCancellation(cancellationToken);
            }
            catch (OperationCanceledException)
            {
                // 取消/超时交由上层（AuthManager）按 isTimeout 归类，保持既有语义。
                throw;
            }
            catch (Exception ex)
            {
                return LoginResult.Fail(TelemetryErrorCodes.Auth.NetworkOffline, ex.Message);
            }

            if (!response.Succeeded)
            {
                // 401/403 视为凭据/令牌失效（引导重新登录），其余按网络离线（可重试）。
                string transportCode = response.StatusCode == 401 || response.StatusCode == 403
                    ? TelemetryErrorCodes.Auth.InvalidCredential
                    : TelemetryErrorCodes.Auth.NetworkOffline;
                return LoginResult.Fail(transportCode, $"http {response.StatusCode}: {response.Error}");
            }

            if (!_json.TryFromJson(response.Text, out LoginResponseDto parsed) || parsed == null)
                return LoginResult.Fail(TelemetryErrorCodes.Auth.Unknown, "invalid login response");

            if (!parsed.success)
            {
                string errorCode = string.IsNullOrEmpty(parsed.errorCode)
                    ? TelemetryErrorCodes.Auth.InvalidCredential
                    : parsed.errorCode;
                return LoginResult.Fail(errorCode, parsed.errorMessage ?? string.Empty);
            }

            if (string.IsNullOrEmpty(parsed.userId))
                return LoginResult.Fail(TelemetryErrorCodes.Auth.Unknown, "login response missing userId");

            return LoginResult.Ok(parsed.userId, parsed.sessionToken ?? string.Empty);
        }
    }
}
