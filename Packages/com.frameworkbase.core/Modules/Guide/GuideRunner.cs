using System;
using System.Collections.Generic;
using System.Threading;
using Cysharp.Threading.Tasks;
using Framework.Foundation;

namespace Framework
{
    /// <summary>主动尝试启动引导的结果，区分“拒绝启动”“已排队”“已启动”。</summary>
    public enum GuideStartResult
    {
        /// <summary>已启动（可能因步骤同步完成而立即推进或结束）。</summary>
        Started = 0,
        /// <summary>前置条件不满足：StartRule 未通过，或 Once 引导已完成。</summary>
        Rejected = 1,
        /// <summary>已有引导在运行，按 Priority 暂存，当前引导结束后重新检查 StartRule。</summary>
        Queued = 2,
    }

    /// <summary>
    /// 配置引导运行器。常态只持有 Trigger 订阅，不做 Update 轮询；同一时刻只运行一条全局引导，
    /// 运行中到达的其它开始信号按 Priority 排队，在当前引导结束后重新检查 StartRule。
    /// </summary>
    public sealed class GuideRunner : IDisposable
    {
        /// <summary>进入步骤的结果：区分“已进入并等待完成”“已被同步推进/结束取代”“进入失败”。</summary>
        private enum StepEnterOutcome
        {
            Entered = 0,
            Superseded = 1,
            Failed = 2,
        }

        /// <summary>转换进行中到达的同步完成信号，转换结束后据此继续推进，避免被吞掉。</summary>
        private sealed class PendingCompletion
        {
            public Session Session;
            public int StepId;
            public object Data;
        }

        private sealed class Session
        {
            public GuideRuntimeGuide Guide;
            public int StepIndex;
            public object Scope;
            public object Data;
            public CancellationTokenSource Cancellation;
        }

        private sealed class PendingStart
        {
            public GuideRuntimeGuide Guide;
            public object Scope;
            public object Data;
        }

        private readonly RuleService _rules;
        private readonly TriggerService _triggers;
        private readonly ActionService _actions;
        private readonly IGuideRuntimeProgressStore _progress;
        /// <summary>GuideId → 运行时引导；由 GuideCatalogBinder 在 Initialize 时织好后整体接管。</summary>
        private Dictionary<int, GuideRuntimeGuide> _guides = new Dictionary<int, GuideRuntimeGuide>();
        private readonly List<IDisposable> _startSubscriptions = new List<IDisposable>();
        private readonly Dictionary<int, PendingStart> _pending = new Dictionary<int, PendingStart>();

        private Session _active;
        private IDisposable _stepSubscription;
        private PendingCompletion _pendingCompletion;
        private bool _starting;
        private bool _transitioning;
        private bool _listening;
        private bool _disposed;

        public GuideRunner(
            RuleService rules,
            TriggerService triggers,
            ActionService actions,
            IGuideRuntimeProgressStore progress)
        {
            _rules = rules ?? throw new ArgumentNullException(nameof(rules));
            _triggers = triggers ?? throw new ArgumentNullException(nameof(triggers));
            _actions = actions ?? throw new ArgumentNullException(nameof(actions));
            _progress = progress ?? throw new ArgumentNullException(nameof(progress));
        }

        public bool IsInitialized { get; private set; }
        public bool IsListening => _listening;
        public bool IsRunning => _active != null;
        public int CurrentGuideId => _active?.Guide.Definition.Id ?? 0;
        public int CurrentStepId => _active?.Guide.Steps[_active.StepIndex].Definition.StepId ?? 0;
        public GuideCatalog Catalog { get; private set; }
        public Action<Exception> ObserverErrorSink { get; set; }

        public event Action<int> GuideStarted;
        public event Action<int, int> StepEntered;
        public event Action<int> GuideCompleted;
        public event Action<int, string> GuideCancelled;
        public event Action<int, string> GuideFailed;

        public void Initialize(GuideCatalog catalog)
        {
            ThrowIfDisposed();
            if (IsInitialized) throw new InvalidOperationException("GuideRunner 已初始化，不能替换 Catalog。");
            if (catalog == null) throw new ArgumentNullException(nameof(catalog));
            if (!_rules.IsInitialized || !_triggers.IsInitialized || !_actions.IsInitialized)
                throw new InvalidOperationException("Rule/Trigger/Action Catalog 必须先于 GuideCatalog 初始化。");

            // 跨表校验与对象图织造交给绑定器；运行器只接管织好的结果，专注会话状态机。
            _guides = GuideCatalogBinder.Bind(catalog, _rules, _triggers, _actions);

            Catalog = catalog;
            IsInitialized = true;
        }

        /// <summary>绑定所有配置 StartTrigger。重复调用幂等。</summary>
        public void StartListening()
        {
            ThrowIfDisposed();
            EnsureInitialized();
            if (_listening) return;
            _listening = true;
            try
            {
                foreach (GuideRuntimeGuide guide in SortedGuides())
                {
                    if (guide.Definition.StartTriggerId <= 0) continue;
                    IDisposable subscription = _triggers.Bind(
                        guide.Definition.StartTriggerId,
                        new TriggerContext(this),
                        signal => HandleStartSignal(guide, signal).Forget());
                    _startSubscriptions.Add(subscription);
                }
            }
            catch
            {
                StopListening();
                throw;
            }
        }

        public void StopListening()
        {
            for (int i = _startSubscriptions.Count - 1; i >= 0; i--)
            {
                try { _startSubscriptions[i]?.Dispose(); }
                catch (Exception ex) { Report(ex); }
            }
            _startSubscriptions.Clear();
            _pending.Clear();
            _listening = false;
        }

        /// <summary>
        /// 业务/GM 主动尝试开始一条引导。
        /// <list type="bullet">
        /// <item><see cref="GuideStartResult.Started"/>：已启动（若步骤同步完成可能立即推进或结束）。</item>
        /// <item><see cref="GuideStartResult.Rejected"/>：StartRule 不通过，或 Once 引导已完成。</item>
        /// <item><see cref="GuideStartResult.Queued"/>：已有引导在运行，按 Priority 暂存，稍后重试。</item>
        /// </list>
        /// </summary>
        public async UniTask<GuideStartResult> TryStartAsync(int guideId, object scope = null, object data = null)
        {
            ThrowIfDisposed();
            EnsureInitialized();
            if (!_guides.TryGetValue(guideId, out GuideRuntimeGuide guide))
                throw new KeyNotFoundException($"Guide ID 不存在：{guideId}。");

            if (_active != null || _starting)
            {
                QueuePending(guide, scope, data);
                return GuideStartResult.Queued;
            }

            GuideProgress saved = _progress.Get(guideId);
            if (saved.IsCompleted && guide.Definition.RepeatMode == GuideRepeatMode.Once)
                return GuideStartResult.Rejected;

            if (guide.Definition.StartRuleId > 0)
            {
                RuleResult rule = _rules.Evaluate(
                    guide.Definition.StartRuleId,
                    new RuleContext(this, scope, data));
                if (!rule.IsPassed) return GuideStartResult.Rejected;
            }

            _starting = true;
            var session = new Session
            {
                Guide = guide,
                StepIndex = ResolveStepIndex(
                    guide,
                    saved.IsCompleted && guide.Definition.RepeatMode == GuideRepeatMode.Always
                        ? 0
                        : saved.CurrentStepId),
                Scope = scope,
                Data = data,
                Cancellation = new CancellationTokenSource(),
            };
            _active = session;
            Notify(GuideStarted, guideId);
            try
            {
                StepEnterOutcome outcome = await EnterCurrentStepAsync(session, data);
                if (outcome == StepEnterOutcome.Failed && ReferenceEquals(_active, session))
                    await FailSessionAsync(session, "进入首个步骤失败。");
                return GuideStartResult.Started;
            }
            finally
            {
                _starting = false;
                TryStartNextPendingAsync().Forget();
            }
        }

        /// <summary>取消当前引导，保留当前 StepId 断点，后续可从该步骤恢复。</summary>
        public async UniTask CancelAsync(string reason = "cancelled")
        {
            Session session = _active;
            if (session == null) return;
            await EndSessionAsync(
                session, completed: false, failed: false, runCancelActions: true, reason: reason);
        }

        /// <summary>跳过当前引导并标记完成，Once 引导不再自动触发。</summary>
        public async UniTask SkipAsync(string reason = "skipped")
        {
            Session session = _active;
            if (session == null) return;
            await EndSessionAsync(
                session, completed: true, failed: false, runCancelActions: true, reason: reason);
        }

        public void ResetProgress(int guideId)
        {
            EnsureInitialized();
            if (!_guides.ContainsKey(guideId))
                throw new KeyNotFoundException($"Guide ID 不存在：{guideId}。");
            if (_active?.Guide.Definition.Id == guideId)
                throw new InvalidOperationException("运行中的引导不能同步 Reset，请先 CancelAsync。");
            _progress.Clear(guideId);
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            StopListening();
            Session session = _active;
            _active = null;
            _pendingCompletion = null;
            DisposeStepSubscription();
            if (session != null)
            {
                try { session.Cancellation.Cancel(); } catch { }
                session.Cancellation.Dispose();
            }
        }

        private async UniTask HandleStartSignal(GuideRuntimeGuide guide, TriggerSignal signal)
        {
            object scope = ResolveScope(signal.Data);
            await TryStartAsync(guide.Definition.Id, scope, signal.Data);
        }

        private async UniTask<StepEnterOutcome> EnterCurrentStepAsync(Session session, object data)
        {
            if (!ReferenceEquals(_active, session)) return StepEnterOutcome.Superseded;
            GuideRuntimeStep step = session.Guide.Steps[session.StepIndex];
            _progress.SetCurrentStep(session.Guide.Definition.Id, step.Definition.StepId);

            if (!await ExecuteActionsAsync(session, step.EnterActions, data, session.Cancellation.Token))
                return ReferenceEquals(_active, session) && !session.Cancellation.IsCancellationRequested
                    ? StepEnterOutcome.Failed
                    : StepEnterOutcome.Superseded;
            if (!ReferenceEquals(_active, session) || session.Cancellation.IsCancellationRequested)
                return StepEnterOutcome.Superseded;

            Notify(StepEntered, session.Guide.Definition.Id, step.Definition.StepId);
            if (!ReferenceEquals(_active, session) || session.Cancellation.IsCancellationRequested)
                return StepEnterOutcome.Superseded;

            IDisposable binding;
            try
            {
                binding = _triggers.BindOnce(
                    step.Definition.CompleteTriggerId,
                    new TriggerContext(this, session.Scope, data, session.Cancellation.Token),
                    signal => CompleteCurrentStepAsync(
                        session, step.Definition.StepId, signal.Data).Forget());
            }
            catch (Exception ex)
            {
                Report(ex);
                return ReferenceEquals(_active, session) ? StepEnterOutcome.Failed : StepEnterOutcome.Superseded;
            }

            // BindOnce 可能同步发火，导致本步在返回前已被推进甚至整条结束。
            // 此时订阅已由 CompleteCurrentStepAsync 处理，本调用只需释放并报告“被取代”，不能当成失败。
            if (!ReferenceEquals(_active, session)
                || session.StepIndex >= session.Guide.Steps.Count
                || session.Guide.Steps[session.StepIndex].Definition.StepId != step.Definition.StepId)
            {
                binding.Dispose();
                return StepEnterOutcome.Superseded;
            }
            _stepSubscription = binding;
            return StepEnterOutcome.Entered;
        }

        private async UniTask CompleteCurrentStepAsync(Session session, int expectedStepId, object signalData)
        {
            if (!ReferenceEquals(_active, session)) return;
            GuideRuntimeStep current = session.Guide.Steps[session.StepIndex];
            if (current.Definition.StepId != expectedStepId) return;
            if (_transitioning)
            {
                // 完成信号在上一次转换尚未收尾时同步到达：记录下来，转换结束后再驱动，避免被丢弃。
                _pendingCompletion = new PendingCompletion
                {
                    Session = session, StepId = expectedStepId, Data = signalData,
                };
                return;
            }

            _transitioning = true;
            DisposeStepSubscription();
            try
            {
                if (!await ExecuteActionsAsync(
                        session, current.ExitActions, signalData, session.Cancellation.Token))
                {
                    if (ReferenceEquals(_active, session))
                        await FailSessionAsync(session, $"步骤 {expectedStepId} Exit Action 失败。");
                    return;
                }
                if (!ReferenceEquals(_active, session)) return;

                int next = session.StepIndex + 1;
                if (next >= session.Guide.Steps.Count)
                {
                    await EndSessionAsync(
                        session, completed: true, failed: false, runCancelActions: false, reason: null);
                    return;
                }

                session.StepIndex = next;
                if (await EnterCurrentStepAsync(session, signalData) == StepEnterOutcome.Failed
                    && ReferenceEquals(_active, session))
                    await FailSessionAsync(session, $"进入步骤 {session.Guide.Steps[next].Definition.StepId} 失败。");
            }
            finally
            {
                _transitioning = false;
                if (!DrainPendingCompletion())
                    TryStartNextPendingAsync().Forget();
            }
        }

        /// <summary>驱动转换期间暂存的同步完成信号；已驱动返回 true。</summary>
        private bool DrainPendingCompletion()
        {
            PendingCompletion pending = _pendingCompletion;
            _pendingCompletion = null;
            if (pending == null || !ReferenceEquals(_active, pending.Session)) return false;
            if (_active.StepIndex >= _active.Guide.Steps.Count
                || _active.Guide.Steps[_active.StepIndex].Definition.StepId != pending.StepId)
                return false;
            CompleteCurrentStepAsync(pending.Session, pending.StepId, pending.Data).Forget();
            return true;
        }

        private async UniTask<bool> ExecuteActionsAsync(
            Session session,
            List<GuideRuntimeAction> runtimeActions,
            object data,
            CancellationToken cancellationToken)
        {
            for (int i = 0; i < runtimeActions.Count; i++)
            {
                GuideStepActionDefinition definition = runtimeActions[i].Definition;
                ActionExecutionResult result = await _actions.ExecuteAsync(
                    definition.ActionId,
                    new ActionContext(this, session.Scope, data),
                    cancellationToken);
                if (result.IsSuccess) continue;
                if (definition.FailurePolicy == GuideActionFailurePolicy.Continue) continue;
                return false;
            }
            return true;
        }

        private UniTask FailSessionAsync(Session session, string reason)
            => EndSessionAsync(
                session, completed: false, failed: true, runCancelActions: true, reason: reason);

        private async UniTask EndSessionAsync(
            Session session,
            bool completed,
            bool failed,
            bool runCancelActions,
            string reason)
        {
            if (!ReferenceEquals(_active, session)) return;
            _active = null;
            DisposeStepSubscription();
            try { session.Cancellation.Cancel(); } catch (Exception ex) { Report(ex); }

            GuideRuntimeStep step = session.Guide.Steps[session.StepIndex];
            try
            {
                if (runCancelActions)
                    await ExecuteActionsAsync(session, step.CancelActions, session.Data, CancellationToken.None);
            }
            catch (Exception ex)
            {
                Report(ex);
            }
            finally
            {
                session.Cancellation.Dispose();
            }

            int guideId = session.Guide.Definition.Id;
            if (completed)
            {
                _progress.MarkCompleted(guideId);
                Notify(GuideCompleted, guideId);
            }
            else if (failed)
            {
                Notify(GuideFailed, guideId, reason ?? "failed");
            }
            else
            {
                Notify(GuideCancelled, guideId, reason ?? "cancelled");
            }

            if (!_transitioning && !_starting)
                TryStartNextPendingAsync().Forget();
        }

        private async UniTask TryStartNextPendingAsync()
        {
            if (_active != null || _starting || _pending.Count == 0) return;
            while (_pending.Count > 0 && _active == null)
            {
                PendingStart next = null;
                foreach (PendingStart candidate in _pending.Values)
                {
                    if (next == null
                        || candidate.Guide.Definition.Priority > next.Guide.Definition.Priority
                        || (candidate.Guide.Definition.Priority == next.Guide.Definition.Priority
                            && candidate.Guide.Definition.Id < next.Guide.Definition.Id))
                        next = candidate;
                }
                if (next == null) return;
                _pending.Remove(next.Guide.Definition.Id);
                await TryStartAsync(next.Guide.Definition.Id, next.Scope, next.Data);
            }
        }

        private void QueuePending(GuideRuntimeGuide guide, object scope, object data)
        {
            if (_active?.Guide.Definition.Id == guide.Definition.Id) return;
            _pending[guide.Definition.Id] = new PendingStart
            {
                Guide = guide,
                Scope = scope,
                Data = data,
            };
        }

        private IEnumerable<GuideRuntimeGuide> SortedGuides()
        {
            var list = new List<GuideRuntimeGuide>(_guides.Values);
            list.Sort((left, right) =>
            {
                int priority = right.Definition.Priority.CompareTo(left.Definition.Priority);
                return priority != 0 ? priority : left.Definition.Id.CompareTo(right.Definition.Id);
            });
            return list;
        }

        private static int ResolveStepIndex(GuideRuntimeGuide guide, int savedStepId)
            => savedStepId > 0 && guide.StepIndexById.TryGetValue(savedStepId, out int index) ? index : 0;

        private static object ResolveScope(object signalData)
        {
            if (signalData is UIWindowLifecycleEvent window) return window.Root;
            if (signalData is UITarget target) return target.Scope;
            return null;
        }

        private void DisposeStepSubscription()
        {
            IDisposable subscription = _stepSubscription;
            _stepSubscription = null;
            try { subscription?.Dispose(); }
            catch (Exception ex) { Report(ex); }
        }

        private void EnsureInitialized()
        {
            if (!IsInitialized) throw new InvalidOperationException("GuideRunner 尚未初始化。");
        }

        private void ThrowIfDisposed()
        {
            if (_disposed) throw new ObjectDisposedException(nameof(GuideRunner));
        }

        private void Notify(Action<int> handler, int guideId)
        {
            try { handler?.Invoke(guideId); } catch (Exception ex) { Report(ex); }
        }

        private void Notify(Action<int, int> handler, int guideId, int stepId)
        {
            try { handler?.Invoke(guideId, stepId); } catch (Exception ex) { Report(ex); }
        }

        private void Notify(Action<int, string> handler, int guideId, string reason)
        {
            try { handler?.Invoke(guideId, reason); } catch (Exception ex) { Report(ex); }
        }

        private void Report(Exception error)
        {
            try { ObserverErrorSink?.Invoke(error); } catch { }
        }
    }
}
