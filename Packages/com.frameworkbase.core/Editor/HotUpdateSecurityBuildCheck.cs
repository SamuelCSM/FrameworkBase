using Framework.Core;
using Framework.HotUpdate;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEngine;

namespace Framework.Editor
{
    /// <summary>
    /// Player 构建前的热更新供应链安全门禁。
    /// <para>
    /// 只要当前包启用了远程热更新，就必须同时满足：更新服务 URL 符合环境传输策略、客户端内置至少一把
    /// 可解析且不含私钥参数的验签公钥、公钥环 KeyId 唯一。任何环境都不得跳过清单验签；开发环境应使用独立开发密钥。
    /// </para>
    /// <para>
    /// 该门禁把“运行时才发现没有信任根”前移到构建期，并阻止误把 RSA 私钥序列化进 AppConfig 和 Player 包体。
    /// </para>
    /// </summary>
    public sealed class HotUpdateSecurityBuildCheck : IPreprocessBuildWithReport
    {
        /// <summary>
        /// 在大多数资源和 Player 构建步骤之前执行，尽早中止不安全构建。
        /// </summary>
        public int callbackOrder => -100;

        /// <summary>
        /// 校验当前 Resources/AppConfig.asset 的远程更新安全配置；失败时抛出 BuildFailedException。
        /// </summary>
        public void OnPreprocessBuild(BuildReport report)
        {
            AppConfigAsset config = Resources.Load<AppConfigAsset>("AppConfig");
            if (config == null)
            {
                throw new BuildFailedException(
                    "[HotUpdateSecurityBuildCheck] 缺少 Resources/AppConfig.asset，无法确认热更新安全策略。");
            }

            if (!UpdateSecurity.ValidateUpdateServerUrl(config.UpdateServerUrl, config.AppEnv, out string reason))
            {
                throw new BuildFailedException(
                    $"[HotUpdateSecurityBuildCheck] 更新服务 URL 未通过安全准入：{reason}");
            }

            if (config.EnableHotUpdate &&
                UpdateSecurity.IsProductionEnv(config.AppEnv) &&
                string.IsNullOrWhiteSpace(config.UpdateServerUrl))
            {
                throw new BuildFailedException(
                    "[HotUpdateSecurityBuildCheck] 生产环境启用热更新时必须配置 UpdateServerUrl。");
            }

            bool remoteHotUpdateEnabled = config.EnableHotUpdate && !string.IsNullOrWhiteSpace(config.UpdateServerUrl);
            if (!remoteHotUpdateEnabled)
                return;
            if (UpdateSecurity.IsProductionEnv(config.AppEnv) && config.AllowLaunchWhenUpdateCheckFails)
            {
                throw new BuildFailedException(
                    "[HotUpdateSecurityBuildCheck] 强联网生产环境禁止 AllowLaunchWhenUpdateCheckFails，更新检查必须失败关闭。");
            }

            if (!UpdateSecurity.ValidatePublicKeyConfiguration(
                    config.UpdateManifestPublicKey,
                    config.UpdateManifestPublicKeys,
                    out reason))
            {
                throw new BuildFailedException(
                    $"[HotUpdateSecurityBuildCheck] 清单验签公钥配置无效：{reason}");
            }
        }
    }
}
