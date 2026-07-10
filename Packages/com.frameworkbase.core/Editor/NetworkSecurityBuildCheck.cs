using System;
using System.Collections.Generic;
using Framework.Core;
using Framework.HotUpdate;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEngine;

namespace Framework.Editor
{
    /// <summary>
    /// Player 构建前的强联网传输安全门禁。
    /// <para>
    /// 生产环境启用真实网络登录时强制 TLS、有效 SNI 主机名和合理连接参数，并禁止通过证书 Pin 绕过系统证书链；
    /// 开发环境可显式允许自签名证书，但所有 Pin 仍必须是合法 SHA-256 且不得重复。
    /// </para>
    /// </summary>
    public sealed class NetworkSecurityBuildCheck : IPreprocessBuildWithReport
    {
        public int callbackOrder => -90;

        public void OnPreprocessBuild(BuildReport report)
        {
            AppConfigAsset config = Resources.Load<AppConfigAsset>("AppConfig");
            if (config == null || !config.UseNetworkLogin)
                return;

            if (string.IsNullOrWhiteSpace(config.GameServerHost))
                throw new BuildFailedException("[NetworkSecurity] GameServerHost 不能为空。");
            if (config.GameServerPort <= 0 || config.GameServerPort > 65535)
                throw new BuildFailedException("[NetworkSecurity] GameServerPort 必须位于 1～65535。");
            if (config.NetworkTimeoutSeconds <= 0 || config.NetworkTimeoutSeconds > 120)
                throw new BuildFailedException("[NetworkSecurity] NetworkTimeoutSeconds 必须位于 1～120 秒。");

            bool production = UpdateSecurity.IsProductionEnv(config.AppEnv);
            if (production && !config.UseTls)
                throw new BuildFailedException("[NetworkSecurity] 生产环境真实网络连接必须启用 TLS。");
            if (!config.UseTls)
                return;

            if (string.IsNullOrWhiteSpace(config.TlsServerName))
                throw new BuildFailedException("[NetworkSecurity] 启用 TLS 时必须配置用于 SNI 与主机名校验的 TlsServerName。");
            if (production && config.AllowPinnedCertificateWithoutSystemTrust)
            {
                throw new BuildFailedException(
                    "[NetworkSecurity] 生产环境禁止 AllowPinnedCertificateWithoutSystemTrust，证书必须同时通过系统信任校验。");
            }

            var pins = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            ValidatePin(config.TlsCertSha256, "TlsCertSha256", pins);
            if (config.TlsCertSha256Pins != null)
            {
                for (int i = 0; i < config.TlsCertSha256Pins.Length; i++)
                    ValidatePin(config.TlsCertSha256Pins[i], $"TlsCertSha256Pins[{i}]", pins);
            }
        }

        /// <summary>
        /// 校验并去重单个证书 SHA-256 Pin；空值表示不启用 Pin，只依赖系统证书链。
        /// </summary>
        private static void ValidatePin(string value, string fieldName, HashSet<string> pins)
        {
            if (string.IsNullOrWhiteSpace(value)) return;
            string normalized = value.Replace(":", string.Empty).Replace(" ", string.Empty);
            if (normalized.Length != 64)
                throw new BuildFailedException($"[NetworkSecurity] {fieldName} 不是 64 位 SHA-256 十六进制摘要。");
            foreach (char c in normalized)
            {
                bool hex = c >= '0' && c <= '9' || c >= 'a' && c <= 'f' || c >= 'A' && c <= 'F';
                if (!hex)
                    throw new BuildFailedException($"[NetworkSecurity] {fieldName} 包含非十六进制字符。");
            }
            if (!pins.Add(normalized))
                throw new BuildFailedException($"[NetworkSecurity] 证书 Pin 配置重复：{fieldName}");
        }
    }
}
