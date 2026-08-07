using Framework.Core;
using Framework.Editor;
using Framework.Save;
using NUnit.Framework;
using UnityEditor.Build;
using UnityEngine;

namespace Framework.Tests
{
    /// <summary>
    /// 本地存档安全构建门禁测试。守的是一个运行期毫无症状的坑：
    /// 项目级 Salt 不设也能跑，只是同设备上的兄弟产品会派生出同一把存档密钥。
    /// </summary>
    public sealed class SaveSecurityBuildCheckTests
    {
        private AppConfigAsset _config;

        [SetUp]
        public void SetUp()
        {
            _config = ScriptableObject.CreateInstance<AppConfigAsset>();
            _config.AppEnv = "dev";
        }

        [TearDown]
        public void TearDown()
        {
            Object.DestroyImmediate(_config);
        }

        [Test]
        public void 开发环境未设Salt_不拦截()
        {
            // 开发期沿用兜底 Salt 是常态，拦下来只会妨碍上手。
            Assert.DoesNotThrow(() => SaveSecurityBuildCheck.ValidateConfig(_config));
        }

        [Test]
        public void 生产构建未设Salt_拒绝构建()
        {
            _config.AppEnv = "prod";
            Assert.Throws<BuildFailedException>(() => SaveSecurityBuildCheck.ValidateConfig(_config));
        }

        [Test]
        public void 生产构建仍用框架兜底Salt_拒绝构建()
        {
            _config.AppEnv = "prod";
            _config.SaveSalt = AesHelper.DefaultAppSalt;

            // 把兜底值抄进配置不算"设过"，那正是要防的情况。
            Assert.Throws<BuildFailedException>(() => SaveSecurityBuildCheck.ValidateConfig(_config));
        }

        [Test]
        public void 生产构建设置项目专属Salt_通过门禁()
        {
            _config.AppEnv = "prod";
            _config.SaveSalt = "com.yourcompany.yourgame";

            Assert.DoesNotThrow(() => SaveSecurityBuildCheck.ValidateConfig(_config));
        }

        [Test]
        public void 目标发布prod但配置仍为dev_按目标环境拦截()
        {
            _config.AppEnv = "dev";

            // 与网络门禁同款：发布入口显式传目标环境，防止用低环境配置发高环境产物。
            Assert.Throws<BuildFailedException>(() => SaveSecurityBuildCheck.ValidateConfig(_config, "prod"));
        }
    }
}
