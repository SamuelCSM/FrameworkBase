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
    /// 同时覆盖登录 HTTP 链路与游戏长连接：生产环境禁止 Mock 登录、禁止明文登录 URL，
    /// 并强制游戏连接启用 TLS、有效 SNI 主机名和合理连接参数；生产环境还禁止通过证书 Pin
    /// 绕过系统证书链。开发环境可以使用本机 HTTP 和自签名证书，但配置错误仍失败关闭。
    /// </para>
    /// </summary>
    public sealed class NetworkSecurityBuildCheck : IPreprocessBuildWithReport
    {
        public int callbackOrder => -90;

        public void OnPreprocessBuild(BuildReport report)
        {
            AppConfigAsset config = Resources.Load<AppConfigAsset>("AppConfig");
            ValidateConfig(config, config?.AppEnv);
        }

        /// <summary>
        /// 校验登录服务和游戏长连接配置。提取为纯配置门禁，便于 EditMode 自动化覆盖，
        /// 避免安全规则只存在于构建回调中而无法稳定回归。
        /// </summary>
        public static void ValidateConfig(AppConfigAsset config) => ValidateConfig(config, config?.AppEnv);

        /// <summary>
        /// 按显式目标环境校验网络配置。正式发布入口必须传 ReleaseProfile 环境，防止以
        /// <c>releaseEnv=prod</c> 发布时 AppConfig 仍伪装成 dev，从而绕过 HTTPS 和 Mock 登录门禁。
        /// </summary>
        public static void ValidateConfig(AppConfigAsset config, string expectedEnvironment)
        {
            if (config == null)
                throw new BuildFailedException("[NetworkSecurity] 缺少 Resources/AppConfig.asset，无法验证生产网络安全配置。");

            string expected = string.IsNullOrWhiteSpace(expectedEnvironment)
                ? config.AppEnv?.Trim()
                : expectedEnvironment.Trim();
            if (!string.Equals(config.AppEnv?.Trim(), expected, StringComparison.OrdinalIgnoreCase))
            {
                throw new BuildFailedException(
                    $"[NetworkSecurity] AppConfig.AppEnv={config.AppEnv} 与目标发布环境 {expected} 不一致，禁止使用低环境配置发布高环境产物。");
            }

            bool production = UpdateSecurity.IsProductionEnv(expected);

            if (float.IsNaN(config.NetworkBackgroundGraceSeconds) ||
                float.IsInfinity(config.NetworkBackgroundGraceSeconds) ||
                config.NetworkBackgroundGraceSeconds < 0f || config.NetworkBackgroundGraceSeconds > 300f)
                throw new BuildFailedException("[NetworkSecurity] NetworkBackgroundGraceSeconds 必须位于 0～300 秒。");
            if (float.IsNaN(config.NetworkForegroundProbeTimeoutSeconds) ||
                float.IsInfinity(config.NetworkForegroundProbeTimeoutSeconds) ||
                config.NetworkForegroundProbeTimeoutSeconds <= 0f || config.NetworkForegroundProbeTimeoutSeconds > 60f)
                throw new BuildFailedException("[NetworkSecurity] NetworkForegroundProbeTimeoutSeconds 必须位于 0～60 秒。");

            // 生产环境禁止静默落到 MockAuthBackend。Mock 只能服务 Editor、开发环境和自动化测试；
            // 如果项目确实不需要账号系统，应使用非 prod 环境模板或显式替换整套启动模板，而不是伪装登录成功。
            if (production && !config.UseNetworkLogin)
            {
                throw new BuildFailedException(
                    "[NetworkSecurity] 生产环境禁止关闭 UseNetworkLogin 后静默使用 MockAuthBackend。");
            }

            if (!config.UseNetworkLogin)
                return;

            ValidateAuthServerUrl(config.AuthServerUrl, production);

            if (string.IsNullOrWhiteSpace(config.GameServerHost))
                throw new BuildFailedException("[NetworkSecurity] GameServerHost 不能为空。");
            if (config.GameServerPort <= 0 || config.GameServerPort > 65535)
                throw new BuildFailedException("[NetworkSecurity] GameServerPort 必须位于 1～65535。");
            if (config.NetworkTimeoutSeconds <= 0 || config.NetworkTimeoutSeconds > 120)
                throw new BuildFailedException("[NetworkSecurity] NetworkTimeoutSeconds 必须位于 1～120 秒。");

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
        /// 校验登录服务 URL。登录请求包含账号密码和会话令牌，因此生产环境必须使用 HTTPS，
        /// 且禁止 localhost、回环地址、example.com 占位域名和 URL userinfo，防止凭据误发或明文泄露。
        /// </summary>
        private static void ValidateAuthServerUrl(string value, bool production)
        {
            if (string.IsNullOrWhiteSpace(value))
                throw new BuildFailedException("[NetworkSecurity] UseNetworkLogin=true 时必须配置 AuthServerUrl，禁止回退 Mock 登录。");

            if (!Uri.TryCreate(value.Trim(), UriKind.Absolute, out Uri uri) ||
                !(string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) ||
                  string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)))
            {
                throw new BuildFailedException("[NetworkSecurity] AuthServerUrl 必须是有效的 HTTP(S) 绝对地址。");
            }

            if (!string.IsNullOrEmpty(uri.UserInfo))
                throw new BuildFailedException("[NetworkSecurity] AuthServerUrl 禁止包含 userinfo，账号或密钥不得写入 URL。");

            if (production && !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
                throw new BuildFailedException("[NetworkSecurity] 生产环境 AuthServerUrl 必须使用 HTTPS，登录凭据禁止明文传输。");

            if (production &&
                (uri.IsLoopback ||
                 string.Equals(uri.Host, "localhost", StringComparison.OrdinalIgnoreCase) ||
                 uri.Host.EndsWith(".example.com", StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(uri.Host, "example.com", StringComparison.OrdinalIgnoreCase)))
            {
                throw new BuildFailedException("[NetworkSecurity] 生产环境 AuthServerUrl 仍指向本机、回环或 example.com 占位地址。");
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
