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
        /// <summary>
        /// Step4 资源更新结果。
        /// Success=false 时 LaunchFlow 必须中止启动并禁止提交 ResourceVersion；
        /// ErrorCode/Message 用于遥测与日志诊断，禁止吞掉。
        /// </summary>
        public struct ResourceUpdateResult
        {
            /// <summary>本步骤是否成功（失败必须中止启动，不得提交版本）。</summary>
            public bool Success;

            /// <summary>本次启动是否走了资源更新链路（服务端 ResourceVersion 高于本地）。</summary>
            public bool ResourceUpdated;

            /// <summary>稳定错误码；成功时为空。</summary>
            public string ErrorCode;

            /// <summary>可诊断错误信息；成功时为空。</summary>
            public string Message;
        }

        /// <summary>Step4 各失败阶段的稳定错误码（用于遥测聚合，一经发布不得改名）。</summary>
        public static class ResourceUpdateStageErrors
        {
            /// <summary>Catalog 检查/更新失败（细分原因见 CatalogUpdateResult.ErrorCode）。</summary>
            public const string CatalogFailed = "resource_catalog_failed";

            /// <summary>下载尺寸查询失败——不能与"无需下载"混同。</summary>
            public const string SizeQueryFailed = "resource_size_query_failed";

            /// <summary>资源 bundle 下载失败。</summary>
            public const string DownloadFailed = "resource_download_failed";
        }

        /// <summary>
        /// 纯逻辑评估器：把 Step4 三个阶段（Catalog → 尺寸查询 → 下载）的结果折叠成 Step4 结论。
        /// <para>
        /// 独立成纯函数的原因：失败传播规则必须能在 EditMode 中直接测试（任务书第三章第 10 点），
        /// 而异步外壳依赖 GameEntry/LoadingWindow 场景对象无法单测。规则：
        /// 1. Catalog 未成功（检查失败/更新失败/取消/无效）→ 整步失败；
        /// 2. 尺寸查询未成功 → 整步失败（"查询失败"≠"无需下载"）；
        /// 3. 尺寸为 0 → 成功且无需下载；
        /// 4. 下载失败 → 整步失败；下载成功 → 整步成功。
        /// </para>
        /// </summary>
        /// <param name="catalog">Catalog 检查/更新结果。</param>
        /// <param name="size">尺寸查询结果；Catalog 失败时传 null（未执行）。</param>
        /// <param name="downloadOk">下载是否成功；未执行到下载阶段时传 null。</param>
        public static ResourceUpdateResult EvaluateResourceUpdate(
            CatalogUpdateResult catalog,
            DownloadSizeResult? size,
            bool? downloadOk)
        {
            if (!catalog.Succeeded)
            {
                return new ResourceUpdateResult
                {
                    Success = false,
                    ResourceUpdated = true,
                    ErrorCode = ResourceUpdateStageErrors.CatalogFailed,
                    Message = $"catalog:{catalog.ErrorCode} {catalog.Message}",
                };
            }

            if (!size.HasValue || !size.Value.Succeeded)
            {
                // Catalog 成功但尺寸查询失败（或流程被截断）：无法得知是否需要下载，失败关闭。
                return new ResourceUpdateResult
                {
                    Success = false,
                    ResourceUpdated = true,
                    ErrorCode = ResourceUpdateStageErrors.SizeQueryFailed,
                    Message = size.HasValue ? size.Value.Message : "尺寸查询未执行",
                };
            }

            if (size.Value.Bytes <= 0)
            {
                // 真正的"无需下载"：Catalog 成功 + 尺寸查询成功 + 0 字节。
                return new ResourceUpdateResult { Success = true, ResourceUpdated = true, ErrorCode = "", Message = "" };
            }

            if (downloadOk != true)
            {
                return new ResourceUpdateResult
                {
                    Success = false,
                    ResourceUpdated = true,
                    ErrorCode = ResourceUpdateStageErrors.DownloadFailed,
                    Message = downloadOk.HasValue ? "bundle 下载失败" : "下载未执行",
                };
            }

            return new ResourceUpdateResult { Success = true, ResourceUpdated = true, ErrorCode = "", Message = "" };
        }

        /// <summary>
        /// Step6c 配置应用结果。
        /// Success=false 时 LaunchFlow 必须中止启动并回滚待确认代码槽——
        /// "配置安装失败"与"本次没有配置更新"是两个语义，绝不允许混同后静默放行。
        /// </summary>
        public struct ConfigApplyResult
        {
            /// <summary>配置应用是否成功（NotIncluded 与 Installed 均视为成功；失败必须中止启动）。</summary>
            public bool Success;

            /// <summary>本次是否执行了配置应用（资源发生更新时才执行）。</summary>
            public bool Applied;

            /// <summary>是否实际安装了新配置数据库。</summary>
            public bool Updated;

            /// <summary>失败终态与诊断信息（来自 ConfigInstallResult）；成功时为空。</summary>
            public string Message;
        }

        /// <summary>判断是否需要做资源更新。</summary>
        public static bool ShouldUpdateResources(HotUpdate.UpdateInfo serverVersion, HotUpdate.UpdateInfo localVersion)
        {
            return serverVersion != null
                && localVersion != null
                && string.Equals(serverVersion.AppVersion, Application.version, System.StringComparison.Ordinal)
                && serverVersion.ResourceVersion > localVersion.ResourceVersion;
        }

        /// <summary>判断是否需要做代码热更（以 CodeVersion 为准）。</summary>
        public static bool ShouldUpdateCode(HotUpdate.UpdateInfo serverVersion, HotUpdate.UpdateInfo localVersion)
        {
            return HotUpdate.VersionManager.ShouldUpdateCode(serverVersion, localVersion);
        }

        /// <summary>
        /// 执行 Step4：Catalog 检查 + 资源下载。
        /// 失败传播契约：Catalog 检查失败、Catalog 更新失败、取消、尺寸查询失败、下载失败
        /// 任一发生都返回 Success=false，LaunchFlow 据此中止启动并禁止提交 ResourceVersion。
        /// </summary>
        public static async UniTask<ResourceUpdateResult> ExecuteResourceUpdateAsync(LoadingWindow loading, bool resourceUpdated)
        {
            if (!resourceUpdated)
            {
                loading.SetProgress(0.65f);
                Debug.Log("[LaunchFlow] Step 4  资源版本一致，跳过");
                return new ResourceUpdateResult { Success = true, ResourceUpdated = false, ErrorCode = "", Message = "" };
            }

            loading.SetStatus("正在检查资源更新...");
            loading.SetProgress(0.25f);

            // ── Step 4a：Catalog 检查 + 更新 ────────────────────────────────
            CatalogUpdateResult catalogResult = await GameEntry.Resource.CheckAndUpdateCatalogsAsync();
            Debug.Log($"[LaunchFlow] Step 4a  Catalog 结果: {catalogResult}");
            if (!catalogResult.Succeeded)
            {
                // 检查失败 ≠ 没有更新：此时继续走下载/提交会把失败固化成"已是最新版本"。
                Debug.LogError($"[LaunchFlow] Step 4a  Catalog 检查/更新失败，中止本次资源更新: {catalogResult}");
                return EvaluateResourceUpdate(catalogResult, null, null);
            }

            // ── Step 4b：待下载尺寸查询 ─────────────────────────────────────
            const string RemoteLabel = "remote";
            DownloadSizeResult sizeResult = await GameEntry.Resource.TryGetDownloadSizeAsync(RemoteLabel);
            if (!sizeResult.Succeeded)
            {
                Debug.LogError($"[LaunchFlow] Step 4b  下载尺寸查询失败，中止本次资源更新: {sizeResult}");
                return EvaluateResourceUpdate(catalogResult, sizeResult, null);
            }

            long totalBytes = sizeResult.Bytes;
            Debug.Log($"[LaunchFlow] Step 4b  待下载: {totalBytes / 1024f / 1024f:0.##} MB");

            if (totalBytes <= 0)
            {
                loading.SetProgress(0.65f);
                Debug.Log("[LaunchFlow] Step 4  所有 bundle 已是最新，无需下载");
                return EvaluateResourceUpdate(catalogResult, sizeResult, null);
            }

            // ── Step 4c：bundle 下载 ────────────────────────────────────────
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
                Debug.LogError("[LaunchFlow] Step 4  资源下载失败");
            else
                Debug.Log("[LaunchFlow] Step 4  资源下载完成");
            return EvaluateResourceUpdate(catalogResult, sizeResult, ok);
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

        /// <summary>
        /// 执行 Step6c：应用配置更新（仅资源发生更新时尝试）。
        /// 失败传播契约：配置下载失败 / 校验失败 / 替换失败 / 重载失败任一发生都返回 Success=false，
        /// LaunchFlow 据此中止启动；"本次发行不包含配置数据库"（NotIncluded）是正常成功路径。
        /// </summary>
        public static async UniTask<ConfigApplyResult> ExecuteConfigApplyAsync(LoadingWindow loading, bool resourceUpdated)
        {
            if (!resourceUpdated)
                return new ConfigApplyResult { Success = true, Applied = false, Updated = false, Message = "" };

            loading.SetStatus("正在应用配置更新...");
            loading.SetProgress(0.84f);
            ConfigInstallResult installResult = await GameEntry.RefData.UpdateDatabaseFromAddressablesAsync();

            if (!installResult.Succeeded)
            {
                Debug.LogError($"[LaunchFlow] Step 6c  配置数据库安装失败，中止启动: {installResult}");
                return new ConfigApplyResult
                {
                    Success = false,
                    Applied = true,
                    Updated = false,
                    Message = $"{installResult.Status}:{installResult.Message}",
                };
            }

            Debug.Log(installResult.DatabaseChanged
                ? "[LaunchFlow] Step 6c  热更配置数据库已应用（备份保留至启动确认）"
                : "[LaunchFlow] Step 6c  本次发行不包含热更配置数据库，继续使用当前配置");

            return new ConfigApplyResult
            {
                Success = true,
                Applied = true,
                Updated = installResult.DatabaseChanged,
                Message = "",
            };
        }
    }
}
