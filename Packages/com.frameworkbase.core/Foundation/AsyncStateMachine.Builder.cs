using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using Cysharp.Threading.Tasks;

namespace Framework.Foundation
{
    public sealed partial class AsyncStateMachine<TState, TTrigger>
    {
        /// <summary>
        /// 拓扑构建器：仅在 <see cref="Build"/> 的 configure 回调内可用，回调返回后完成校验并冻结。
        /// </summary>
        public sealed class Builder
        {
            internal sealed class StateDraft
            {
                public readonly Dictionary<TTrigger, List<TransitionRule>> Rules =
                    new Dictionary<TTrigger, List<TransitionRule>>();
                public readonly List<StateHandler> EnterHandlers = new List<StateHandler>();
                public readonly List<StateHandler> ExitHandlers = new List<StateHandler>();
                public readonly List<RollbackHandler> RollbackHandlers = new List<RollbackHandler>();
            }

            private readonly Dictionary<TState, StateDraft> _drafts = new Dictionary<TState, StateDraft>();
            private TimeSpan _defaultTransitionTimeout = Timeout.InfiniteTimeSpan;
            private TimeSpan _rollbackTimeout = TimeSpan.FromSeconds(5);
            private int _maxHistoryRecords = 128;
            private int _maxChainedTransitions = 64;

            internal Builder() { }

            /// <summary>机器级同状态转换默认策略，默认 Ignore；单条规则可覆盖。</summary>
            public SameStateTransitionBehavior SameStateBehavior { get; set; } = SameStateTransitionBehavior.Ignore;

            /// <summary>默认转换超时（约束 Exit/Enter），默认不限时；单条规则可覆盖。</summary>
            public TimeSpan DefaultTransitionTimeout
            {
                get => _defaultTransitionTimeout;
                set => _defaultTransitionTimeout = ValidateTimeout(value, nameof(DefaultTransitionTimeout));
            }

            /// <summary>补偿与恢复的独立超时，默认 5 秒。</summary>
            public TimeSpan RollbackTimeout
            {
                get => _rollbackTimeout;
                set => _rollbackTimeout = ValidateTimeout(value, nameof(RollbackTimeout));
            }

            /// <summary>诊断历史上限，默认 128，至少为 1。</summary>
            public int MaxHistoryRecords
            {
                get => _maxHistoryRecords;
                set => _maxHistoryRecords = value >= 1
                    ? value
                    : throw new ArgumentOutOfRangeException(nameof(MaxHistoryRecords), "至少为 1。");
            }

            /// <summary>单次外层触发允许的链式转换上限，默认 64，至少为 1。</summary>
            public int MaxChainedTransitions
            {
                get => _maxChainedTransitions;
                set => _maxChainedTransitions = value >= 1
                    ? value
                    : throw new ArgumentOutOfRangeException(nameof(MaxChainedTransitions), "至少为 1。");
            }

            /// <summary>观察者（TransitionRecorded/StateChanged 订阅者）异常的诊断出口；为 null 时静默隔离。</summary>
            public Action<Exception> ObserverErrorSink { get; set; }

            /// <summary>声明一个状态并返回其配置器。重复声明抛异常——拓扑应一处声明完毕，禁止分散追加。</summary>
            public StateBuilder State(TState state)
            {
                if (_drafts.ContainsKey(state))
                    throw new InvalidOperationException($"状态 {state} 已声明；拓扑应在一处声明完毕，禁止分散追加。");
                var draft = new StateDraft();
                _drafts.Add(state, draft);
                return new StateBuilder(this, state, draft);
            }

            internal AsyncStateMachine<TState, TTrigger> Build(TState initialState)
            {
                if (!_drafts.ContainsKey(initialState))
                    throw new InvalidOperationException($"初始状态 {initialState} 未声明。");

                // 拓扑校验：所有外部转换目标必须已声明，杜绝"拼错状态到运行期才炸"。
                foreach (KeyValuePair<TState, StateDraft> pair in _drafts)
                {
                    foreach (KeyValuePair<TTrigger, List<TransitionRule>> ruleGroup in pair.Value.Rules)
                    {
                        foreach (TransitionRule rule in ruleGroup.Value)
                        {
                            if (!rule.IsInternal && !_drafts.ContainsKey(rule.Target))
                            {
                                throw new InvalidOperationException(
                                    $"状态 {pair.Key} 经触发器 {ruleGroup.Key} 指向未声明状态 {rule.Target}。");
                            }
                        }
                    }
                }

                var topology = new Dictionary<TState, StateNode>(_drafts.Count);
                foreach (KeyValuePair<TState, StateDraft> pair in _drafts)
                {
                    var node = new StateNode
                    {
                        EnterHandlers = pair.Value.EnterHandlers.Count > 0
                            ? pair.Value.EnterHandlers.ToArray() : EmptyHandlers,
                        ExitHandlers = pair.Value.ExitHandlers.Count > 0
                            ? pair.Value.ExitHandlers.ToArray() : EmptyHandlers,
                        RollbackHandlers = pair.Value.RollbackHandlers.Count > 0
                            ? pair.Value.RollbackHandlers.ToArray() : EmptyRollbacks,
                    };
                    if (pair.Value.Rules.Count > 0)
                    {
                        node.Rules = new Dictionary<TTrigger, TransitionRule[]>(pair.Value.Rules.Count);
                        foreach (KeyValuePair<TTrigger, List<TransitionRule>> ruleGroup in pair.Value.Rules)
                            node.Rules.Add(ruleGroup.Key, ruleGroup.Value.ToArray());
                    }
                    topology.Add(pair.Key, node);
                }

                return new AsyncStateMachine<TState, TTrigger>(
                    initialState,
                    topology,
                    SameStateBehavior,
                    _defaultTransitionTimeout,
                    _rollbackTimeout,
                    _maxHistoryRecords,
                    _maxChainedTransitions,
                    ObserverErrorSink);
            }

            private static TimeSpan ValidateTimeout(TimeSpan value, string parameterName)
            {
                if (value == Timeout.InfiniteTimeSpan) return value;
                if (value <= TimeSpan.Zero)
                    throw new ArgumentOutOfRangeException(parameterName, "必须大于 0 或为 Timeout.InfiniteTimeSpan。");
                return value;
            }

            /// <summary>单个状态的配置器：转换规则、生命周期处理器与显式补偿。</summary>
            public sealed class StateBuilder
            {
                private readonly Builder _owner;
                private readonly TState _state;
                private readonly StateDraft _draft;

                internal StateBuilder(Builder owner, TState state, StateDraft draft)
                {
                    _owner = owner;
                    _state = state;
                    _draft = draft;
                }

                /// <summary>
                /// 声明转换规则（同步守卫）。同一触发器可声明多条守卫规则，按声明顺序取首个通过者；
                /// 无守卫规则必然命中，其后不得再声明同触发器规则（不可达）。
                /// </summary>
                public StateBuilder Permit(
                    TTrigger trigger,
                    TState target,
                    Func<StateTransitionContext<TState, TTrigger>, bool> guard = null,
                    TimeSpan? timeout = null,
                    SameStateTransitionBehavior? sameStateBehavior = null)
                {
                    StateGuard asyncGuard = guard == null
                        ? (StateGuard)null
                        : (context, _) => UniTask.FromResult(guard(context));
                    return AddRule(trigger, target, asyncGuard, timeout, sameStateBehavior);
                }

                /// <summary>声明转换规则（异步守卫）。守卫应保持轻量且无副作用；不受转换超时约束。</summary>
                public StateBuilder PermitAsync(
                    TTrigger trigger,
                    TState target,
                    Func<StateTransitionContext<TState, TTrigger>, CancellationToken, UniTask<bool>> guard,
                    TimeSpan? timeout = null,
                    SameStateTransitionBehavior? sameStateBehavior = null)
                {
                    if (guard == null) throw new ArgumentNullException(nameof(guard));
                    return AddRule(trigger, target, guard.Invoke, timeout, sameStateBehavior);
                }

                /// <summary>
                /// 声明内部转换：命中后只执行 <paramref name="handler"/>，不执行 Exit/Enter、不改变状态
                /// （典型如刷新类事件）。处理器自持一致性，失败无补偿。
                /// </summary>
                public StateBuilder PermitInternal(
                    TTrigger trigger,
                    Action<StateTransitionContext<TState, TTrigger>> handler,
                    Func<StateTransitionContext<TState, TTrigger>, bool> guard = null,
                    TimeSpan? timeout = null)
                {
                    if (handler == null) throw new ArgumentNullException(nameof(handler));
                    return PermitInternalAsync(
                        trigger,
                        (context, _) =>
                        {
                            handler(context);
                            return UniTask.CompletedTask;
                        },
                        guard,
                        timeout);
                }

                /// <summary>声明内部转换（异步处理器）。</summary>
                public StateBuilder PermitInternalAsync(
                    TTrigger trigger,
                    Func<StateTransitionContext<TState, TTrigger>, CancellationToken, UniTask> handler,
                    Func<StateTransitionContext<TState, TTrigger>, bool> guard = null,
                    TimeSpan? timeout = null)
                {
                    if (handler == null) throw new ArgumentNullException(nameof(handler));
                    StateGuard asyncGuard = guard == null
                        ? (StateGuard)null
                        : (context, _) => UniTask.FromResult(guard(context));
                    var rule = new TransitionRule
                    {
                        IsInternal = true,
                        Guard = asyncGuard,
                        InternalHandlers = new StateHandler[] { handler.Invoke },
                        Timeout = timeout.HasValue
                            ? ValidateTimeout(timeout.Value, nameof(timeout))
                            : (TimeSpan?)null,
                    };
                    AppendRule(trigger, rule);
                    return this;
                }

                /// <summary>进入本状态时执行（含 Reenter 与 RecoverAsync，经 context 区分）。</summary>
                public StateBuilder OnEnter(Action<StateTransitionContext<TState, TTrigger>> handler)
                {
                    if (handler == null) throw new ArgumentNullException(nameof(handler));
                    return OnEnterAsync((context, _) =>
                    {
                        handler(context);
                        return UniTask.CompletedTask;
                    });
                }

                /// <summary>进入本状态时执行（异步）。</summary>
                public StateBuilder OnEnterAsync(
                    Func<StateTransitionContext<TState, TTrigger>, CancellationToken, UniTask> handler)
                {
                    if (handler == null) throw new ArgumentNullException(nameof(handler));
                    _draft.EnterHandlers.Add(handler.Invoke);
                    return this;
                }

                /// <summary>离开本状态时执行。</summary>
                public StateBuilder OnExit(Action<StateTransitionContext<TState, TTrigger>> handler)
                {
                    if (handler == null) throw new ArgumentNullException(nameof(handler));
                    return OnExitAsync((context, _) =>
                    {
                        handler(context);
                        return UniTask.CompletedTask;
                    });
                }

                /// <summary>离开本状态时执行（异步）。</summary>
                public StateBuilder OnExitAsync(
                    Func<StateTransitionContext<TState, TTrigger>, CancellationToken, UniTask> handler)
                {
                    if (handler == null) throw new ArgumentNullException(nameof(handler));
                    _draft.ExitHandlers.Add(handler.Invoke);
                    return this;
                }

                /// <summary>
                /// 显式补偿处理器。转换失败时：本状态作为目标（Enter 已开始）执行清理、作为源（Exit 已开始）
                /// 执行恢复，阶段经 StateRollbackContext.Phase 区分。
                /// 未配置补偿的状态一旦需要补偿，机器直接 Faulted（fail-closed）——配置本方法即是对
                /// "此状态可安全回滚"的显式承诺。
                /// </summary>
                public StateBuilder OnRollback(Action<StateRollbackContext<TState, TTrigger>> handler)
                {
                    if (handler == null) throw new ArgumentNullException(nameof(handler));
                    return OnRollbackAsync((context, _) =>
                    {
                        handler(context);
                        return UniTask.CompletedTask;
                    });
                }

                /// <summary>显式补偿处理器（异步）。</summary>
                public StateBuilder OnRollbackAsync(
                    Func<StateRollbackContext<TState, TTrigger>, CancellationToken, UniTask> handler)
                {
                    if (handler == null) throw new ArgumentNullException(nameof(handler));
                    _draft.RollbackHandlers.Add(handler.Invoke);
                    return this;
                }

                private StateBuilder AddRule(
                    TTrigger trigger,
                    TState target,
                    StateGuard guard,
                    TimeSpan? timeout,
                    SameStateTransitionBehavior? sameStateBehavior)
                {
                    var rule = new TransitionRule
                    {
                        Target = target,
                        Guard = guard,
                        Timeout = timeout.HasValue
                            ? ValidateTimeout(timeout.Value, nameof(timeout))
                            : (TimeSpan?)null,
                        SameStateBehavior = sameStateBehavior,
                    };
                    AppendRule(trigger, rule);
                    return this;
                }

                private void AppendRule(TTrigger trigger, TransitionRule rule)
                {
                    if (!_draft.Rules.TryGetValue(trigger, out List<TransitionRule> rules))
                    {
                        rules = new List<TransitionRule>();
                        _draft.Rules.Add(trigger, rules);
                    }
                    if (rules.Count > 0 && rules[rules.Count - 1].Guard == null)
                    {
                        throw new InvalidOperationException(
                            $"状态 {_state} 的触发器 {trigger} 已存在无守卫规则，其后的规则不可达。");
                    }
                    rules.Add(rule);
                }
            }
        }
    }
}
