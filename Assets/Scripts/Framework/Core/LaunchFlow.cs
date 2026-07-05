using System;
using Cysharp.Threading.Tasks;
using Framework.Input;
using Framework.UI;
using UnityEngine;

namespace Framework.Core
{
    /// <summary>
    /// 游戏启动流程控制器。
    ///
    /// 职责：驱动 LoadingWindow 完成 9 步启动序列（版本检查 → 热更 → 游戏逻辑接管）。
    /// GameEntry 只负责框架 Manager 初始化，LaunchFlow 专注业务启动，两者职责分离。
    ///
    /// ┌─────────────────────────────────────────────────────┐
    /// │  Step 1  读本地版本                                   │
    /// │  Step 2  初始化 Addressables                          │
    /// │  Step 3  检查 version.json（需配置 UpdateServerUrl）   │
    /// │  Step 4  资源热更：Catalog 检查 → 下载新 bundle        │
    /// │  Step 5  代码热更：下载热更程序集组                    │
    /// │  Step 6  整包更新提示（AppVersion 不一致时）           │
    /// │  Step 7  加载 AOT 泛型补充元数据（须在热更 DLL 之前）     │
    /// │  Step 8  加载 HybridCLR 热更程序集                       │
    /// │  Step 9  StartHotfix → 游戏逻辑接管，淡出 Loading       │
    /// └─────────────────────────────────────────────────────┘
    /// </summary>
    public static class LaunchFlow
    {
        /// <summary>
        /// 启动链路阶段枚举（用于结构化埋点与耗时统计）。
        /// </summary>
        private enum LaunchPhase
        {
            /// <summary>读取本地版本（persistent / streaming）。</summary>
            LocalVersionLoad,
            /// <summary>初始化 Addressables 运行时。</summary>
            AddressablesInit,
            /// <summary>请求服务器 version.json 并判定更新类型。</summary>
            ServerVersionCheck,
            /// <summary>资源热更（Catalog 检查 + bundle 下载）。</summary>
            ResourceUpdate,
            /// <summary>代码热更（热更程序集组下载）。</summary>
            CodeUpdate,
            /// <summary>整包更新闸门（FullUpdate 时阻断后续流程）。</summary>
            FullUpdateGate,
            /// <summary>准备运行时配置库（确保 config.db 可用）。</summary>
            ConfigPrepare,
            /// <summary>应用热更配置库（Addressables RefData）。</summary>
            ConfigApply,
            /// <summary>加载 HybridCLR 热更程序集。</summary>
            HotUpdateAssemblyLoad,
            /// <summary>加载 AOT 元数据补充。</summary>
            MetadataLoad,
            /// <summary>启动 Hotfix 入口并交接业务逻辑。</summary>
            HotfixStart
        }

        /// <summary>
        /// 执行完整启动流程（带重试循环）。
        /// 失败时 LoadingWindow 显示重试面板，玩家点击重试后重新执行所有步骤。
        /// </summary>
        public static async UniTask<LaunchFlowOutcome> RunAsync(LoadingWindow loading)
        {
            while (true)
            {
                LaunchFlowOutcome outcome = await RunStepsAsync(loading);
                if (outcome != LaunchFlowOutcome.Failed)
                    return outcome;

                // 显示错误面板，等待玩家点击重试
                var tcs = new UniTaskCompletionSource();
                loading.ShowError(
                    "网络连接失败，请检查网络后重试",
                    onRetry: () => tcs.TrySetResult()
                );
                await tcs.Task;

                loading.HideError();
                loading.SetProgress(0f);
            }
        }

        // ── 9 步启动序列 ──────────────────────────────────────────────────────

        private static async UniTask<LaunchFlowOutcome> RunStepsAsync(LoadingWindow loading)
        {
            Debug.Log("[LaunchFlow] ========== 游戏启动流程开始 ==========");
            var runMetric = LaunchTelemetryHelper.BeginRunMetric();

            using (InputBlockScope.Begin("LaunchLoading"))
            {
            try
            {
                // ── Step 1: 读取本地版本 ──────────────────────────
                var step1 = LaunchTelemetryHelper.BeginPhaseMetric(runMetric, LaunchPhase.LocalVersionLoad, "step01_local_version_load");
                loading.SetStatus("正在读取版本信息...");
                loading.SetProgress(0.05f);
                var localVersion = HotUpdate.VersionManager.GetLocalVersion();
                loading.SetVersion(
                    localVersion.AppVersion,
                    localVersion.ResourceVersion,
                    localVersion.CodeVersion);
                Debug.Log($"[LaunchFlow] Step 1  App={localVersion.AppVersion} " +
                          $"Resource={localVersion.ResourceVersion} Code={localVersion.CodeVersion}");
                LaunchTelemetryHelper.EndPhaseMetric(step1, true, $"app={localVersion.AppVersion},res={localVersion.ResourceVersion},code={localVersion.CodeVersion}");

                // ── Step 2: 初始化 Addressables ───────────────────
                var step2 = LaunchTelemetryHelper.BeginPhaseMetric(runMetric, LaunchPhase.AddressablesInit, "step02_addressables_init");
                loading.SetStatus("正在初始化资源系统...");
                loading.SetProgress(0.1f);
                await GameEntry.Resource.InitializeAsync();
                Debug.Log("[LaunchFlow] Step 2  Addressables 初始化完成");
                LaunchTelemetryHelper.EndPhaseMetric(step2, true);

                // ── Step 3: 检查服务器版本 ────────────────────────
                var step3 = LaunchTelemetryHelper.BeginPhaseMetric(runMetric, LaunchPhase.ServerVersionCheck, "step03_server_version_check");
                string updateUrl = GetUpdateServerUrl();
                HotUpdate.UpdateInfo serverVersion = null;

                if (string.IsNullOrEmpty(updateUrl))
                {
                    loading.SetStatus("未配置更新服务器，跳过版本检查");
                    loading.SetProgress(0.2f);
                    Debug.Log("[LaunchFlow] Step 3  未配置 UpdateServerUrl，跳过");
                }
                else
                {
                    loading.SetStatus("正在检查更新...");
                    loading.SetProgress(0.2f);
                    serverVersion = await GameEntry.HotUpdate.CheckUpdateAsync(updateUrl);

                    if (serverVersion == null)
                        Debug.LogWarning("[LaunchFlow] Step 3  获取服务器版本失败，使用本地版本继续");
                    else
                        Debug.Log($"[LaunchFlow] Step 3  Server: App={serverVersion.AppVersion} " +
                                  $"Resource={serverVersion.ResourceVersion} Type={serverVersion.Type}");
                }
                LaunchTelemetryHelper.EndPhaseMetric(step3, true, serverVersion == null ? "server_version=null" : $"server_app={serverVersion.AppVersion},server_res={serverVersion.ResourceVersion},type={serverVersion.Type}");

                // ── Step 4: 资源热更 ──────────────────────────────
                var step4 = LaunchTelemetryHelper.BeginPhaseMetric(runMetric, LaunchPhase.ResourceUpdate, "step04_resource_update");
                bool resourceUpdated = LaunchFlowUpdateExecutor.ShouldUpdateResources(serverVersion, localVersion);
                var resourceUpdateResult = await LaunchFlowUpdateExecutor.ExecuteResourceUpdateAsync(loading, resourceUpdated);
                if (!resourceUpdateResult.Success)
                {
                    LaunchTelemetryHelper.EndPhaseMetric(step4, false, "download_failed");
                            LaunchTelemetryHelper.FinalizeRunMetric(runMetric, false, TelemetryErrorCodes.Launch.ResourceDownloadFailed);
                            return LaunchFlowOutcome.Failed;
                }

                // 资源热更成功后写回 ResourceVersion，避免下次启动重复检查/下载。
                if (resourceUpdateResult.ResourceUpdated && serverVersion != null)
                    HotUpdate.VersionManager.CommitResourceUpdate(serverVersion);

                LaunchTelemetryHelper.EndPhaseMetric(step4, true,
                    resourceUpdateResult.ResourceUpdated ? "resource_updated=true" : "resource_updated=false");

                // ── Step 5: 代码热更 ──────────────────────────────
                var step5 = LaunchTelemetryHelper.BeginPhaseMetric(runMetric, LaunchPhase.CodeUpdate, "step05_code_update");
                bool codeUpdated = await LaunchFlowUpdateExecutor.ExecuteCodeUpdateAsync(
                    loading, serverVersion, localVersion, updateUrl);
                if (LaunchFlowUpdateExecutor.ShouldUpdateCode(serverVersion, localVersion) && !codeUpdated)
                {
                    LaunchTelemetryHelper.EndPhaseMetric(step5, false, "download_failed");
                    LaunchTelemetryHelper.FinalizeRunMetric(runMetric, false, TelemetryErrorCodes.Launch.CodeDownloadFailed);
                    return LaunchFlowOutcome.Failed;
                }
                LaunchTelemetryHelper.EndPhaseMetric(step5, true, codeUpdated ? "code_updated=true" : "code_updated=false");

                // ── Step 6: 整包更新检测 ──────────────────────────
                var step6 = LaunchTelemetryHelper.BeginPhaseMetric(runMetric, LaunchPhase.FullUpdateGate, "step06_full_update_gate");
                if (LaunchFlowUpdateExecutor.ExecuteFullUpdateGate(loading, serverVersion))
                {
                    LaunchTelemetryHelper.EndPhaseMetric(step6, true, "full_update_required=true");
                    LaunchTelemetryHelper.FinalizeRunMetric(runMetric, true, TelemetryErrorCodes.Launch.FullUpdateGateBlocked);
                    // 强更闸门：不销毁 Loading，也不进入登录。
                    return LaunchFlowOutcome.BlockedOnForceUpdate;
                }
                LaunchTelemetryHelper.EndPhaseMetric(step6, true, "full_update_required=false");

                // ── Step 6b: 准备配置数据库 ───────────────────────
                var step6b = LaunchTelemetryHelper.BeginPhaseMetric(runMetric, LaunchPhase.ConfigPrepare, "step06b_config_prepare");
                bool configReady = await LaunchFlowUpdateExecutor.ExecuteConfigPrepareAsync(loading);
                LaunchTelemetryHelper.EndPhaseMetric(step6b, true, $"config_ready={configReady}");

                // ── Step 6c: 应用热更配置数据库 ───────────────────
                var step6c = LaunchTelemetryHelper.BeginPhaseMetric(runMetric, LaunchPhase.ConfigApply, "step06c_config_apply");
                var configApplyResult = await LaunchFlowUpdateExecutor.ExecuteConfigApplyAsync(
                    loading,
                    resourceUpdateResult.ResourceUpdated);
                if (configApplyResult.Applied)
                    LaunchTelemetryHelper.EndPhaseMetric(step6c, true, $"resource_updated=true,config_updated={configApplyResult.Updated}");
                else
                    LaunchTelemetryHelper.EndPhaseMetric(step6c, true, "resource_updated=false,skip_apply");

                // ── 热更总开关：无业务热更程序集的项目（纯框架壳/单机）跳过 Step 7-9，直接进入登录 ──
                if (!IsHotUpdateEnabled())
                {
                    loading.SetStatus("正在进入游戏...");
                    loading.SetProgress(0.95f);
                    Debug.Log("[LaunchFlow] 热更已关闭（AppConfig.EnableHotUpdate=false），跳过 AOT 元数据 / 热更程序集 / StartHotfix");
                    await loading.HideAsync();
                    Debug.Log("[LaunchFlow] ========== 启动流程完成（纯框架模式）==========");
                    LaunchTelemetryHelper.FinalizeRunMetric(runMetric, true, TelemetryErrorCodes.Launch.Ok);
                    return LaunchFlowOutcome.ReadyForLogin;
                }

                // ── Step 7: 加载 AOT 元数据（须在热更 DLL 之前）────
                var step7 = LaunchTelemetryHelper.BeginPhaseMetric(runMetric, LaunchPhase.MetadataLoad, "step07_metadata_load");
                loading.SetStatus("正在加载运行时元数据...");
                loading.SetProgress(0.85f);
                bool metadataOk = await GameEntry.HotUpdate.LoadMetadataAsync();
                if (!metadataOk)
                {
                    LaunchTelemetryHelper.EndPhaseMetric(step7, false, "metadata_load_failed");
                    LaunchTelemetryHelper.FinalizeRunMetric(runMetric, false, TelemetryErrorCodes.Launch.MetadataLoadFailed);
                    return LaunchFlowOutcome.Failed;
                }
                Debug.Log("[LaunchFlow] Step 7  AOT 元数据加载完成");
                LaunchTelemetryHelper.EndPhaseMetric(step7, true);

                // ── Step 8: 加载热更程序集 ─────────────────────────
                var step8 = LaunchTelemetryHelper.BeginPhaseMetric(runMetric, LaunchPhase.HotUpdateAssemblyLoad, "step08_hotupdate_assembly_load");
                loading.SetStatus("正在加载游戏数据...");
                loading.SetProgress(0.9f);
                bool assemblyOk = await GameEntry.HotUpdate.LoadHotUpdateAssemblyAsync();
                if (!assemblyOk)
                {
                    LaunchTelemetryHelper.EndPhaseMetric(step8, false, "assembly_load_failed");
                    LaunchTelemetryHelper.FinalizeRunMetric(runMetric, false, TelemetryErrorCodes.Launch.HotUpdateAssemblyLoadFailed);
                    return LaunchFlowOutcome.Failed;
                }
                Debug.Log("[LaunchFlow] Step 8  HybridCLR 热更程序集加载完成");
                LaunchTelemetryHelper.EndPhaseMetric(step8, true);

                // ── Step 9: 启动热更逻辑，淡出 Loading ───────────
                var step9 = LaunchTelemetryHelper.BeginPhaseMetric(runMetric, LaunchPhase.HotfixStart, "step09_hotfix_start");
                loading.SetStatus("正在进入游戏...");
                loading.SetProgress(0.95f);
                GameEntry.HotUpdate.StartHotfix();
                Debug.Log("[LaunchFlow] Step 9  游戏逻辑启动完成");
                LaunchTelemetryHelper.EndPhaseMetric(step9, true);

                await loading.HideAsync();
                Debug.Log("[LaunchFlow] ========== 启动流程完成 ==========");
                LaunchTelemetryHelper.FinalizeRunMetric(runMetric, true, TelemetryErrorCodes.Launch.Ok);
                return LaunchFlowOutcome.ReadyForLogin;
            }
            catch (Exception ex)
            {
                Debug.LogError($"[LaunchFlow] 启动流程异常: {ex.Message}\n{ex.StackTrace}");
                LaunchTelemetryHelper.FinalizeRunMetric(runMetric, false, TelemetryErrorCodes.Launch.UnhandledException);
                return LaunchFlowOutcome.Failed;
            }
            }
        }

        // ── 工具方法 ──────────────────────────────────────────────────────────

        /// <summary>
        /// 获取热更服务器根 URL。
        /// 读取顺序：
        ///   1. Resources/AppConfig.asset → UpdateServerUrl 字段
        ///   2. PlayerPrefs "UpdateServerUrl"（方便运行时覆盖，用于测试）
        ///   3. 返回空字符串（跳过热更检查）
        /// </summary>
        private static string GetUpdateServerUrl()
        {
            var config = AppConfig.Load();
            if (config != null && !string.IsNullOrEmpty(config.UpdateServerUrl))
                return config.UpdateServerUrl;

            return PlayerPrefs.GetString("UpdateServerUrl", string.Empty);
        }

        /// <summary>
        /// 是否启用热更（AOT 元数据 / HybridCLR 程序集加载 / StartHotfix）。
        /// 读 AppConfig.EnableHotUpdate；无配置时默认启用（保持既有项目行为不变）。
        /// 无热更业务程序集的项目须置 false，否则 Step 8 加载热更 DLL 失败会卡在重试循环。
        /// </summary>
        private static bool IsHotUpdateEnabled()
        {
            var config = AppConfig.Load();
            return config == null || config.EnableHotUpdate;
        }

    }
}
