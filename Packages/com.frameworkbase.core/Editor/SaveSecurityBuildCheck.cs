using System;
using Framework.Core;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEngine;

namespace Framework.Editor
{
    /// <summary>
    /// Player 构建前的本地存档安全门禁。
    /// <para>
    /// 目前只有一条规则，但它是个静默失效的坑：存档密钥的项目级 Salt 不设也能跑，
    /// 只是同一台设备上两个 FrameworkBase 产品会派生出同一把存档密钥——甲产品的存档能被
    /// 乙产品解开。运行期没有任何症状，只能在构建期拦。
    /// </para>
    /// </summary>
    public sealed class SaveSecurityBuildCheck : IPreprocessBuildWithReport
    {
        /// <summary>与网络安全门禁相邻执行，顺序不敏感。</summary>
        public int callbackOrder => -89;

        public void OnPreprocessBuild(BuildReport report)
        {
            AppConfigAsset config = Resources.Load<AppConfigAsset>("AppConfig");
            ValidateConfig(config, config?.AppEnv);
        }

        /// <summary>
        /// 校验存档安全配置。提取为纯配置门禁便于 EditMode 覆盖，
        /// 避免规则只存在于构建回调里而无法稳定回归。
        /// </summary>
        /// <param name="config">待校验配置。</param>
        /// <param name="expectedEnvironment">目标发布环境；为空时取配置自身的 AppEnv。</param>
        public static void ValidateConfig(AppConfigAsset config, string expectedEnvironment = null)
        {
            if (config == null)
                throw new BuildFailedException("[SaveSecurity] 缺少 Resources/AppConfig.asset，无法验证存档安全配置。");

            string environment = string.IsNullOrWhiteSpace(expectedEnvironment)
                ? config.AppEnv
                : expectedEnvironment;
            if (!UpdateSecurityIsProduction(environment))
                return;

            string salt = config.SaveSalt?.Trim();
            if (string.IsNullOrEmpty(salt))
            {
                throw new BuildFailedException(
                    "[SaveSecurity] 生产构建必须配置 AppConfig.SaveSalt（建议用包名）。" +
                    "留空会沿用框架兜底 Salt，同设备上的兄弟产品将派生出同一把存档密钥。");
            }

            // AesHelper 是 internal，Framework.AssemblyInfo 已对 Framework.Editor 开放，
            // 故门禁能直接对比兜底常量本身，不必在此复写一份字面量造成双源漂移。
            if (string.Equals(salt, Framework.Save.AesHelper.DefaultAppSalt, StringComparison.Ordinal))
            {
                throw new BuildFailedException(
                    "[SaveSecurity] AppConfig.SaveSalt 仍是框架兜底值，必须改为项目专属值。");
            }
        }

        /// <summary>
        /// 是否面向生产环境。复用热更安全模块的环境判定，避免各门禁各写一套 prod 识别逻辑而漂移。
        /// </summary>
        /// <param name="environment">环境标识。</param>
        /// <returns>是生产环境返回 true。</returns>
        private static bool UpdateSecurityIsProduction(string environment)
            => Framework.HotUpdate.UpdateSecurity.IsProductionEnv(environment);
    }
}
