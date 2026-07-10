using System;

namespace Framework.Network
{
    /// <summary>
    /// TCP 客户端 TLS 配置。
    /// <para>
    /// 默认要求系统证书链、有效期和目标主机名全部校验通过；可选 SHA-256 Pin 用于进一步收窄信任范围。
    /// 证书轮换期间应同时配置当前证书和下一张证书的 Pin，完成服务端切换并确认客户端覆盖率后再移除旧 Pin。
    /// </para>
    /// <para>
    /// <see cref="AllowPinnedCertificateWithoutSystemTrust"/> 仅供受控开发环境使用自签名证书，正式环境必须关闭，
    /// 否则 Pin 泄露或配置错误会绕过 CA、有效期和主机名校验。
    /// </para>
    /// </summary>
    public sealed class TlsClientOptions
    {
        /// <summary>
        /// 是否启用 TLS。关闭时走明文 TCP，仅允许本机或隔离开发网络使用。
        /// </summary>
        public bool Enabled;

        /// <summary>
        /// TLS SNI 和证书主机名校验目标，必须与正式服务证书的 SAN/CN 匹配。
        /// </summary>
        public string TargetHost = string.Empty;

        /// <summary>
        /// 旧版单证书 SHA-256 Pin 兼容字段；新配置应使用 <see cref="CertSha256Pins"/> 支持无停机轮换。
        /// </summary>
        public string CertSha256 = string.Empty;

        /// <summary>
        /// 允许同时生效的证书 SHA-256 Pin 集合，十六进制编码可包含冒号或空格，比较时忽略大小写。
        /// </summary>
        public string[] CertSha256Pins = Array.Empty<string>();

        /// <summary>
        /// Pin 匹配时是否允许忽略系统证书链或主机名错误。仅限开发自签名证书，生产构建门禁必须禁止。
        /// </summary>
        public bool AllowPinnedCertificateWithoutSystemTrust;
    }
}
