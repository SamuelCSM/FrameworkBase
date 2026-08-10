using System;
using Framework.Core;
using Framework.HotUpdate;
using Framework.Serialization;

namespace Framework.RemoteConfig
{
    /// <summary>
    /// 远程配置签名信封的校验。
    /// <para>
    /// 远端配置能改灰度、开关和数值，是一条实打实的远程控制通道；TLS 只保证"这段字节来自那个域名"，
    /// 挡不住 CDN 被投毒、后台账号被盗，也挡不住本地缓存被改后在下次启动被原样信任。
    /// 因此配了公钥就要求载荷自带签名，签名覆盖<b>原始 payload 字节</b>——
    /// 不能对反序列化后重新生成的 JSON 验签，字段顺序与空白变化会破坏签名边界。
    /// </para>
    /// <para>
    /// 未配置公钥时不启用（返回原样载荷）：远程配置本就是可选能力，模板与开发期没有签名服务，
    /// 强制启用只会让这条路整个不可用。启用与否由 <c>AppConfig.RemoteConfigPublicKeys</c> 决定。
    /// </para>
    /// </summary>
    public static class RemoteConfigSignature
    {
        /// <summary>
        /// 签名信封。服务端把真实配置 JSON 原样放进 <see cref="Payload"/>，
        /// 并用私钥对该字符串的 UTF-8 字节做 RSA-SHA256 签名。
        /// </summary>
        [Serializable]
        public sealed class SignedEnvelope
        {
            /// <summary>选择信任根用的密钥 ID，须与 AppConfig 公钥环中的条目匹配。</summary>
            public string KeyId = string.Empty;

            /// <summary>Base64 的 RSA-SHA256 签名，覆盖 <see cref="Payload"/> 的 UTF-8 字节。</summary>
            public string Signature = string.Empty;

            /// <summary>真实配置 JSON 文本（原样字符串，不做二次转义之外的任何改动）。</summary>
            public string Payload = string.Empty;

            /// <summary>过期时刻（Unix 秒）；0 表示不设过期。用于限制一份被截获的旧配置能被重放多久。</summary>
            public long ExpiresAtUnixSeconds;
        }

        /// <summary>
        /// 校验并拆出配置载荷。
        /// </summary>
        /// <param name="raw">后端取回或磁盘缓存读出的原始文本。</param>
        /// <param name="config">应用配置，提供公钥环；为 null 视为未配置公钥。</param>
        /// <param name="nowUnixSeconds">当前时间（Unix 秒），用于过期判定。</param>
        /// <param name="payload">校验通过时返回真实配置 JSON。</param>
        /// <param name="rejectReason">校验失败时返回原因；未启用签名时为 null。</param>
        /// <returns>可以使用 <paramref name="payload"/> 时返回 true。</returns>
        public static bool TryUnwrap(
            string raw,
            AppConfigAsset config,
            long nowUnixSeconds,
            out string payload,
            out string rejectReason)
        {
            payload = raw;
            rejectReason = null;

            if (!IsEnabled(config))
                return true;

            if (string.IsNullOrWhiteSpace(raw))
            {
                rejectReason = "载荷为空";
                return false;
            }

            SignedEnvelope envelope;
            try
            {
                envelope = JsonSerializers.Shared.FromJson<SignedEnvelope>(raw);
            }
            catch (Exception ex)
            {
                rejectReason = $"签名信封无法解析：{ex.Message}";
                return false;
            }

            if (envelope == null || string.IsNullOrWhiteSpace(envelope.KeyId))
            {
                rejectReason = "签名信封缺少 KeyId";
                return false;
            }
            if (string.IsNullOrWhiteSpace(envelope.Signature) || string.IsNullOrEmpty(envelope.Payload))
            {
                rejectReason = "签名信封缺少 Signature 或 Payload";
                return false;
            }

            // 复用热更同一套 KeyId → 公钥解析：新旧公钥并存与分阶段轮换的语义完全一致。
            string publicKey = UpdateSecurity.ResolvePublicKey(envelope.KeyId, null, config.RemoteConfigPublicKeys);
            if (string.IsNullOrWhiteSpace(publicKey))
            {
                rejectReason = $"未找到 KeyId={envelope.KeyId} 对应的远程配置验签公钥";
                return false;
            }

            byte[] payloadBytes = System.Text.Encoding.UTF8.GetBytes(envelope.Payload);
            if (!UpdateSecurity.VerifyManifestSignature(payloadBytes, envelope.Signature, publicKey))
            {
                rejectReason = "RSA-SHA256 签名不匹配";
                return false;
            }

            // 过期判定放在验签之后：先确认这份声明本身可信，再看它声明的时效。
            if (envelope.ExpiresAtUnixSeconds > 0 && nowUnixSeconds > envelope.ExpiresAtUnixSeconds)
            {
                rejectReason = $"配置已过期（expiresAt={envelope.ExpiresAtUnixSeconds}）";
                return false;
            }

            payload = envelope.Payload;
            return true;
        }

        /// <summary>是否启用签名校验：配置了任一远程配置公钥即启用。</summary>
        /// <param name="config">应用配置。</param>
        /// <returns>启用返回 true。</returns>
        public static bool IsEnabled(AppConfigAsset config)
            => config?.RemoteConfigPublicKeys != null && config.RemoteConfigPublicKeys.Length > 0;
    }
}
