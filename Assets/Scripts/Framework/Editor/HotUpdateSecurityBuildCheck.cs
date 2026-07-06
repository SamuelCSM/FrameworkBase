using Framework.Core;
using Framework.HotUpdate;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEngine;

namespace Framework.Editor
{
    /// <summary>
    /// 打包前热更安全门禁：把"配置错误 → 运行时静默跳过热更"的问题提前到构建期拦截。
    ///
    /// 规则（仅检查 Resources/AppConfig.asset）：
    ///   1. UpdateServerUrl 未通过 <see cref="UpdateSecurity.ValidateUpdateServerUrl"/>
    ///      （如 prod + 明文 HTTP）→ <b>构建失败</b>；
    ///   2. prod 环境未配置清单验签公钥 → 构建警告（正式发布前必须补齐）。
    /// </summary>
    public class HotUpdateSecurityBuildCheck : IPreprocessBuildWithReport
    {
        public int callbackOrder => -100; // 尽早执行，避免白跑后续构建步骤

        public void OnPreprocessBuild(BuildReport report)
        {
            var config = Resources.Load<AppConfigAsset>("AppConfig");
            if (config == null)
            {
                Debug.LogWarning("[HotUpdateSecurityBuildCheck] 未找到 Resources/AppConfig.asset，跳过热更安全检查" +
                                 "（无配置时运行时不会执行热更）");
                return;
            }

            if (!UpdateSecurity.ValidateUpdateServerUrl(config.UpdateServerUrl, config.AppEnv, out string reason))
            {
                throw new BuildFailedException(
                    $"[HotUpdateSecurityBuildCheck] 热更安全检查未通过，构建中止：{reason}");
            }

            if (UpdateSecurity.IsProductionEnv(config.AppEnv) &&
                !string.IsNullOrEmpty(config.UpdateServerUrl) &&
                string.IsNullOrWhiteSpace(config.UpdateManifestPublicKey))
            {
                Debug.LogWarning("[HotUpdateSecurityBuildCheck] AppEnv=prod 但未配置清单验签公钥（UpdateManifestPublicKey），" +
                                 "热更清单将处于无签名保护状态。正式发布前请生成密钥对并填入公钥" +
                                 "（菜单 Framework → Hot Update Security → Generate Signing Key Pair）。");
            }
        }
    }
}
