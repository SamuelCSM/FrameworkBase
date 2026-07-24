using System;
using System.Threading;
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
        public static async UniTask<LaunchFlowOutcome> RunAsync(LoadingWindow loading, CancellationToken cancellationToken = default)
        {
            while (true)
            {
                LaunchFlowOutcome outcome = await RunStepsAsync(loading, cancellationToken);
                if (outcome != LaunchFlowOutcome.Failed)
                    return outcome;

                // 显示错误面板，等待玩家点击重试
                var tcs = new UniTaskCompletionSource();
                loading.ShowError(
                    Language.GetOrDefault("#1_launch_network_error", "网络连接失败，请检查网络后重试"),
                    onRetry: () => tcs.TrySetResult()
                );
                await tcs.Task;

                loading.HideError();
                loading.SetProgress(0f);
            }
        }

        // ── 9 步启动序列 ──────────────────────────────────────────────────────

        private static async UniTask<LaunchFlowOutcome> RunStepsAsync(LoadingWindow loading, CancellationToken cancellationToken = default)
        {
            GameLog.Log("[LaunchFlow] ========== 游戏启动流程开始 ==========");
            var runMetric = LaunchTelemetryHelper.BeginRunMetric();

            // language 片提前就绪（ADR-006）：在第一条 Loading 文案之前完成小片提取，
            // 使首装启动早期文案即可走配表本地化（后续启动为存在性检查，幂等且廉价）。
            // 失败不阻断——Language.GetOrDefault 的源语言兜底仍在（三级取值的异常保险）。
            try
            {
                await GameEntry.RefData.EnsureShardReadyAsync(ConfigShardCatalog.LanguageShardFileName);
            }
            catch (Exception ex)
            {
                GameLog.Log($"[LaunchFlow] language 片提前就绪失败（文案走源语言兜底）：{ex.Message}");
            }

            // 远程配置与启动序列并行拉取：不阻塞启动、失败静默沿用磁盘缓存/代码默认值
            // （FetchAndActivateAsync 自带重入保护，重试循环再次进入本方法不会重复拉取）。
            // 需要硬门控的业务（开关决定登录后流程）自行 await GameEntry.RemoteConfig.FetchAndActivateAsync()。
            GameEntry.RemoteConfig?.FetchAndActivateAsync().Forget();

            using (InputBlockScope.Begin("LaunchLoading"))
            {
            try
            {
                // ── Step 1: 读取本地版本 ──────────────────────────
                var step1 = LaunchTelemetryHelper.BeginPhaseMetric(runMetric, LaunchPhase.LocalVersionLoad, "step01_local_version_load");
                loading.SetStatus(Language.GetOrDefault("#1_launch_reading_version", "正在读取版本信息..."));
                loading.SetProgress(0.05f);
                var localVersion = HotUpdate.VersionManager.GetLocalVersion();
                loading.SetVersion(
                    localVersion.AppVersion,
                    localVersion.ResourceVersion,
                    localVersion.CodeVersion);
                GameLog.Log($"[LaunchFlow] Step 1  App={localVersion.AppVersion} " +
                          $"Resource={localVersion.ResourceVersion} Code={localVersion.CodeVersion}");
                LaunchTelemetryHelper.EndPhaseMetric(step1, true, $"app={localVersion.AppVersion},res={localVersion.ResourceVersion},code={localVersion.CodeVersion}");

                // ── Step 1.5: 内容发行事务恢复（必须在 Addressables 初始化之前）───
                // 上次启动若安装了新内容（Catalog/配置/代码槽）但未走到统一确认点：
                //   - Catalog：用 LKG 快照覆写 Addressables 缓存目录（初始化前恢复，Addressables 无感知）；
                //   - 配置：恢复上一份已确认数据库（.bak 由 ConfigDatabaseInstaller 保留至确认点）；
                //   - 代码槽：HotUpdateSlotManager.PrepareForLaunch 在程序集加载前自行回滚（既有机制）。
                // 内容级 Crash-loop（已确认发行连续启动失败）触发出厂回退：清 Catalog 缓存 + 恢复出厂配置。
                // 提交阶段中断时必须先前滚代码槽。只有代码槽校验并确认成功后，内容事务才可
                // 丢弃 Catalog 快照并继续前滚配置和 version；否则应保留提交日志，等待修复或下次重试。
                if (GameEntry.HotUpdate.ContentRelease.IsCommitInProgress)
                    GameEntry.HotUpdate.ReplayPendingCommitCodeSlot(GameEntry.HotUpdate.ContentRelease.Committing);

                var recovery = GameEntry.HotUpdate.ContentRelease.PrepareForLaunch();
                if (recovery.CommitReplayed)
                {
                    // 确认阶段被中断：内容侧已前滚（PrepareForLaunch 内），代码槽已前滚（HotUpdateManager.OnInit）。
                    // 此处幂等前滚补完配置与 version.json，最后清除提交日志，使四类内容一致地停在“新内容”。
                    GameEntry.RefData.ConfirmHotUpdateDatabase();
                    HotUpdate.VersionManager.CommitHotUpdateFromRecord(recovery.CommittingRecord);
                    GameEntry.HotUpdate.ContentRelease.EndCommit();
                    GameLog.Warning($"[LaunchFlow] Step 1.5  检测到确认阶段被中断的提交 {recovery.CommittingRecord.ReleaseId}，已前滚补完全部内容");
                }
                else
                {
                    if (recovery.PendingRolledBack || recovery.StateCorruptionHandled ||
                        GameEntry.RefData.HasUnconfirmedDatabaseBackup)
                    {
                        GameEntry.RefData.RestoreLastConfirmedDatabaseIfAny();
                    }
                    if (recovery.FactoryResetPerformed)
                    {
                        GameEntry.RefData.ResetDatabaseToFactoryBaseline();
                        string cause = recovery.StateCorruptionHandled ? "状态损坏且无快照" : "内容级崩溃循环";
                        GameLog.Warning($"[LaunchFlow] Step 1.5  {cause}，已回退出厂内容基线（安全恢复模式）");
                    }
                    if (recovery.StateCorruptionHandled && !recovery.FactoryResetPerformed)
                        GameLog.Warning("[LaunchFlow] Step 1.5  状态文件损坏，已恢复上一份 LKG 内容（安全兜底）");
                    if (recovery.PendingRolledBack)
                        GameLog.Warning($"[LaunchFlow] Step 1.5  未确认发行 {recovery.RolledBackReleaseId} 已回滚");
                }

                // ── Step 2: 初始化 Addressables ───────────────────
                var step2 = LaunchTelemetryHelper.BeginPhaseMetric(runMetric, LaunchPhase.AddressablesInit, "step02_addressables_init");
                loading.SetStatus(Language.GetOrDefault("#1_launch_init_resource", "正在初始化资源系统..."));
                loading.SetProgress(0.1f);
                await GameEntry.Resource.InitializeAsync();
                GameLog.Log("[LaunchFlow] Step 2  Addressables 初始化完成");
                LaunchTelemetryHelper.EndPhaseMetric(step2, true);

                // ── Step 3: 检查服务器版本 ────────────────────────
                var step3 = LaunchTelemetryHelper.BeginPhaseMetric(runMetric, LaunchPhase.ServerVersionCheck, "step03_server_version_check");
                string updateUrl = GetUpdateServerUrl();
                HotUpdate.UpdateInfo serverVersion = null;

                if (string.IsNullOrEmpty(updateUrl))
                {
                    loading.SetStatus(Language.GetOrDefault("#1_launch_no_update_server", "未配置更新服务器，跳过版本检查"));
                    loading.SetProgress(0.2f);
                    GameLog.Log("[LaunchFlow] Step 3  未配置 UpdateServerUrl，跳过");
                }
                else
                {
                    loading.SetStatus(Language.GetOrDefault("#1_launch_checking_update", "正在检查更新..."));
                    loading.SetProgress(0.2f);
                    serverVersion = await GameEntry.HotUpdate.CheckUpdateAsync(updateUrl);

                    if (serverVersion == null)
                    {
                        if (!AllowLaunchWhenUpdateCheckFails())
                            throw new InvalidOperationException("更新清单获取、验签或安全准入失败，当前环境禁止使用本地版本继续启动。");
                        GameLog.Warning("[LaunchFlow] Step 3  获取服务器版本失败，配置允许降级为本地版本启动。");
                    }
                    else
                        GameLog.Log($"[LaunchFlow] Step 3  Server: App={serverVersion.AppVersion} " +
                                  $"Resource={serverVersion.ResourceVersion} Type={serverVersion.Type}");
                }
                // 灰度放量闸门：version.json 携带 GrayPercent 且本机未命中分桶时，按"无更新"继续
                // （version.json 已经过验签，灰度字段可信；放量上调后本机自动纳入）。
                bool grayMiss = false;
                if (serverVersion != null &&
                    !HotUpdate.VersionManager.IsDeviceInGrayRollout(serverVersion, SystemInfo.deviceUniqueIdentifier))
                {
                    GameLog.Log($"[LaunchFlow] Step 3  灰度放量 {serverVersion.GrayPercent}% 未命中本机，按无更新继续");
                    grayMiss = true;
                    serverVersion = null;
                }

                LaunchTelemetryHelper.EndPhaseMetric(step3, true, serverVersion == null
                    ? (grayMiss ? "gray_miss=true" : "server_version=null")
                    : $"server_app={serverVersion.AppVersion},server_res={serverVersion.ResourceVersion},type={serverVersion.Type},gray={serverVersion.GrayPercent}");

                // ── Step 4: 资源热更 ──────────────────────────────
                // 整包更新是不可绕过的启动硬闸门。在目标 AppVersion 尚未安装前，禁止修改 Catalog、配置、代码槽或持久化内容状态。
                var fullUpdateGate = LaunchTelemetryHelper.BeginPhaseMetric(
                    runMetric, LaunchPhase.FullUpdateGate, "step04_full_update_gate");
                if (LaunchFlowUpdateExecutor.ExecuteFullUpdateGate(loading, serverVersion))
                {
                    LaunchTelemetryHelper.EndPhaseMetric(fullUpdateGate, true, "full_update_required=true");
                    LaunchTelemetryHelper.FinalizeRunMetric(runMetric, true, TelemetryErrorCodes.Launch.FullUpdateGateBlocked);
                    return LaunchFlowOutcome.BlockedOnForceUpdate;
                }
                LaunchTelemetryHelper.EndPhaseMetric(fullUpdateGate, true, "full_update_required=false");

                // 事务边界声明（内容发行事务）：代码槽、Addressables Catalog、配置数据库共享同一个
                // 确认边界。任何内容安装动作之前先 BeginPending（内含 Catalog 缓存快照 + Pending 落盘），
                // 统一确认点在 Step9 HotfixEntry.Start 成功之后；确认前任何失败（含进程被杀）都会在
                // 本进程（AbortPendingContent）或下次启动（Step1.5 恢复）回滚全部三类内容。
                bool resourceUpdated = LaunchFlowUpdateExecutor.ShouldUpdateResources(serverVersion, localVersion);
                bool codeWillUpdate = LaunchFlowUpdateExecutor.ShouldUpdateCode(serverVersion, localVersion);
                if ((resourceUpdated || codeWillUpdate) && serverVersion != null)
                {
                    // ReleaseId 统一身份：复用已签名清单的 ManifestId（发布侧即 ReleaseContext.ReleaseId），
                    // 代码、资源、配置与服务端发布台账共享同一发行身份。
                    if (!GameEntry.HotUpdate.ContentRelease.BeginPending(new HotUpdate.ContentReleaseRecord
                        {
                            ReleaseId = string.IsNullOrEmpty(serverVersion.ManifestId)
                                ? $"local-{serverVersion.AppVersion}-r{serverVersion.ResourceVersion}-c{serverVersion.CodeVersion}"
                                : serverVersion.ManifestId,
                            AppVersion = Application.version,
                            ResourceVersion = serverVersion.ResourceVersion,
                            CodeVersion = serverVersion.CodeVersion,
                            MinCompatibleVersion = serverVersion.MinCompatibleVersion ?? string.Empty,
                            ResourceChanged = resourceUpdated,
                            CodeChanged = codeWillUpdate,
                        }))
                    {
                        // 快照或落盘失败即失败关闭：没有回滚能力就不允许开始安装。
                        GameLog.Error("[LaunchFlow] Step 4  内容发行事务开启失败，中止本次更新");
                        LaunchTelemetryHelper.FinalizeRunMetric(runMetric, false, TelemetryErrorCodes.Launch.CatalogUpdateFailed);
                        return LaunchFlowOutcome.Failed;
                    }
                }

                var step4 = LaunchTelemetryHelper.BeginPhaseMetric(runMetric, LaunchPhase.ResourceUpdate, "step04_resource_update");
                // serverVersion 已过验签 + 字段级准入（CheckUpdateAsync），其 ResourceCatalog 身份可信；
                // 传给资源更新执行器，供应用远端 Catalog 前验签（ADR-009）。
                var resourceUpdateResult = await LaunchFlowUpdateExecutor.ExecuteResourceUpdateAsync(
                    loading, resourceUpdated, cancellationToken, serverVersion?.ResourceCatalog);
                if (!resourceUpdateResult.Success)
                {
                    // 失败关闭：Catalog 失败 / 尺寸查询失败 / 下载失败都中止启动，绝不提交 ResourceVersion。
                    // 按失败阶段区分遥测终态码，避免"检查失败"与"下载失败"在告警侧混成一类。
                    string failureCode = resourceUpdateResult.ErrorCode switch
                    {
                        LaunchFlowUpdateExecutor.ResourceUpdateStageErrors.CatalogFailed =>
                            TelemetryErrorCodes.Launch.CatalogUpdateFailed,
                        LaunchFlowUpdateExecutor.ResourceUpdateStageErrors.SizeQueryFailed =>
                            TelemetryErrorCodes.Launch.DownloadSizeQueryFailed,
                        _ => TelemetryErrorCodes.Launch.ResourceDownloadFailed,
                    };
                    AbortPendingContent($"resource_update_failed:{resourceUpdateResult.ErrorCode}");
                    LaunchTelemetryHelper.EndPhaseMetric(step4, false,
                        $"{resourceUpdateResult.ErrorCode}:{resourceUpdateResult.Message}");
                    LaunchTelemetryHelper.FinalizeRunMetric(runMetric, false, failureCode);
                    return LaunchFlowOutcome.Failed;
                }

                // 资源热更成功后写回 ResourceVersion，避免下次启动重复检查/下载。
                LaunchTelemetryHelper.EndPhaseMetric(step4, true,
                    resourceUpdateResult.ResourceUpdated ? "resource_updated=true" : "resource_updated=false");

                // ── Step 5: 代码热更 ──────────────────────────────
                var step5 = LaunchTelemetryHelper.BeginPhaseMetric(runMetric, LaunchPhase.CodeUpdate, "step05_code_update");
                bool codeUpdated = await LaunchFlowUpdateExecutor.ExecuteCodeUpdateAsync(
                    loading, serverVersion, localVersion, updateUrl, cancellationToken);
                if (LaunchFlowUpdateExecutor.ShouldUpdateCode(serverVersion, localVersion) && !codeUpdated)
                {
                    AbortPendingContent("code_download_failed");
                    LaunchTelemetryHelper.EndPhaseMetric(step5, false, "download_failed");
                    LaunchTelemetryHelper.FinalizeRunMetric(runMetric, false, TelemetryErrorCodes.Launch.CodeDownloadFailed);
                    return LaunchFlowOutcome.Failed;
                }
                LaunchTelemetryHelper.EndPhaseMetric(step5, true, codeUpdated ? "code_updated=true" : "code_updated=false");

                // ── Step 6: 整包更新检测 ──────────────────────────
                var step6b = LaunchTelemetryHelper.BeginPhaseMetric(runMetric, LaunchPhase.ConfigPrepare, "step06b_config_prepare");
                bool configReady = await LaunchFlowUpdateExecutor.ExecuteConfigPrepareAsync(loading);
                LaunchTelemetryHelper.EndPhaseMetric(step6b, true, $"config_ready={configReady}");

                // ── Step 6c: 应用热更配置数据库 ───────────────────
                var step6c = LaunchTelemetryHelper.BeginPhaseMetric(runMetric, LaunchPhase.ConfigApply, "step06c_config_apply");
                var configApplyResult = await LaunchFlowUpdateExecutor.ExecuteConfigApplyAsync(
                    loading,
                    resourceUpdateResult.ResourceUpdated);
                if (!configApplyResult.Success)
                {
                    // 失败关闭：配置安装失败不能被当成"没有配置更新"继续提交版本。
                    // 同时回滚整个待确认发行（代码槽 + Catalog 快照 + 配置备份），
                    // 避免"新代码 + 旧配置"或"新 Catalog + 旧配置"错配组合在下次启动生效。
                    AbortPendingContent("config_apply_failed");
                    LaunchTelemetryHelper.EndPhaseMetric(step6c, false, configApplyResult.Message);
                    LaunchTelemetryHelper.FinalizeRunMetric(runMetric, false, TelemetryErrorCodes.Launch.ConfigApplyFailed);
                    return LaunchFlowOutcome.Failed;
                }
                if (configApplyResult.Applied)
                    LaunchTelemetryHelper.EndPhaseMetric(step6c, true, $"resource_updated=true,config_updated={configApplyResult.Updated}");
                else
                    LaunchTelemetryHelper.EndPhaseMetric(step6c, true, "resource_updated=false,skip_apply");

                // ── 热更总开关：无业务热更程序集的项目（纯框架壳/单机）跳过 Step 7-9，直接进入登录 ──
                if (!IsHotUpdateEnabled())
                {
                    loading.SetStatus(Language.GetOrDefault("#1_launch_entering_game", "正在进入游戏..."));
                    loading.SetProgress(0.95f);
                    GameLog.Log("[LaunchFlow] 热更已关闭（AppConfig.EnableHotUpdate=false），跳过 AOT 元数据 / 热更程序集 / StartHotfix");
                    // 纯框架模式的统一确认点：无 Hotfix 入口即视为启动就绪，内容事务在此确认。
                    // 同样用 BeginCommit/EndCommit 包裹，确保确认阶段中断可前滚补完（无代码槽，仅内容/配置/version）。
                    GameEntry.HotUpdate.ContentRelease.BeginCommit();
                    GameEntry.HotUpdate.ContentRelease.ConfirmPending();
                    GameEntry.RefData.ConfirmHotUpdateDatabase();
                    HotUpdate.VersionManager.CommitHotUpdate(
                        serverVersion,
                        resourceUpdateResult.ResourceUpdated,
                        codeUpdated: false);
                    GameEntry.HotUpdate.ContentRelease.EndCommit();
                    await loading.HideAsync();
                    GameLog.Log("[LaunchFlow] ========== 启动流程完成（纯框架模式）==========");
                    LaunchTelemetryHelper.FinalizeRunMetric(runMetric, true, TelemetryErrorCodes.Launch.Ok);
                    return LaunchFlowOutcome.ReadyForLogin;
                }

                // ── Step 7: 加载 AOT 元数据（须在热更 DLL 之前）────
                var step7 = LaunchTelemetryHelper.BeginPhaseMetric(runMetric, LaunchPhase.MetadataLoad, "step07_metadata_load");
                loading.SetStatus(Language.GetOrDefault("#1_launch_loading_metadata", "正在加载运行时元数据..."));
                loading.SetProgress(0.85f);
                bool metadataOk = await GameEntry.HotUpdate.LoadMetadataAsync();
                if (!metadataOk)
                {
                    LaunchTelemetryHelper.EndPhaseMetric(step7, false, "metadata_load_failed");
                    AbortPendingContent("metadata_load_failed");
                    LaunchTelemetryHelper.FinalizeRunMetric(runMetric, false, TelemetryErrorCodes.Launch.MetadataLoadFailed);
                    return LaunchFlowOutcome.Failed;
                }
                GameLog.Log("[LaunchFlow] Step 7  AOT 元数据加载完成");
                LaunchTelemetryHelper.EndPhaseMetric(step7, true);

                // ── Step 8: 加载热更程序集 ─────────────────────────
                var step8 = LaunchTelemetryHelper.BeginPhaseMetric(runMetric, LaunchPhase.HotUpdateAssemblyLoad, "step08_hotupdate_assembly_load");
                loading.SetStatus(Language.GetOrDefault("#1_launch_loading_assembly", "正在加载游戏数据..."));
                loading.SetProgress(0.9f);
                bool assemblyOk = await GameEntry.HotUpdate.LoadHotUpdateAssemblyAsync();
                if (!assemblyOk)
                {
                    LaunchTelemetryHelper.EndPhaseMetric(step8, false, "assembly_load_failed");
                    AbortPendingContent("assembly_load_failed");
                    LaunchTelemetryHelper.FinalizeRunMetric(runMetric, false, TelemetryErrorCodes.Launch.HotUpdateAssemblyLoadFailed);
                    return LaunchFlowOutcome.Failed;
                }
                GameLog.Log("[LaunchFlow] Step 8  HybridCLR 热更程序集加载完成");
                LaunchTelemetryHelper.EndPhaseMetric(step8, true);

                // ── Step 9: 启动热更逻辑，淡出 Loading ───────────
                var step9 = LaunchTelemetryHelper.BeginPhaseMetric(runMetric, LaunchPhase.HotfixStart, "step09_hotfix_start");
                loading.SetStatus(Language.GetOrDefault("#1_launch_entering_game", "正在进入游戏..."));
                loading.SetProgress(0.95f);
                if (!GameEntry.HotUpdate.StartHotfix())
                {
                    AbortPendingContent("hotfix_start_returned_false");
                    LaunchTelemetryHelper.EndPhaseMetric(step9, false, "hotfix_start_failed");
                    return LaunchFlowOutcome.Failed;
                }
                // ── 统一启动确认点：只有走到这里，本次发行的全部内容才被承认 ──────
                // 崩溃原子性：四类内容分属四个独立持久化文件，无法一次原子提交。BeginCommit 先落盘
                // 提交日志（回滚↔前滚的原子开关），四步确认之后 EndCommit 清除日志。进程若在任意两步之间
                // 被杀，下次启动检测到提交日志即“前滚补完”而非回滚（HotUpdateManager 前滚代码槽、
                // Step1.5 前滚内容/配置/version），杜绝“新代码 + 旧 Catalog/配置”错配。
                // 确认顺序（进程内任一步抛异常仍走 catch 的 AbortPendingContent 整体回滚）：
                //   1. 代码槽 Pending → LKG（HotUpdateSlotManager）；
                //   2. 内容事务 Pending → Active + LKG，丢弃 Catalog 快照（当前缓存成为新 LKG Catalog）；
                //   3. 配置数据库备份清理（新库正式生效）；
                //   4. version.json 提交（事实源：槽清单代码版本 + 本次确认的资源版本）。
                GameEntry.HotUpdate.ContentRelease.BeginCommit();
                GameEntry.HotUpdate.ConfirmPendingUpdate();
                GameEntry.HotUpdate.ContentRelease.ConfirmPending();
                GameEntry.RefData.ConfirmHotUpdateDatabase();
                HotUpdate.VersionManager.CommitHotUpdate(
                    serverVersion,
                    resourceUpdateResult.ResourceUpdated,
                    codeUpdated);
                GameEntry.HotUpdate.ContentRelease.EndCommit();
                GameLog.Log("[LaunchFlow] Step 9  游戏逻辑启动完成");
                LaunchTelemetryHelper.EndPhaseMetric(step9, true);

                await loading.HideAsync();
                GameLog.Log("[LaunchFlow] ========== 启动流程完成 ==========");
                LaunchTelemetryHelper.FinalizeRunMetric(runMetric, true, TelemetryErrorCodes.Launch.Ok);
                return LaunchFlowOutcome.ReadyForLogin;
            }
            catch (Exception ex)
            {
                AbortPendingContent($"unhandled_exception:{ex.Message}");
                GameLog.Error($"[LaunchFlow] 启动流程异常: {ex.Message}\n{ex.StackTrace}");
                LaunchTelemetryHelper.FinalizeRunMetric(runMetric, false, TelemetryErrorCodes.Launch.UnhandledException);
                return LaunchFlowOutcome.Failed;
            }
            }
        }

        /// <summary>
        /// 统一失败回滚：把本次待确认发行的全部内容标记失败并尽力恢复。
        /// <para>
        /// 回滚顺序与语义：
        ///   1. 代码槽 Pending 标记失败并回滚指针（本进程已加载的程序集无法卸载，调用方必须中止启动）；
        ///   2. 内容事务 Pending 清除并恢复 Catalog 缓存快照（本进程已激活的 Catalog 同样无法卸载，
        ///      文件恢复对下一次进程启动生效）；
        ///   3. 配置数据库恢复上一份已确认版本（.bak 消费）。
        /// 每一步各自失败安全：恢复失败时保留恢复凭据（快照/备份），下次启动 Step1.5 兜底重试。
        /// </para>
        /// </summary>
        private static void AbortPendingContent(string reason)
        {
            // BeginCommit 已经把事务决策持久化为“必须前滚”。此后任何确认步骤异常都不得再执行
            // 代码槽、Catalog 和配置回滚，否则会留下“提交日志要求前滚、实际内容已回滚”的矛盾状态。
            // 保留提交日志和所有恢复材料，本次启动失败退出；重试或下次启动由 Step1.5 幂等补完。
            if (GameEntry.HotUpdate?.ContentRelease?.IsCommitInProgress == true)
            {
                GameLog.Error($"[LaunchFlow] 确认提交阶段异常（{reason}），已保留提交日志等待前滚重放，禁止回滚待确认内容。");
                return;
            }

            try { GameEntry.HotUpdate?.MarkPendingUpdateFailed(reason); }
            catch (Exception ex) { GameLog.Error($"[LaunchFlow] 代码槽回滚异常：{ex.Message}"); }

            try { GameEntry.HotUpdate?.ContentRelease.MarkPendingFailed(reason); }
            catch (Exception ex) { GameLog.Error($"[LaunchFlow] 内容事务回滚异常：{ex.Message}"); }

            try { GameEntry.RefData?.RestoreLastConfirmedDatabaseIfAny(); }
            catch (Exception ex) { GameLog.Error($"[LaunchFlow] 配置恢复异常：{ex.Message}"); }
        }

        // ── 工具方法 ──────────────────────────────────────────────────────────

        /// <summary>
        /// 获取热更服务器根 URL（经 <see cref="HotUpdate.UpdateSecurity"/> 准入校验）。
        /// 读取顺序：
        ///   1. Resources/AppConfig.asset → UpdateServerUrl 字段
        ///   2. PlayerPrefs "UpdateServerUrl" —— 仅 Editor / Development Build 生效。
        ///      正式包封死该覆盖口：热更 DLL 是远程代码执行通道，release 包若允许 PlayerPrefs
        ///      重定向更新服务器，等于把补丁来源交给任何能改本地存储的攻击者。
        ///   3. 返回空字符串（跳过热更检查）
        /// 准入规则：prod 环境强制 HTTPS，违规 URL 拒绝使用（记 Error 并跳过热更，
        /// 避免"配置错误 → 明文拉 DLL"静默发生；配置错误应在发布前被构建校验拦截）。
        /// </summary>
        private static string GetUpdateServerUrl()
        {
            var config = AppConfig.Load();
            string url = config != null ? config.UpdateServerUrl : string.Empty;

#if UNITY_EDITOR || DEVELOPMENT_BUILD
            if (string.IsNullOrEmpty(url))
                url = PlayerPrefs.GetString("UpdateServerUrl", string.Empty);
#endif

            if (string.IsNullOrEmpty(url))
                return string.Empty;

            string appEnv = config != null ? config.AppEnv : string.Empty;
            if (!HotUpdate.UpdateSecurity.ValidateUpdateServerUrl(url, appEnv, out string reason))
            {
                throw new InvalidOperationException($"更新服务器 URL 未通过安全准入：{reason}");
            }

            return url;
        }

        /// <summary>
        /// 是否启用热更（AOT 元数据 / HybridCLR 程序集加载 / StartHotfix）。
        /// 读 AppConfig.EnableHotUpdate；无配置时默认启用（保持既有项目行为不变）。
        /// 无热更业务程序集的项目须置 false，否则 Step 8 加载热更 DLL 失败会卡在重试循环。
        /// </summary>
        /// <summary>
        /// 更新检查失败时是否允许降级启动。默认失败关闭；只有明确配置的离线/开发场景才允许继续。
        /// </summary>
        private static bool AllowLaunchWhenUpdateCheckFails()
        {
            AppConfigAsset config = AppConfig.Load();
            return config != null && config.AllowLaunchWhenUpdateCheckFails;
        }

        private static bool IsHotUpdateEnabled()
        {
            var config = AppConfig.Load();
            return config == null || config.EnableHotUpdate;
        }

    }
}
