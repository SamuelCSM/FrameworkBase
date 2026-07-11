using System.Linq;
using Framework.Core;
using Framework.Editor.Release;
using Framework.HotUpdate;
using NUnit.Framework;
using UnityEngine;

namespace Framework.Tests
{
    /// <summary>
    /// 客户端公钥环与发布 Profile 的跨配置契约测试。
    /// 清单 KeyId 取自 Profile.SigningKeyRef，客户端按 KeyId 从 AppConfig 公钥环选公钥验签：
    /// 两份配置任何一侧漂移（改引用名、删环条目、换公钥材料）都会导致该环境签出的清单被
    /// 线上客户端全量拒收，属于必须在 CI 拦截的发布事故。
    /// </summary>
    public class ManifestKeyRingContractTests
    {
        /// <summary>接入 GitHub Environments 审批链的流水线环境；staging 仅有 Profile、暂未接入云端流水线。</summary>
        private static readonly string[] PipelineEnvironments = { "dev", "qa", "prod" };

        [Test]
        public void 公钥环配置合法_KeyId唯一且公钥可导入不含私钥参数()
        {
            AppConfigAsset config = LoadAppConfig();
            Assert.IsTrue(
                UpdateSecurity.ValidatePublicKeyConfiguration(
                    config.UpdateManifestPublicKey, config.UpdateManifestPublicKeys, out string reason),
                reason);
        }

        [Test]
        public void 流水线环境的SigningKeyRef_在客户端公钥环中均有对应公钥()
        {
            AppConfigAsset config = LoadAppConfig();
            foreach (string env in PipelineEnvironments)
            {
                ReleaseProfile profile = ReleaseProfileStore.TryLoad(env, out string error);
                Assert.IsNotNull(profile, $"环境 {env} 的 Profile 加载失败：{error}");
                bool found = config.UpdateManifestPublicKeys != null && config.UpdateManifestPublicKeys
                    .Any(entry => entry != null && entry.KeyId == profile.SigningKeyRef);
                Assert.IsTrue(found,
                    $"公钥环缺少环境 {env} 的 KeyId={profile.SigningKeyRef}，该环境签出的清单将被客户端全量拒收。");
            }
        }

        [Test]
        public void 各环境公钥材料互不复用_单环境私钥泄露不波及其他环境()
        {
            AppConfigAsset config = LoadAppConfig();
            string[] materials = config.UpdateManifestPublicKeys
                .Select(entry => entry.PublicKeyXml)
                .ToArray();
            CollectionAssert.AllItemsAreUnique(
                materials, "不同 KeyId 复用了同一份公钥材料，环境隔离与密钥轮换失效。");
        }

        private static AppConfigAsset LoadAppConfig()
        {
            var config = Resources.Load<AppConfigAsset>("AppConfig");
            Assert.IsNotNull(config, "Resources/AppConfig.asset 不存在，无法校验客户端公钥环。");
            return config;
        }
    }
}
