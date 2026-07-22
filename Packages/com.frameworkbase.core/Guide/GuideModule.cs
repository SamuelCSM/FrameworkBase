using System;
using System.Threading;
using Cysharp.Threading.Tasks;
using Framework.Core;
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
    /// 编排 Catalog 冻结后初始化引导目录并开始监听。全局 Rule/Trigger/Action 目录的构建与冻结由 L3 提供
    /// （见 <see cref="GameEntry.OnFreezeOrchestration"/>）；本模块只消费已冻结的编排服务与自己的 GuideCatalog。
    /// </summary>
    public sealed class GuideModule : FrameworkModuleBase
    {
        private readonly Func<GuideCatalog> _guideCatalogProvider;
        private GuideRunner _runner;
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
                BuiltinOrchestrationTypeIds.Actions.GuideFocusTarget,
                new GuideFocusTargetAction(_presentation));
            GameEntry.Actions.Register(
                BuiltinOrchestrationTypeIds.Actions.GuideClearFocus,
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
            Debug.Log($"[Guide] 引导模块已启动，Guide={_runner.Catalog.Guides.Length}，Step={_runner.Catalog.Steps.Length}。");
            return UniTask.CompletedTask;
        }

        public override void Dispose()
        {
            Guides.Runner = null;
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
