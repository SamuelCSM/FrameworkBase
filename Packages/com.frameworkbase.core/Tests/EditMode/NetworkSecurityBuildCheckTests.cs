using Framework.Core;
using Framework.Editor;
using NUnit.Framework;
using UnityEditor.Build;
using UnityEngine;

namespace Framework.Tests
{
    /// <summary>
    /// 登录服务与游戏长连接构建安全门禁测试。重点防止生产包静默使用 Mock 登录，
    /// 或把账号密码、会话令牌发送到 HTTP / localhost / 占位域名。
    /// </summary>
    public sealed class NetworkSecurityBuildCheckTests
    {
        private AppConfigAsset _config;

        [SetUp]
        public void SetUp()
        {
            _config = ScriptableObject.CreateInstance<AppConfigAsset>();
            _config.AppEnv = "dev";
            _config.UseNetworkLogin = true;
            _config.AuthServerUrl = "http://127.0.0.1:8080/auth/login";
            _config.GameServerHost = "127.0.0.1";
            _config.GameServerPort = 9000;
            _config.NetworkTimeoutSeconds = 30;
            _config.UseTls = false;
        }

        [TearDown]
        public void TearDown()
        {
            Object.DestroyImmediate(_config);
        }

        [Test]
        public void 开发环境允许本机Http登录服务()
        {
            Assert.DoesNotThrow(() => NetworkSecurityBuildCheck.ValidateConfig(_config));
        }

        [Test]
        public void 启用网络登录但地址为空_构建失败而不是回退Mock()
        {
            _config.AuthServerUrl = string.Empty;
            Assert.Throws<BuildFailedException>(() => NetworkSecurityBuildCheck.ValidateConfig(_config));
        }

        [Test]
        public void 生产环境关闭网络登录_拒绝静默使用Mock()
        {
            ConfigureProduction();
            _config.UseNetworkLogin = false;
            Assert.Throws<BuildFailedException>(() => NetworkSecurityBuildCheck.ValidateConfig(_config));
        }

        [Test]
        public void 生产环境Http登录地址_拒绝明文凭据传输()
        {
            ConfigureProduction();
            _config.AuthServerUrl = "http://auth.game.test/login";
            Assert.Throws<BuildFailedException>(() => NetworkSecurityBuildCheck.ValidateConfig(_config));
        }

        [Test]
        public void 生产环境回环或占位登录地址_拒绝构建()
        {
            ConfigureProduction();
            _config.AuthServerUrl = "https://localhost/login";
            Assert.Throws<BuildFailedException>(() => NetworkSecurityBuildCheck.ValidateConfig(_config));

            _config.AuthServerUrl = "https://auth.example.com/login";
            Assert.Throws<BuildFailedException>(() => NetworkSecurityBuildCheck.ValidateConfig(_config));
        }

        [Test]
        public void 目标发布prod但AppConfig仍为dev_拒绝环境降级绕过门禁()
        {
            _config.AppEnv = "dev";
            Assert.Throws<BuildFailedException>(() => NetworkSecurityBuildCheck.ValidateConfig(_config, "prod"));
        }

        [Test]
        public void 非法后台宽限或探活超时_构建期拒绝()
        {
            _config.NetworkBackgroundGraceSeconds = -1f;
            Assert.Throws<BuildFailedException>(() => NetworkSecurityBuildCheck.ValidateConfig(_config));

            _config.NetworkBackgroundGraceSeconds = 10f;
            _config.NetworkForegroundProbeTimeoutSeconds = 0f;
            Assert.Throws<BuildFailedException>(() => NetworkSecurityBuildCheck.ValidateConfig(_config));
        }

        [Test]
        public void 生产环境合法Https登录与Tls游戏连接_通过门禁()
        {
            ConfigureProduction();
            Assert.DoesNotThrow(() => NetworkSecurityBuildCheck.ValidateConfig(_config));
        }

        // ── 出网端点统一门禁（遥测 / 远程配置）─────────────────────────────

        [Test]
        public void 遥测端点留空_视为未启用不拦截()
        {
            ConfigureProduction();
            _config.CrashReportUrl = string.Empty;
            _config.AnalyticsUrl = string.Empty;
            _config.RemoteConfigUrl = string.Empty;

            Assert.DoesNotThrow(() => NetworkSecurityBuildCheck.ValidateConfig(_config));
        }

        [Test]
        public void 生产环境合法Https遥测端点_通过门禁()
        {
            ConfigureProduction();
            _config.CrashReportUrl = "https://crash.game.test/report";
            _config.AnalyticsUrl = "https://analytics.game.test/collect";
            _config.RemoteConfigUrl = "https://config.game.test/v1?channel=default";
            EnableConsentGate();

            // 远程配置端点带 Query 是正常用法（服务端按渠道定向），不应被拦。
            Assert.DoesNotThrow(() => NetworkSecurityBuildCheck.ValidateConfig(_config));
        }

        // ── 隐私同意闸门（生产构建）───────────────────────────────────────

        [Test]
        public void 生产构建配了遥测端点却未开同意闸门_拒绝构建()
        {
            ConfigureProduction();
            _config.AnalyticsUrl = "https://analytics.game.test/collect";
            _config.RequirePrivacyConsentForAnalytics = false;

            // 闸门关着意味着采集先于用户同意发生，属上架审核会问的合规问题。
            Assert.Throws<BuildFailedException>(() => NetworkSecurityBuildCheck.ValidateConfig(_config));
        }

        [Test]
        public void 开了同意闸门但协议版本非法_拒绝构建()
        {
            ConfigureProduction();
            _config.AnalyticsUrl = "https://analytics.game.test/collect";
            _config.RequirePrivacyConsentForAnalytics = true;
            _config.PrivacyPolicyVersion = 0;

            // 版本号为 0 时 IsAccepted 恒 false，采集会被永久关死——配置错误而非合规选择。
            Assert.Throws<BuildFailedException>(() => NetworkSecurityBuildCheck.ValidateConfig(_config));
        }

        [Test]
        public void 生产构建未配置任何遥测端点_不要求同意闸门()
        {
            ConfigureProduction();
            _config.RequirePrivacyConsentForAnalytics = false;

            // 没有出网采集能力就没有 consent-before-collection 问题，不该强加要求。
            Assert.DoesNotThrow(() => NetworkSecurityBuildCheck.ValidateConfig(_config));
        }

        /// <summary>打开同意闸门并给出有效协议版本，供"端点合法即应通过"的用例排除闸门这一变量。</summary>
        private void EnableConsentGate()
        {
            _config.RequirePrivacyConsentForAnalytics = true;
            _config.PrivacyPolicyVersion = 1;
        }

        [Test]
        public void 生产环境遥测端点为明文Http_拒绝构建()
        {
            ConfigureProduction();
            _config.AnalyticsUrl = "http://analytics.game.test/collect";

            Assert.Throws<BuildFailedException>(() => NetworkSecurityBuildCheck.ValidateConfig(_config));
        }

        [Test]
        public void 生产环境端点指向本机或占位域名_拒绝构建()
        {
            ConfigureProduction();
            _config.CrashReportUrl = "https://127.0.0.1/report";
            Assert.Throws<BuildFailedException>(() => NetworkSecurityBuildCheck.ValidateConfig(_config));

            _config.CrashReportUrl = "https://crash.example.com/report";
            Assert.Throws<BuildFailedException>(() => NetworkSecurityBuildCheck.ValidateConfig(_config));
        }

        [Test]
        public void 端点URL含凭据_开发环境也拒绝构建()
        {
            // userinfo 与环境无关：把密钥写进 URL 在任何环境都会顺着日志和代理泄露。
            _config.RemoteConfigUrl = "https://user:secret@config.game.test/v1";

            Assert.Throws<BuildFailedException>(() => NetworkSecurityBuildCheck.ValidateConfig(_config));
        }

        [Test]
        public void 关闭网络登录时_遥测端点仍受门禁()
        {
            ConfigureProduction();
            _config.AppEnv = "dev";        // 关闭网络登录在 prod 会先撞 Mock 门禁，此处只验端点这一条
            _config.UseNetworkLogin = false;
            _config.AnalyticsUrl = "not-a-url";

            // 端点校验若排在 UseNetworkLogin 提前返回之后，关掉登录就等于绕过这几条规则。
            Assert.Throws<BuildFailedException>(() => NetworkSecurityBuildCheck.ValidateConfig(_config));
        }

        private void ConfigureProduction()
        {
            _config.AppEnv = "prod";
            _config.UseNetworkLogin = true;
            _config.AuthServerUrl = "https://auth.game.test/login";
            _config.GameServerHost = "game.game.test";
            _config.GameServerPort = 443;
            _config.NetworkTimeoutSeconds = 30;
            _config.UseTls = true;
            _config.TlsServerName = "game.game.test";
            _config.AllowPinnedCertificateWithoutSystemTrust = false;
        }
    }
}
