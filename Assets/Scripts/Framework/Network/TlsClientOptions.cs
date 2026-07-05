namespace Framework.Network
{
    /// <summary>
    /// 客户端 TLS 配置：启用后 <see cref="TcpClient"/> 在 TCP 连接建立后先完成 TLS 握手，
    /// 服务器证书按「标准链校验通过 或 SHA-256 指纹匹配」二者其一放行（自签名证书走指纹固定，
    /// 无需 CA/域名，指纹随包内置、比链校验更抗中间人）。
    /// 取值来源：组合根从 <c>AppConfig</c>（UseTls / TlsServerName / TlsCertSha256）装配。
    /// </summary>
    public sealed class TlsClientOptions
    {
        /// <summary>是否启用 TLS。false 时其余字段无效，走明文 TCP（仅限本机/内网开发）。</summary>
        public bool Enabled;

        /// <summary>
        /// 握手目标名（SNI），须与服务端证书 CN 一致；自签名默认 <c>clientbase-gs</c>。
        /// 名称不匹配不致命——会落入指纹校验兜底。
        /// </summary>
        public string TargetHost = "clientbase-gs";

        /// <summary>
        /// 服务端证书 SHA-256 指纹（十六进制，允许冒号/空格分隔，大小写不敏感）。
        /// 服务端以 <c>Tools/gen_gs_tls_cert.ps1</c> 生成证书时打印；为空则只接受标准链校验通过的证书。
        /// </summary>
        public string CertSha256 = string.Empty;
    }
}
