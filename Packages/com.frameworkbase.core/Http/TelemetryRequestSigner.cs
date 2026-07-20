using System;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Framework.Http
{
    /// <summary>
    /// 遥测上报签名凭据：<see cref="UserId"/> 标识密钥归属（服务端据此查会话令牌），
    /// <see cref="Secret"/> 即当前会话令牌。任一为空视为无凭据（不签名）。
    /// </summary>
    public readonly struct TelemetrySigningCredentials
    {
        public readonly string UserId;
        public readonly string Secret;

        public TelemetrySigningCredentials(string userId, string secret)
        {
            UserId = userId;
            Secret = secret;
        }

        public bool IsValid => !string.IsNullOrEmpty(UserId) && !string.IsNullOrEmpty(Secret);
    }

    /// <summary>
    /// 无鉴权写入面（埋点 / 崩溃上报）的轻量请求签名器。
    /// <para>
    /// 契约：请求携带三个头——<c>X-Telemetry-Ts</c>（Unix 毫秒）、<c>X-Telemetry-Uid</c>（userId）、
    /// <c>X-Telemetry-Sign</c>（小写十六进制 HMAC-SHA256，key = UTF8(会话令牌)，
    /// message = UTF8("{ts}\n") + body 字节）。服务端按 Uid 查活跃会话令牌重算比对，
    /// 并用 Ts 做时间窗校验（防重放）。
    /// </para>
    /// <para>
    /// 密钥刻意选用会话令牌而非内嵌密钥：客户端资产禁止携带上传密钥（AppConfig 红线），
    /// 内嵌对称密钥可被提取属伪安全；未登录/无令牌时不签名照常发送，
    /// 服务端把未签名流量归入更严限流的通道，而不是拒收（登录前的启动埋点仍有价值）。
    /// </para>
    /// <para>
    /// 凭据由组合根注入（<see cref="SetCredentialsProvider"/>，典型实现读当前登录会话），
    /// 本类不依赖任何鉴权模块；签名失败绝不打断上报管道（折算为未签名发送）。
    /// </para>
    /// </summary>
    public static class TelemetryRequestSigner
    {
        public const string TimestampHeader = "X-Telemetry-Ts";
        public const string UserHeader = "X-Telemetry-Uid";
        public const string SignatureHeader = "X-Telemetry-Sign";

        private static Func<TelemetrySigningCredentials> _credentialsProvider;

        /// <summary>签名异常告警去重标记，避免每次上报刷屏。</summary>
        private static bool _signFailureWarned;

        /// <summary>
        /// 注入凭据提供器（组合根调用；传 null 关闭签名）。提供器按请求逐次求值，
        /// 会话切换后无需重新注入。
        /// </summary>
        public static void SetCredentialsProvider(Func<TelemetrySigningCredentials> provider)
        {
            _credentialsProvider = provider;
            _signFailureWarned = false;
        }

        /// <summary>
        /// 尝试为请求附加签名头。无提供器 / 无有效凭据 / 求值异常时返回 false 且不改动请求
        /// （调用方照常发送未签名请求）。
        /// </summary>
        public static bool TrySign(HttpRequest request)
        {
            if (request == null)
                return false;

            Func<TelemetrySigningCredentials> provider = _credentialsProvider;
            if (provider == null)
                return false;

            try
            {
                TelemetrySigningCredentials credentials = provider();
                if (!credentials.IsValid)
                    return false;

                long timestampMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                string signature = ComputeSignature(credentials.Secret, timestampMs, request.Body);
                request.WithHeader(TimestampHeader, timestampMs.ToString(CultureInfo.InvariantCulture));
                request.WithHeader(UserHeader, credentials.UserId);
                request.WithHeader(SignatureHeader, signature);
                return true;
            }
            catch (Exception ex)
            {
                if (!_signFailureWarned)
                {
                    _signFailureWarned = true;
                    GameLog.Warning($"[TelemetryRequestSigner] 签名失败，按未签名请求发送: {ex.Message}");
                }
                return false;
            }
        }

        /// <summary>
        /// 纯签名计算（跨端契约的唯一算法定义，服务端按同式重算）：
        /// 小写十六进制 HMAC-SHA256(key=UTF8(secret), message=UTF8("{timestampMs}\n") + body)。
        /// </summary>
        public static string ComputeSignature(string secret, long timestampMs, byte[] body)
        {
            if (string.IsNullOrEmpty(secret))
                throw new ArgumentException("签名密钥为空", nameof(secret));

            byte[] prefix = Encoding.UTF8.GetBytes(
                timestampMs.ToString(CultureInfo.InvariantCulture) + "\n");
            int bodyLength = body?.Length ?? 0;
            byte[] message = new byte[prefix.Length + bodyLength];
            Array.Copy(prefix, message, prefix.Length);
            if (bodyLength > 0)
                Array.Copy(body, 0, message, prefix.Length, bodyLength);

            using (var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secret)))
            {
                byte[] hash = hmac.ComputeHash(message);
                var sb = new StringBuilder(hash.Length * 2);
                for (int i = 0; i < hash.Length; i++)
                    sb.Append(hash[i].ToString("x2", CultureInfo.InvariantCulture));
                return sb.ToString();
            }
        }
    }
}
