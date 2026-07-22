using System;
using System.Threading;
using Cysharp.Threading.Tasks;
using Framework.Foundation;

namespace Framework
{
    /// <summary>框架内置编排能力的稳定 TypeId；具体配置实例仍使用各自 Rule/Trigger/Action Id。</summary>
    public static class BuiltinOrchestrationTypeIds
    {
        public static class Rules
        {
            public const int UIWindowIsOpen = 1001;
            public const int UITargetExists = 1002;
        }

        public static class Triggers
        {
            public const int UIWindowLifecycle = 2001;
            public const int UITargetClicked = 2002;
            public const int Delay = 2003;
        }

        public static class Actions
        {
            public const int UIOpenWindow = 3001;
            public const int UICloseWindow = 3002;
            public const int Delay = 3003;
            public const int GuideFocusTarget = 3004;
            public const int GuideClearFocus = 3005;
        }
    }

    [Serializable]
    public sealed class UIWindowRulePayload
    {
        public int WindowId;
    }

    [Serializable]
    public sealed class UITargetRulePayload
    {
        public int TargetId;
    }

    [Serializable]
    public sealed class UIWindowTriggerPayload
    {
        public int WindowId;
        public UIWindowPhase Phase;
    }

    [Serializable]
    public sealed class UITargetClickTriggerPayload
    {
        public int TargetId;
    }

    [Serializable]
    public sealed class DelayPayload
    {
        public int Milliseconds;
        public bool IgnoreTimeScale;
    }

    [Serializable]
    public sealed class UIOpenWindowActionPayload
    {
        public int WindowId;
        public bool UsePool = true;
    }

    [Serializable]
    public sealed class UICloseWindowActionPayload
    {
        public int WindowId;
        public bool Destroy;
    }

    [Serializable]
    public sealed class GuideFocusTargetActionPayload
    {
        public int TargetId;
        public float Padding = 8f;
        public float DimAlpha = 0.6f;
    }

    [Serializable]
    public sealed class GuideClearFocusActionPayload
    {
    }

    /// <summary>把只依赖框架 UI/时间能力的内置积木注册到通用服务；必须早于 Catalog.Initialize。</summary>
    public static class UIOrchestrationBuiltins
    {
        public static void Register(
            RuleService rules,
            TriggerService triggers,
            ActionService actions,
            UIManager ui,
            UITargetRegistry targets,
            GuidePresentationService guidePresentation)
        {
            if (rules == null) throw new ArgumentNullException(nameof(rules));
            if (triggers == null) throw new ArgumentNullException(nameof(triggers));
            if (actions == null) throw new ArgumentNullException(nameof(actions));
            if (ui == null) throw new ArgumentNullException(nameof(ui));
            if (targets == null) throw new ArgumentNullException(nameof(targets));
            if (guidePresentation == null) throw new ArgumentNullException(nameof(guidePresentation));

            rules.Register(BuiltinOrchestrationTypeIds.Rules.UIWindowIsOpen,
                new UIWindowIsOpenRule(ui));
            rules.Register(BuiltinOrchestrationTypeIds.Rules.UITargetExists,
                new UITargetExistsRule(targets));

            triggers.Register(BuiltinOrchestrationTypeIds.Triggers.UIWindowLifecycle,
                new UIWindowLifecycleTrigger(ui));
            triggers.Register(BuiltinOrchestrationTypeIds.Triggers.UITargetClicked,
                new UITargetClickedTrigger(targets));
            triggers.Register(BuiltinOrchestrationTypeIds.Triggers.Delay,
                new DelayTrigger());

            actions.Register(BuiltinOrchestrationTypeIds.Actions.UIOpenWindow,
                new UIOpenWindowAction(ui));
            actions.Register(BuiltinOrchestrationTypeIds.Actions.UICloseWindow,
                new UICloseWindowAction(ui));
            actions.Register(BuiltinOrchestrationTypeIds.Actions.Delay,
                new DelayAction());
            actions.Register(BuiltinOrchestrationTypeIds.Actions.GuideFocusTarget,
                new GuideFocusTargetAction(guidePresentation));
            actions.Register(BuiltinOrchestrationTypeIds.Actions.GuideClearFocus,
                new GuideClearFocusAction(guidePresentation));
        }

        private sealed class UIWindowIsOpenRule : IRuleEvaluator<UIWindowRulePayload>
        {
            private readonly UIManager _ui;
            public UIWindowIsOpenRule(UIManager ui) => _ui = ui;
            public RuleResult Evaluate(UIWindowRulePayload payload, RuleContext context)
                => _ui.IsUIOpened(payload.WindowId)
                    ? RuleResult.Passed()
                    : RuleResult.Failed($"WindowId={payload.WindowId} 尚未打开。");
        }

        private sealed class UITargetExistsRule : IRuleEvaluator<UITargetRulePayload>
        {
            private readonly UITargetRegistry _targets;
            public UITargetExistsRule(UITargetRegistry targets) => _targets = targets;
            public RuleResult Evaluate(UITargetRulePayload payload, RuleContext context)
                => _targets.TryResolve(payload.TargetId, context.Scope, out _)
                    || _targets.TryResolve(payload.TargetId, out _)
                    ? RuleResult.Passed()
                    : RuleResult.Failed($"TargetId={payload.TargetId} 当前不存在或实例不唯一。");
        }

        private sealed class UIWindowLifecycleTrigger : ITriggerBinder<UIWindowTriggerPayload>
        {
            private readonly UIManager _ui;
            public UIWindowLifecycleTrigger(UIManager ui) => _ui = ui;

            public IDisposable Bind(
                UIWindowTriggerPayload payload,
                TriggerContext context,
                Action<object> onTriggered)
            {
                return _ui.SubscribeWindowLifecycle(evt =>
                {
                    if (evt.WindowId != payload.WindowId || evt.Phase != payload.Phase) return;
                    if (context.Scope != null && !ReferenceEquals(context.Scope, evt.Root)) return;
                    onTriggered(evt);
                });
            }
        }

        private sealed class UITargetClickedTrigger : ITriggerBinder<UITargetClickTriggerPayload>
        {
            private readonly UITargetRegistry _targets;
            public UITargetClickedTrigger(UITargetRegistry targets) => _targets = targets;

            public IDisposable Bind(
                UITargetClickTriggerPayload payload,
                TriggerContext context,
                Action<object> onTriggered)
                => _targets.SubscribeClick(payload.TargetId, context.Scope, target => onTriggered(target));
        }

        private sealed class DelayTrigger : ITriggerBinder<DelayPayload>
        {
            private sealed class CancellationHandle : IDisposable
            {
                private CancellationTokenSource _source;

                public CancellationHandle(CancellationTokenSource source) => _source = source;

                public void Dispose()
                {
                    CancellationTokenSource source = _source;
                    _source = null;
                    if (source == null) return;
                    try { source.Cancel(); }
                    finally { source.Dispose(); }
                }
            }

            public IDisposable Bind(DelayPayload payload, TriggerContext context, Action<object> onTriggered)
            {
                if (payload.Milliseconds < 0) throw new ArgumentOutOfRangeException(nameof(payload.Milliseconds));
                var cancellation = CancellationTokenSource.CreateLinkedTokenSource(context.CancellationToken);
                FireAsync(payload, cancellation.Token, onTriggered).Forget();
                return new CancellationHandle(cancellation);
            }

            private static async UniTaskVoid FireAsync(
                DelayPayload payload,
                CancellationToken cancellationToken,
                Action<object> onTriggered)
            {
                try
                {
                    await UniTask.Delay(
                        payload.Milliseconds,
                        payload.IgnoreTimeScale,
                        PlayerLoopTiming.Update,
                        cancellationToken);
                    onTriggered(null);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
            }
        }

        private sealed class UIOpenWindowAction : IActionExecutor<UIOpenWindowActionPayload>
        {
            private readonly UIManager _ui;
            public UIOpenWindowAction(UIManager ui) => _ui = ui;

            public async UniTask<ActionExecutionResult> ExecuteAsync(
                UIOpenWindowActionPayload payload,
                ActionContext context,
                CancellationToken cancellationToken)
            {
                cancellationToken.ThrowIfCancellationRequested();
                UIBaseCore window = await _ui.OpenUIAsync(payload.WindowId, context.Data, payload.UsePool);
                return window != null
                    ? ActionExecutionResult.Succeeded()
                    : ActionExecutionResult.Failed($"打开 WindowId={payload.WindowId} 失败。");
            }
        }

        private sealed class UICloseWindowAction : IActionExecutor<UICloseWindowActionPayload>
        {
            private readonly UIManager _ui;
            public UICloseWindowAction(UIManager ui) => _ui = ui;

            public async UniTask<ActionExecutionResult> ExecuteAsync(
                UICloseWindowActionPayload payload,
                ActionContext context,
                CancellationToken cancellationToken)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!_ui.IsUIOpened(payload.WindowId))
                    return ActionExecutionResult.Skipped($"WindowId={payload.WindowId} 未打开。");
                await _ui.CloseUIAsync(payload.WindowId, payload.Destroy);
                return ActionExecutionResult.Succeeded();
            }
        }

        private sealed class DelayAction : IActionExecutor<DelayPayload>
        {
            public async UniTask<ActionExecutionResult> ExecuteAsync(
                DelayPayload payload,
                ActionContext context,
                CancellationToken cancellationToken)
            {
                if (payload.Milliseconds < 0) throw new ArgumentOutOfRangeException(nameof(payload.Milliseconds));
                await UniTask.Delay(
                    payload.Milliseconds,
                    payload.IgnoreTimeScale,
                    PlayerLoopTiming.Update,
                    cancellationToken);
                return ActionExecutionResult.Succeeded();
            }
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
