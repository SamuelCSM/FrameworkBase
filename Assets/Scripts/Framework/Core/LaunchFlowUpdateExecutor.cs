using Cysharp.Threading.Tasks;
using Framework.UI;
using UnityEngine;

namespace Framework.Core
{
    /// <summary>
    /// LaunchFlow 更新相关步骤执行器（Step4~6c）。
    /// 职责：封装资源/代码/配置更新细节，降低 LaunchFlow 主流程复杂度。
    /// </summary>
    public static class LaunchFlowUpdateExecutor
    {
        /// <summary>Step4 资源更新结果。</summary>
        public struct ResourceUpdateResult
        {
            public bool Success;
            public bool ResourceUpdated;
        }

        /// <summary>Step6c 配置应用结果。</summary>
        public struct ConfigApplyResult
        {
            public bool Applied;
            public bool Updated;
        }

        /// <summary>判断是否需要做资源更新。</summary>
        public static bool ShouldUpdateResources(HotUpdate.UpdateInfo serverVersion, HotUpdate.UpdateInfo localVersion)
        {
            return serverVersion != null && serverVersion.ResourceVersion != localVersion.ResourceVersion;
        }

        /// <summary>判断是否需要做代码热更（以 CodeVersion 为准）。</summary>
        public static bool ShouldUpdateCode(HotUpdate.UpdateInfo serverVersion, HotUpdate.UpdateInfo localVersion)
        {
            return HotUpdate.VersionManager.ShouldUpdateCode(serverVersion, localVersion);
        }

        /// <summary>执行 Step4：Catalog 检查 + 资源下载。</summary>
        public static async UniTask<ResourceUpdateResult> ExecuteResourceUpdateAsync(LoadingWindow loading, bool resourceUpdated)
        {
            if (!resourceUpdated)
            {
                loading.SetProgress(0.65f);
                Debug.Log("[LaunchFlow] Step 4  资源版本一致，跳过");
                return new ResourceUpdateResult { Success = true, ResourceUpdated = false };
            }

            loading.SetStatus("正在检查资源更新...");
            loading.SetProgress(0.25f);

            int catalogCount = await GameEntry.Resource.CheckAndUpdateCatalogsAsync();
            Debug.Log($"[LaunchFlow] Step 4a  Catalog 更新数量: {catalogCount}");

            const string RemoteLabel = "remote";
            long totalBytes = await GameEntry.Resource.GetDownloadSizeAsync(RemoteLabel);
            Debug.Log($"[LaunchFlow] Step 4b  待下载: {totalBytes / 1024f / 1024f:0.##} MB");

            if (totalBytes <= 0)
            {
                loading.SetProgress(0.65f);
                Debug.Log("[LaunchFlow] Step 4  所有 bundle 已是最新，无需下载");
                return new ResourceUpdateResult { Success = true, ResourceUpdated = true };
            }

            loading.SetStatus("正在下载资源更新...");
            loading.ShowDownload(totalBytes);

            bool ok = await GameEntry.Resource.DownloadDependenciesAsync(
                RemoteLabel,
                progress =>
                {
                    long downloaded = (long)(progress * totalBytes);
                    loading.UpdateDownloadProgress(
                        Mathf.Lerp(0.3f, 0.65f, progress),
                        downloaded, totalBytes);
                },
                totalBytes);

            loading.HideDownload();

            if (!ok)
            {
                Debug.LogError("[LaunchFlow] Step 4  资源下载失败");
                return new ResourceUpdateResult { Success = false, ResourceUpdated = true };
            }

            Debug.Log("[LaunchFlow] Step 4  资源下载完成");
            return new ResourceUpdateResult { Success = true, ResourceUpdated = true };
        }

        /// <summary>执行 Step5：代码补丁下载（按 CodeVersion 触发）。</summary>
        public static async UniTask<bool> ExecuteCodeUpdateAsync(
            LoadingWindow loading,
            HotUpdate.UpdateInfo serverVersion,
            HotUpdate.UpdateInfo localVersion,
            string updateServerUrl)
        {
            if (!ShouldUpdateCode(serverVersion, localVersion))
            {
                loading.SetProgress(0.8f);
                Debug.Log("[LaunchFlow] Step 5  代码版本一致，跳过");
                return false;
            }

            if (string.IsNullOrEmpty(updateServerUrl))
            {
                Debug.LogError("[LaunchFlow] Step 5  CodeVersion 已变更但未配置 UpdateServerUrl");
                return false;
            }

            loading.SetStatus("正在下载热更补丁...");
            bool ok = await GameEntry.HotUpdate.DownloadCodePatchAsync(
                serverVersion,
                localVersion,
                updateServerUrl,
                progress => loading.SetProgress(Mathf.Lerp(0.65f, 0.8f, progress)));

            if (!ok)
            {
                Debug.LogError("[LaunchFlow] Step 5  代码补丁下载失败");
                return false;
            }

            Debug.Log("[LaunchFlow] Step 5  代码补丁下载完成");
            return true;
        }

        /// <summary>执行 Step6：整包更新闸门。返回 true 表示流程应终止在强更页。</summary>
        public static bool ExecuteFullUpdateGate(LoadingWindow loading, HotUpdate.UpdateInfo serverVersion)
        {
            if (serverVersion == null || serverVersion.Type != HotUpdate.UpdateType.FullUpdate)
                return false;

            Debug.LogWarning("[LaunchFlow] Step 6  需要整包更新");
            loading.ShowForceUpdate(
                $"发现新版本 {serverVersion.AppVersion}，需要前往应用商店更新后才能继续游戏。",
                updateUrl: serverVersion.UpdateUrl
            );
            return true;
        }

        /// <summary>执行 Step6b：准备配置数据库。</summary>
        public static async UniTask<bool> ExecuteConfigPrepareAsync(LoadingWindow loading)
        {
            loading.SetStatus("正在准备配置数据...");
            loading.SetProgress(0.82f);
            bool configReady = await GameEntry.RefData.EnsureDatabaseReadyAsync();
            if (!configReady)
                Debug.LogWarning("[LaunchFlow] Step 6b  配置数据库未就绪，后续读取配置时可能失败");
            else
                Debug.Log("[LaunchFlow] Step 6b  配置数据库已就绪");

            return configReady;
        }

        /// <summary>执行 Step6c：应用配置更新（仅资源发生更新时尝试）。</summary>
        public static async UniTask<ConfigApplyResult> ExecuteConfigApplyAsync(LoadingWindow loading, bool resourceUpdated)
        {
            if (!resourceUpdated)
                return new ConfigApplyResult { Applied = false, Updated = false };

            loading.SetStatus("正在应用配置更新...");
            loading.SetProgress(0.84f);
            bool configUpdated = await GameEntry.RefData.UpdateDatabaseFromAddressablesAsync();
            Debug.Log(configUpdated
                ? "[LaunchFlow] Step 6c  热更配置数据库已应用"
                : "[LaunchFlow] Step 6c  未发现热更配置数据库，继续使用当前配置");

            return new ConfigApplyResult { Applied = true, Updated = configUpdated };
        }
    }
}
