using System;
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
            _presentation = new GuidePresentationService(GameEntry.UI, GameEntry.UiTargets);
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

        /// <summary>注册引导断点调试命令（操作旧 GuideFlow 设备级存档）。幂等：已注册则跳过。</summary>
        private static void RegisterDebugCommands()
        {
            CommandRegistry registry = GameEntry.Commands;
            if (registry == null || registry.TryGet("guide", out _)) return;
            registry.Register(
                new CommandInfo("guide", "引导断点调试：查看/重置/跳过某条引导的设备级进度",
                    usage: "guide <status|reset|skip> <引导id>",
                    requiredAccess: CommandAccessLevel.Privileged),
                args =>
                {
                    string op = args.GetString(0);
                    string guideId = args.GetString(1);
                    IGuideProgressStore store = new PrefsGuideProgressStore();
                    switch (op.ToLowerInvariant())
                    {
                        case "status":
                            if (store.IsCompleted(guideId))
                                return CommandResult.Ok($"引导 '{guideId}'：已完成");
                            string stepId = store.GetStepId(guideId);
                            return CommandResult.Ok(string.IsNullOrEmpty(stepId)
                                ? $"引导 '{guideId}'：未开始"
                                : $"引导 '{guideId}'：断点步骤 '{stepId}'（未完成）");
                        case "reset":
                            store.Clear(guideId);
                            return CommandResult.Ok($"引导 '{guideId}' 进度已清（重进流程即从头重播）。");
                        case "skip":
                            store.MarkCompleted(guideId);
                            return CommandResult.Ok($"引导 '{guideId}' 已标记完成（不再弹出；运行中的实例不受影响）。");
                        default:
                            throw new CommandArgumentException($"未知操作 '{op}'，应为 status/reset/skip。");
                    }
                });
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
