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
