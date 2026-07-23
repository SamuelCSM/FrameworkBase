using System;
using System.Text;
using System.Threading;
using Cysharp.Threading.Tasks;
using Framework.Core;
using Framework.Diagnostics;
using Framework.Foundation;
using UnityEngine;

namespace Framework
{
    /// <summary>引导模块访问点（ADR-008）。由 <see cref="GuideModule"/> 启动时赋值，未安装引导模块时为 null。</summary>
    public static class Guides
    {
        /// <summary>配置驱动的全局引导运行器。</summary>
        public static GuideRunner Runner { get; internal set; }
    }

    /// <summary>
    /// 中间层引导模块（ADR-008）：持有 <see cref="GuideRunner"/> 与挖孔表现，注册引导表现 Action，
    /// 编排 Catalog 冻结后初始化引导目录并开始监听。全局 Rule/Trigger/Action 目录的构建与冻结由 L3 的编排
    /// 装配根负责（见 <see cref="GameEntry.OnFreezeOrchestration"/>），引导挖孔 Action 的 Payload 工厂也由
    /// L3 的引导 Bootstrap 向它贡献；本模块只注册 Executor，并消费已冻结的编排服务与自己的 GuideCatalog。
    /// </summary>
    public sealed class GuideModule : FrameworkModuleBase
    {
        /// <summary>引导私有目录提供者（L3 从 ConfigData 构建，延迟到 StartAsync 求值）。</summary>
        private readonly Func<GuideCatalog> _guideCatalogProvider;
        /// <summary>配置引导运行器；StartAsync 创建并初始化，Dispose 释放。</summary>
        private GuideRunner _runner;
        /// <summary>挖孔遮罩表现服务；RegisterCapabilities 创建，供引导表现 Action 与遮罩兜底使用。</summary>
        private GuidePresentationService _presentation;

        public GuideModule(Func<GuideCatalog> guideCatalogProvider)
        {
            _guideCatalogProvider = guideCatalogProvider
                ?? throw new ArgumentNullException(nameof(guideCatalogProvider));
        }

        /// <summary>Phase 1：引导表现依赖 L1 UI 能力；表现 Action 的 executor 必须在编排 Catalog 冻结前注册。</summary>
        public override void RegisterCapabilities()
        {
            // 动作级兜底时限：内置动作（开关窗口/挖孔/延迟）都应在秒级返回，
            // 卡住多半是执行器等了一个不会到来的资源。引导步骤的动作是串行 await 的，不设限会拖住整条链。
            if (GameEntry.Actions.DefaultTimeout <= TimeSpan.Zero)
                GameEntry.Actions.DefaultTimeout = TimeSpan.FromSeconds(30);

            _presentation = new GuidePresentationService(GameEntry.UI, GameEntry.UI.Targets);
            GameEntry.Actions.Register(
                GuideOrchestrationTypeIds.FocusTargetAction,
                new GuideFocusTargetAction(_presentation));
            GameEntry.Actions.Register(
                GuideOrchestrationTypeIds.ClearFocusAction,
                new GuideClearFocusAction(_presentation));
        }

        /// <summary>Phase 2：编排冻结后初始化引导运行器、接线诊断与遮罩兜底、开始监听。</summary>
        public override UniTask StartAsync()
        {
            _runner = new GuideRunner(
                GameEntry.Rules, GameEntry.Triggers, GameEntry.Actions,
                new PrefsGuideRuntimeProgressStore())
            {
                ObserverErrorSink = ex =>
                {
                    Debug.LogError("[Guide] 引导编排回调/执行器异常（已隔离）");
                    if (ex != null) Debug.LogException(ex);
                },
            };
            _runner.Initialize(_guideCatalogProvider());
            AttachDiagnostics(_runner);
            // 表现清理由配置的 GuideClearFocus Action 负责；此处叠加防御兜底，任意结束路径都清挖孔遮罩。
            _runner.GuideCompleted += _ => _presentation?.Clear();
            _runner.GuideCancelled += (_, __) => _presentation?.Clear();
            _runner.GuideFailed += (_, __) => _presentation?.Clear();
            _runner.StartListening();
            Guides.Runner = _runner;
            RegisterDebugCommands();
            Debug.Log($"[Guide] 引导模块已启动，Guide={_runner.Catalog.Guides.Length}，Step={_runner.Catalog.Steps.Length}。");
            return UniTask.CompletedTask;
        }

        /// <summary>
        /// 注册引导调试命令。幂等：已注册则跳过。
        /// <para>
        /// 命令直接操作本模块持有的 <see cref="GuideRunner"/>——即运行器实际读写的那份进度存档。
        /// 早期版本这里操作的是另一套设备级存档，命令会报"已清"但运行器根本读不到，
        /// 排障时比没有命令更误导；此后新增操作一律经运行器公开接口，不得再旁路直连存储。
        /// </para>
        /// </summary>
        private void RegisterDebugCommands()
        {
            CommandRegistry registry = GameEntry.Commands;
            if (registry == null || registry.TryGet("guide", out _)) return;
            registry.Register(
                new CommandInfo("guide",
                    "引导调试：list 列全部引导，status/reset/skip 查改进度，cancel 取消当前，start 立即触发",
                    usage: "guide <list|status|reset|skip|start|cancel> [引导id|Key]",
                    requiredAccess: CommandAccessLevel.Privileged),
                args =>
                {
                    GuideRunner runner = _runner;
                    if (runner == null || !runner.IsInitialized)
                        return CommandResult.Ok("引导目录未初始化。");

                    string op = (args.GetStringOrDefault(0) ?? "list").ToLowerInvariant();
                    if (op == "list") return ListGuides(runner);
                    if (op == "cancel")
                    {
                        if (!runner.IsRunning) return CommandResult.Ok("当前没有引导在运行。");
                        int running = runner.CurrentGuideId;
                        runner.CancelAsync("gm_cancel").Forget();
                        return CommandResult.Ok($"已请求取消运行中的引导 {running}。");
                    }

                    // 其余操作都要定位到具体引导：允许数字 Id，也允许配置 Key。
                    string target = args.GetString(1);
                    if (!int.TryParse(target, out int guideId) && !runner.TryResolveId(target, out guideId))
                        return CommandResult.Fail($"引导 ID/Key 不存在：{target}");

                    switch (op)
                    {
                        case "status":
                        {
                            GuideProgress progress = runner.GetProgress(guideId);
                            string state = progress.IsCompleted
                                ? "已完成"
                                : progress.CurrentStepId > 0
                                    ? $"断点步骤 {progress.CurrentStepId}（未完成）"
                                    : "未开始";
                            string live = runner.CurrentGuideId == guideId
                                ? $"；正在运行，当前步骤 {runner.CurrentStepId}"
                                : string.Empty;
                            return CommandResult.Ok($"引导 {guideId}：{state}{live}");
                        }
                        case "reset":
                            // 运行中的引导不能直接清档（会与会话状态打架），提示先 cancel。
                            if (runner.CurrentGuideId == guideId)
                                return CommandResult.Fail("该引导正在运行，请先 guide cancel。");
                            runner.ResetProgress(guideId);
                            return CommandResult.Ok($"引导 {guideId} 进度已清（重新满足条件即从头重播）。");
                        case "skip":
                            if (runner.CurrentGuideId == guideId)
                                return CommandResult.Fail("该引导正在运行，请先 guide cancel。");
                            runner.MarkCompleted(guideId);
                            return CommandResult.Ok($"引导 {guideId} 已标记完成（Once 类型不再触发）。");
                        case "start":
                            runner.TryStartAsync(guideId).Forget();
                            return CommandResult.Ok($"已请求启动引导 {guideId}（结果见 GUIDE_* 日志）。");
                        default:
                            throw new CommandArgumentException(
                                $"未知操作 '{op}'，应为 list/status/reset/skip/start/cancel。");
                    }
                });
        }

        /// <summary>列出全部引导及其进度，标出正在运行的那条。</summary>
        private static CommandResult ListGuides(GuideRunner runner)
        {
            var text = new StringBuilder(256);
            text.Append("引导清单（Id [Key] 步骤数 进度）：");
            GuideDefinition[] guides = runner.Catalog.Guides;
            for (int i = 0; i < guides.Length; i++)
            {
                GuideDefinition definition = guides[i];
                GuideProgress progress = runner.GetProgress(definition.Id);
                text.AppendLine().Append("  ").Append(definition.Id)
                    .Append(" [").Append(definition.Key).Append("] ")
                    .Append(CountSteps(runner.Catalog, definition.Id)).Append(" 步 ")
                    .Append(progress.IsCompleted
                        ? "已完成"
                        : progress.CurrentStepId > 0 ? $"断点@{progress.CurrentStepId}" : "未开始");
                if (runner.CurrentGuideId == definition.Id) text.Append("  ← 运行中");
            }
            if (guides.Length == 0) text.AppendLine().Append("  （空）");
            return CommandResult.Ok(text.ToString());
        }

        /// <summary>统计某条引导的步骤数（目录是扁平表，此处按 GuideId 计数）。</summary>
        private static int CountSteps(GuideCatalog catalog, int guideId)
        {
            int count = 0;
            for (int i = 0; i < catalog.Steps.Length; i++)
                if (catalog.Steps[i].GuideId == guideId) count++;
            return count;
        }

        /// <summary>释放引导运行器与挖孔表现，并清空访问点。</summary>
        public override void Dispose()
        {
            // 只在访问点仍指向本模块的运行器时才清空：另一实例已接管时不得把它的运行器抹掉。
            if (ReferenceEquals(Guides.Runner, _runner)) Guides.Runner = null;
            _runner?.Dispose();
            _presentation?.Dispose();
            _runner = null;
            _presentation = null;
        }

        private static void AttachDiagnostics(GuideRunner runner)
        {
            runner.GuideCompleted += guideId => Debug.Log($"[Guide] GUIDE_COMPLETED id={guideId}");
            runner.GuideCancelled += (guideId, reason) =>
                Debug.LogWarning($"[Guide] GUIDE_CANCELLED id={guideId} reason={reason}");
            runner.GuideFailed += (guideId, reason) =>
                Debug.LogError($"[Guide] GUIDE_FAILED id={guideId} reason={reason}");
            // 超时单独打点：它几乎总是配置问题（CompleteTrigger 配错或目标被挡），
            // 与运行期异常混在一起会被淹没。线上按此条报警即可定位到具体步骤。
            runner.StepTimedOut += (guideId, stepId) =>
                Debug.LogError($"[Guide] GUIDE_STEP_TIMEOUT id={guideId} step={stepId}");
        }

        private sealed class GuideFocusTargetAction : IActionExecutor<GuideFocusTargetActionPayload>
        {
            private readonly GuidePresentationService _presentation;
            public GuideFocusTargetAction(GuidePresentationService presentation) => _presentation = presentation;

            public UniTask<ActionExecutionResult> ExecuteAsync(
                GuideFocusTargetActionPayload payload,
                ActionContext context,
                CancellationToken cancellationToken)
            {
                cancellationToken.ThrowIfCancellationRequested();
                return UniTask.FromResult(
                    _presentation.TryFocus(payload.TargetId, context.Scope, payload.Padding, payload.DimAlpha)
                        ? ActionExecutionResult.Succeeded()
                        : ActionExecutionResult.Failed(
                            $"TargetId={payload.TargetId} 当前不存在或 Scope 不匹配。"));
            }
        }

        private sealed class GuideClearFocusAction : IActionExecutor<GuideClearFocusActionPayload>
        {
            private readonly GuidePresentationService _presentation;
            public GuideClearFocusAction(GuidePresentationService presentation) => _presentation = presentation;

            public UniTask<ActionExecutionResult> ExecuteAsync(
                GuideClearFocusActionPayload payload,
                ActionContext context,
                CancellationToken cancellationToken)
            {
                cancellationToken.ThrowIfCancellationRequested();
                _presentation.Clear();
                return UniTask.FromResult(ActionExecutionResult.Succeeded());
            }
        }
    }
}
