using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using Cysharp.Threading.Tasks;

namespace Framework.Foundation
{
    /// <summary>状态机整机状态。</summary>
    public enum StateMachineStatus
    {
        /// <summary>空闲，可接受触发。</summary>
        Ready,
        /// <summary>转换执行中（Exit/Enter/补偿尚未收敛）。</summary>
        Transitioning,
        /// <summary>失败且无法证明已恢复到一致状态；后续触发一律拒绝，必须显式 RecoverAsync。</summary>
        Faulted,
        /// <summary>已释放，任何操作抛 <see cref="ObjectDisposedException"/>。</summary>
        Disposed,
    }

    /// <summary>同状态转换（Source == Target）的处理策略。</summary>
    public enum SameStateTransitionBehavior
    {
        /// <summary>静默忽略：不执行守卫与任何生命周期处理器，结果为 <see cref="StateTransitionOutcome.IgnoredSameState"/>。</summary>
        Ignore,
        /// <summary>显式拒绝：结果为 <see cref="StateTransitionOutcome.RejectedSameState"/>，便于调用方发现逻辑错误。</summary>
        Reject,
        /// <summary>重进入：完整执行守卫、Exit、Enter，等价于一次真实转换。</summary>
        Reenter,
    }

    /// <summary>单次触发尝试的最终结果。所有尝试（含拒绝、忽略、入队、丢弃）都会形成历史记录。</summary>
    public enum StateTransitionOutcome
    {
        /// <summary>转换成功提交，CurrentState 已切换。</summary>
        Succeeded,
        /// <summary>当前状态没有为该触发器声明任何规则。</summary>
        NoTransitionFound,
        /// <summary>存在规则但所有候选守卫都未通过。</summary>
        GuardRejected,
        /// <summary>同状态触发且策略为 Ignore（守卫不会执行）。</summary>
        IgnoredSameState,
        /// <summary>同状态触发且策略为 Reject。</summary>
        RejectedSameState,
        /// <summary>处理器内重入触发，已入队；将在当前转换收敛后按序执行，本记录仅代表入队动作，真实执行结果经历史/事件观察。</summary>
        Enqueued,
        /// <summary>入队的触发未执行即被丢弃（外层转换异常退出、机器 Faulted/Disposed、或链式转换超限）。</summary>
        Dropped,
        /// <summary>调用方取消；若已执行部分生命周期，补偿已成功回到源状态。</summary>
        Cancelled,
        /// <summary>转换超时；补偿已成功回到源状态。</summary>
        TimedOut,
        /// <summary>守卫或处理器抛出异常；补偿已成功回到源状态（或尚无副作用无需补偿）。</summary>
        Failed,
        /// <summary>失败且无法证明已恢复（缺少 OnRollback 或补偿本身失败），机器进入 Faulted。</summary>
        Faulted,
        /// <summary>RecoverAsync 成功，机器回到 Ready。</summary>
        RecoverySucceeded,
        /// <summary>RecoverAsync 失败，机器保持 Faulted。</summary>
        RecoveryFailed,
    }

    /// <summary>补偿阶段：先清理部分进入的目标状态，再恢复已退出的源状态。</summary>
    public enum RollbackPhase
    {
        /// <summary>清理目标状态（其 Enter 已开始执行但转换失败）。</summary>
        CleanupTarget,
        /// <summary>恢复源状态（其 Exit 已开始执行但转换失败）。</summary>
        RestoreSource,
    }

    /// <summary>处理器内单次触发引发的链式转换数量超过上限，通常意味着状态间的死循环互跳；机器进入 Faulted。</summary>
    public sealed class StateMachineChainLimitException : InvalidOperationException
    {
        internal StateMachineChainLimitException(int limit)
            : base($"链式转换数量超过上限 {limit}，疑似状态间死循环；机器已进入 Faulted，请检查处理器内的触发逻辑。")
        {
        }
    }

    /// <summary>失败后需要补偿的状态未配置 OnRollback。按 fail-closed 原则机器进入 Faulted，而不是假装已恢复。</summary>
    public sealed class StateMachineCompensationException : InvalidOperationException
    {
        internal StateMachineCompensationException(string message)
            : base(message)
        {
        }
    }

    /// <summary>一次转换尝试的不可变上下文，传入守卫与生命周期处理器。</summary>
    public sealed class StateTransitionContext<TState, TTrigger>
    {
        internal StateTransitionContext(
            long sequence,
            TState source,
            TState target,
            TTrigger trigger,
            bool hasTrigger,
            bool isReentry,
            bool isInternal,
            bool isRecovery)
        {
            Sequence = sequence;
            Source = source;
            Target = target;
            Trigger = trigger;
            HasTrigger = hasTrigger;
            IsReentry = isReentry;
            IsInternal = isInternal;
            IsRecovery = isRecovery;
        }

        /// <summary>全机单调递增序号，用于日志关联。</summary>
        public long Sequence { get; }
        /// <summary>转换起点状态。</summary>
        public TState Source { get; }
        /// <summary>转换目标状态（内部转换时等于 Source）。</summary>
        public TState Target { get; }
        /// <summary>引发本次转换的触发器；仅当 <see cref="HasTrigger"/> 为 true 时有意义（恢复流程没有触发器，勿把 default 误读成真实枚举值）。</summary>
        public TTrigger Trigger { get; }
        /// <summary>本次转换是否由触发器引发（RecoverAsync 为 false）。</summary>
        public bool HasTrigger { get; }
        /// <summary>是否为同状态重进入（策略 Reenter）。</summary>
        public bool IsReentry { get; }
        /// <summary>是否为内部转换（不执行 Exit/Enter、不改变状态）。</summary>
        public bool IsInternal { get; }
        /// <summary>是否为 RecoverAsync 发起的恢复流程。</summary>
        public bool IsRecovery { get; }
    }

    /// <summary>补偿处理器的上下文：携带失败的原始转换、当前补偿阶段与失败原因。</summary>
    public sealed class StateRollbackContext<TState, TTrigger>
    {
        internal StateRollbackContext(
            StateTransitionContext<TState, TTrigger> failedTransition,
            RollbackPhase phase,
            Exception error)
        {
            FailedTransition = failedTransition;
            Phase = phase;
            Error = error;
        }

        /// <summary>失败的原始转换上下文。</summary>
        public StateTransitionContext<TState, TTrigger> FailedTransition { get; }
        /// <summary>当前补偿阶段（先 CleanupTarget 后 RestoreSource）。</summary>
        public RollbackPhase Phase { get; }
        /// <summary>导致补偿的原始异常（超时/取消时为 OperationCanceledException）。</summary>
        public Exception Error { get; }
    }

    /// <summary>一次触发尝试的完整审计记录，进入有界历史并通过 TransitionRecorded 事件发布。</summary>
    public sealed class StateTransitionRecord<TState, TTrigger>
    {
        internal StateTransitionRecord(
            StateTransitionContext<TState, TTrigger> context,
            StateTransitionOutcome outcome,
            TimeSpan duration,
            Exception error,
            StateMachineStatus machineStatus)
        {
            Sequence = context.Sequence;
            Source = context.Source;
            Target = context.Target;
            Trigger = context.Trigger;
            HasTrigger = context.HasTrigger;
            IsReentry = context.IsReentry;
            IsInternal = context.IsInternal;
            IsRecovery = context.IsRecovery;
            Outcome = outcome;
            Duration = duration;
            Error = error;
            MachineStatus = machineStatus;
        }

        /// <summary>全机单调递增序号。</summary>
        public long Sequence { get; }
        /// <summary>转换起点状态。</summary>
        public TState Source { get; }
        /// <summary>转换目标状态。</summary>
        public TState Target { get; }
        /// <summary>触发器；仅 <see cref="HasTrigger"/> 为 true 时有意义。</summary>
        public TTrigger Trigger { get; }
        /// <summary>是否由触发器引发（恢复记录为 false）。</summary>
        public bool HasTrigger { get; }
        /// <summary>是否同状态重进入。</summary>
        public bool IsReentry { get; }
        /// <summary>是否内部转换。</summary>
        public bool IsInternal { get; }
        /// <summary>是否恢复流程。</summary>
        public bool IsRecovery { get; }
        /// <summary>最终结果。</summary>
        public StateTransitionOutcome Outcome { get; }
        /// <summary>本次尝试耗时。</summary>
        public TimeSpan Duration { get; }
        /// <summary>失败原因；补偿也失败时为 AggregateException（首个为原始异常，其余为补偿异常）。</summary>
        public Exception Error { get; }
        /// <summary>记录落笔时的机器状态。</summary>
        public StateMachineStatus MachineStatus { get; }
        /// <summary>是否成功（转换提交或恢复成功）。</summary>
        public bool Succeeded => Outcome == StateTransitionOutcome.Succeeded ||
                                 Outcome == StateTransitionOutcome.RecoverySucceeded;
    }

    /// <summary>
    /// 强类型、串行、可取消的异步状态机。拓扑经 <see cref="Build"/> 一次性构建并校验，运行期完全不可变。
    /// <para>
    /// <b>事务化提交</b>：Source.Exit 与 Target.Enter 全部成功后 CurrentState 才切到 Target。
    /// 失败时执行显式补偿（<c>OnRollback</c>）：先清理部分进入的 Target，再恢复已退出的 Source。
    /// <b>Fail-closed</b>：需要补偿的状态未配置 OnRollback、或补偿本身失败，机器进入 Faulted，
    /// 后续触发一律拒绝，直到 <see cref="RecoverAsync"/> 成功。
    /// </para>
    /// <para>
    /// <b>串行与重入</b>：并发 FireAsync 由单一转换门串行。处理器内（同一异步流）再次 FireAsync 不会死锁
    /// 也不会抛异常，而是<b>入队</b>：当前转换提交后按序执行（调用立即返回
    /// <see cref="StateTransitionOutcome.Enqueued"/> 记录，真实执行结果经历史/事件观察）。
    /// 外层转换异常退出或机器故障时，队列中的触发以 <see cref="StateTransitionOutcome.Dropped"/> 丢弃；
    /// 链式转换超过 <see cref="MaxChainedTransitions"/> 判定为死循环并 Faulted。
    /// 注意：处理器内切换线程（丢失异步流）后同步等待对同一机器的触发仍会死锁，属使用错误。
    /// </para>
    /// <para>
    /// <b>取消与超时</b>：调用方取消向外抛 OperationCanceledException（补偿仍在独立的
    /// <see cref="RollbackTimeout"/> 下完成后才抛出）；超时返回 <see cref="StateTransitionOutcome.TimedOut"/>。
    /// 守卫受调用方取消与生命周期取消约束，但不受转换超时约束——守卫应保持轻量、无副作用。
    /// </para>
    /// <para>
    /// <b>诊断</b>：每次尝试形成 <see cref="StateTransitionRecord{TState,TTrigger}"/> 进入有界历史并发布
    /// <see cref="TransitionRecorded"/>。观察者异常被隔离，可经 Builder 的 ObserverErrorSink 上报。
    /// </para>
    /// <para>注意：构建时不执行初始状态的 Enter；如需初始化副作用请在构建后自行触发一次转换。</para>
    /// </summary>
    public sealed class AsyncStateMachine<TState, TTrigger> : IDisposable
    {
        internal delegate UniTask StateHandler(
            StateTransitionContext<TState, TTrigger> context,
            CancellationToken cancellationToken);

        internal delegate UniTask<bool> StateGuard(
            StateTransitionContext<TState, TTrigger> context,
            CancellationToken cancellationToken);

        internal delegate UniTask RollbackHandler(
            StateRollbackContext<TState, TTrigger> context,
            CancellationToken cancellationToken);

        private static readonly StateHandler[] EmptyHandlers = Array.Empty<StateHandler>();
        private static readonly RollbackHandler[] EmptyRollbacks = Array.Empty<RollbackHandler>();

        /// <summary>一条转换规则。External 规则有 Target；Internal 规则只有处理器、不改状态。</summary>
        internal sealed class TransitionRule
        {
            public bool IsInternal;
            public TState Target;
            public StateGuard Guard;
            public StateHandler[] InternalHandlers = EmptyHandlers;
            public TimeSpan? Timeout;
            public SameStateTransitionBehavior? SameStateBehavior;
        }

        /// <summary>冻结后的状态节点：规则与处理器全部为不可变数组，运行期无锁读取。</summary>
        internal sealed class StateNode
        {
            public Dictionary<TTrigger, TransitionRule[]> Rules;
            public StateHandler[] EnterHandlers = EmptyHandlers;
            public StateHandler[] ExitHandlers = EmptyHandlers;
            public RollbackHandler[] RollbackHandlers = EmptyRollbacks;
        }

        /// <summary>处理器内重入触发的排队项。</summary>
        private readonly struct PendingFire
        {
            public PendingFire(TTrigger trigger, CancellationToken token)
            {
                Trigger = trigger;
                Token = token;
            }

            public TTrigger Trigger { get; }
            public CancellationToken Token { get; }
        }

        private readonly Dictionary<TState, StateNode> _topology;
        private readonly SameStateTransitionBehavior _sameStateBehavior;
        private readonly TimeSpan _defaultTransitionTimeout;
        private readonly TimeSpan _rollbackTimeout;
        private readonly int _maxHistoryRecords;
        private readonly int _maxChainedTransitions;
        private readonly Action<Exception> _observerErrorSink;

        private readonly object _stateSync = new object();
        private readonly object _historySync = new object();
        private readonly object _queueSync = new object();
        private readonly Queue<StateTransitionRecord<TState, TTrigger>> _history =
            new Queue<StateTransitionRecord<TState, TTrigger>>();
        private readonly Queue<PendingFire> _pending = new Queue<PendingFire>();
        private readonly SemaphoreSlim _transitionGate = new SemaphoreSlim(1, 1);
        private readonly CancellationTokenSource _lifetimeCts = new CancellationTokenSource();
        private readonly CancellationToken _lifetimeToken;

        // 重入识别：每次持门执行都生成唯一 marker 并写入 AsyncLocal。
        // 处理器内（同一异步流）再调 FireAsync 时 marker 与活动 marker 相同 → 入队而非等门（等门必死锁）。
        // 处理器 spawn 出去、外层转换已结束后才执行的调用，活动 marker 已清空 → 正常走门，不会误判；
        // 历史遗留的陈旧 marker 与新一轮的活动 marker 必然不同（每轮新建对象），同样无害。
        private readonly AsyncLocal<object> _executionMarker = new AsyncLocal<object>();
        private object _activeMarker;          // _queueSync 保护
        private bool _drainActive;             // _queueSync 保护

        private TState _currentState;
        private StateMachineStatus _status = StateMachineStatus.Ready;
        private Exception _lastFailure;
        private long _sequence;
        private int _disposed;

        private AsyncStateMachine(
            TState initialState,
            Dictionary<TState, StateNode> topology,
            SameStateTransitionBehavior sameStateBehavior,
            TimeSpan defaultTransitionTimeout,
            TimeSpan rollbackTimeout,
            int maxHistoryRecords,
            int maxChainedTransitions,
            Action<Exception> observerErrorSink)
        {
            _currentState = initialState;
            _topology = topology;
            _sameStateBehavior = sameStateBehavior;
            _defaultTransitionTimeout = defaultTransitionTimeout;
            _rollbackTimeout = rollbackTimeout;
            _maxHistoryRecords = maxHistoryRecords;
            _maxChainedTransitions = maxChainedTransitions;
            _observerErrorSink = observerErrorSink;
            _lifetimeToken = _lifetimeCts.Token;
        }

        /// <summary>
        /// 构建状态机：在 <paramref name="configure"/> 内声明全部状态与规则，返回前完成拓扑校验
        /// （初始状态必须已声明、所有转换目标必须已声明、无守卫规则之后不得再声明同触发器规则）。
        /// Builder 不会逃逸，构建即冻结，不存在"运行到一半改拓扑"的窗口。
        /// </summary>
        public static AsyncStateMachine<TState, TTrigger> Build(TState initialState, Action<Builder> configure)
        {
            if (configure == null) throw new ArgumentNullException(nameof(configure));
            var builder = new Builder();
            configure(builder);
            return builder.Build(initialState);
        }

        /// <summary>当前已提交的状态。转换执行中读取到的是转换前的源状态。</summary>
        public TState CurrentState
        {
            get { lock (_stateSync) return _currentState; }
        }

        /// <summary>整机状态。</summary>
        public StateMachineStatus Status
        {
            get { lock (_stateSync) return _status; }
        }

        /// <summary>是否处于 Faulted。</summary>
        public bool IsFaulted => Status == StateMachineStatus.Faulted;

        /// <summary>最近一次导致失败的异常；补偿也失败时为 AggregateException。成功提交后清空。</summary>
        public Exception LastFailure
        {
            get { lock (_stateSync) return _lastFailure; }
        }

        /// <summary>机器级同状态转换默认策略（单条规则可覆盖）。</summary>
        public SameStateTransitionBehavior SameStateBehavior => _sameStateBehavior;

        /// <summary>默认转换超时（约束 Exit/Enter 生命周期），Infinite 表示不限时。</summary>
        public TimeSpan DefaultTransitionTimeout => _defaultTransitionTimeout;

        /// <summary>补偿与恢复的独立超时，不受调用方已取消的 Token 影响。</summary>
        public TimeSpan RollbackTimeout => _rollbackTimeout;

        /// <summary>诊断历史上限，最旧记录先淘汰。</summary>
        public int MaxHistoryRecords => _maxHistoryRecords;

        /// <summary>单次外层触发允许引发的链式（入队）转换上限，超过判定为死循环并 Faulted。</summary>
        public int MaxChainedTransitions => _maxChainedTransitions;

        /// <summary>每次尝试（成功、拒绝、失败、入队、丢弃、恢复）完成后触发；观察者异常被隔离并送往 ObserverErrorSink。</summary>
        public event Action<StateTransitionRecord<TState, TTrigger>> TransitionRecorded;

        /// <summary>成功提交新状态后触发（含同状态 Reenter 与恢复），参数为 (source, target)。</summary>
        public event Action<TState, TState> StateChanged;

        /// <summary>返回诊断历史快照（时间升序）。</summary>
        public IReadOnlyList<StateTransitionRecord<TState, TTrigger>> GetHistorySnapshot()
        {
            lock (_historySync) return _history.ToArray();
        }

        /// <summary>
        /// 触发一次转换。并发调用严格串行；处理器内重入调用立即返回 Enqueued 记录并入队。
        /// 机器 Faulted 时抛 InvalidOperationException（属使用错误，必须先 RecoverAsync）；
        /// 调用方取消抛 OperationCanceledException（补偿完成后才抛出）。
        /// </summary>
        public async UniTask<StateTransitionRecord<TState, TTrigger>> FireAsync(
            TTrigger trigger,
            CancellationToken cancellationToken = default)
        {
            ThrowIfDisposed();

            // 处理器内重入：入队，由持门的外层转换收敛后串行执行。
            // Complete 在锁外执行，避免在 _queueSync 内回调用户观察者。
            object flowMarker = _executionMarker.Value;
            if (flowMarker != null)
            {
                bool enqueued = false;
                lock (_queueSync)
                {
                    if (_drainActive && ReferenceEquals(flowMarker, _activeMarker))
                    {
                        _pending.Enqueue(new PendingFire(trigger, cancellationToken));
                        enqueued = true;
                    }
                }
                if (enqueued)
                {
                    TState current;
                    lock (_stateSync) current = _currentState;
                    var enqueueContext = new StateTransitionContext<TState, TTrigger>(
                        Interlocked.Increment(ref _sequence), current, current, trigger,
                        hasTrigger: true, isReentry: false, isInternal: false, isRecovery: false);
                    return Complete(enqueueContext, StateTransitionOutcome.Enqueued,
                        Stopwatch.GetTimestamp(), null);
                }
            }

            using (CancellationTokenSource waitCts = CancellationTokenSource.CreateLinkedTokenSource(
                       cancellationToken, _lifetimeToken))
            {
                await _transitionGate.WaitAsync(waitCts.Token);
            }

            object marker = new object();
            object previousFlowMarker = _executionMarker.Value;
            _executionMarker.Value = marker;
            lock (_queueSync)
            {
                _drainActive = true;
                _activeMarker = marker;
            }
            try
            {
                ThrowIfDisposed();
                if (Status == StateMachineStatus.Faulted)
                    throw new InvalidOperationException("状态机处于 Faulted；必须先调用 RecoverAsync。", LastFailure);

                StateTransitionRecord<TState, TTrigger> record =
                    await ExecuteTransitionAsync(trigger, cancellationToken, throwOnCallerCancel: true);
                await DrainPendingAsync();
                return record;
            }
            finally
            {
                _executionMarker.Value = previousFlowMarker;
                // 先失效活动 marker，使并发的重入判定改走等门路径；再清扫残留队列项（异常路径兜底，
                // 成功路径此时队列已空），保证不会有排队项被无声搁置。
                lock (_queueSync)
                {
                    _drainActive = false;
                    _activeMarker = null;
                }
                DropAllPending(null);
                _transitionGate.Release();
            }
        }

        /// <summary>
        /// 从 Faulted 显式恢复：执行目标状态的 Enter（上下文 IsRecovery=true），成功后提交为当前状态。
        /// 目标状态必须在构建时声明过；机器非 Faulted 时调用属使用错误。恢复失败保持 Faulted 并返回 false
        /// （若目标状态配置了 OnRollback 会尽力清理部分进入的目标）。不允许在处理器内发起恢复。
        /// </summary>
        public async UniTask<bool> RecoverAsync(TState state, CancellationToken cancellationToken = default)
        {
            ThrowIfDisposed();
            if (!_topology.TryGetValue(state, out StateNode node))
                throw new ArgumentException($"状态 {state} 未在构建时声明，不能作为恢复目标。", nameof(state));
            object flowMarker = _executionMarker.Value;
            if (flowMarker != null)
            {
                lock (_queueSync)
                {
                    if (_drainActive && ReferenceEquals(flowMarker, _activeMarker))
                        throw new InvalidOperationException("不允许在处理器内发起 RecoverAsync；恢复应由外部协调者驱动。");
                }
            }

            using (CancellationTokenSource waitCts = CancellationTokenSource.CreateLinkedTokenSource(
                       cancellationToken, _lifetimeToken))
            {
                await _transitionGate.WaitAsync(waitCts.Token);
            }

            object marker = new object();
            object previousFlowMarker = _executionMarker.Value;
            _executionMarker.Value = marker;
            lock (_queueSync)
            {
                _drainActive = true;
                _activeMarker = marker;
            }
            long started = Stopwatch.GetTimestamp();
            TState source;
            lock (_stateSync) source = _currentState;
            var context = new StateTransitionContext<TState, TTrigger>(
                Interlocked.Increment(ref _sequence), source, state, default,
                hasTrigger: false, isReentry: false, isInternal: false, isRecovery: true);
            try
            {
                ThrowIfDisposed();
                if (Status != StateMachineStatus.Faulted)
                    throw new InvalidOperationException("只有 Faulted 状态机允许 RecoverAsync。");

                SetStatus(StateMachineStatus.Transitioning, LastFailure);
                using (CancellationTokenSource recoveryCts = CancellationTokenSource.CreateLinkedTokenSource(
                           cancellationToken, _lifetimeToken))
                {
                    if (_rollbackTimeout != Timeout.InfiniteTimeSpan)
                        recoveryCts.CancelAfter(_rollbackTimeout);
                    bool enterRan = false;
                    try
                    {
                        if (node.EnterHandlers.Length > 0)
                        {
                            enterRan = true;
                            await InvokeHandlers(node.EnterHandlers, context, recoveryCts.Token);
                        }
                        recoveryCts.Token.ThrowIfCancellationRequested();
                        lock (_stateSync)
                        {
                            // 迟到的恢复处理器不得复活已释放的机器。
                            if (Volatile.Read(ref _disposed) != 0)
                                throw new OperationCanceledException("状态机已释放。", _lifetimeToken);
                            _currentState = state;
                            _status = StateMachineStatus.Ready;
                            _lastFailure = null;
                        }
                        NotifyStateChanged(source, state);
                        Complete(context, StateTransitionOutcome.RecoverySucceeded, started, null);
                        await DrainPendingAsync();
                        return true;
                    }
                    catch (OperationCanceledException ex) when (recoveryCts.IsCancellationRequested)
                    {
                        Exception failure = await CleanupFailedRecoveryAsync(node, context, ex, enterRan);
                        if (_lifetimeToken.IsCancellationRequested)
                        {
                            SetStatus(StateMachineStatus.Disposed, failure);
                            Complete(context, StateTransitionOutcome.RecoveryFailed, started, failure);
                            throw new OperationCanceledException("状态机已释放。", failure, _lifetimeToken);
                        }
                        SetStatus(StateMachineStatus.Faulted, failure);
                        Complete(context, StateTransitionOutcome.RecoveryFailed, started, failure);
                        if (cancellationToken.IsCancellationRequested)
                            throw new OperationCanceledException("恢复已取消。", failure, cancellationToken);
                        return false;
                    }
                    catch (Exception ex)
                    {
                        Exception failure = await CleanupFailedRecoveryAsync(node, context, ex, enterRan);
                        if (_lifetimeToken.IsCancellationRequested)
                        {
                            SetStatus(StateMachineStatus.Disposed, failure);
                            Complete(context, StateTransitionOutcome.RecoveryFailed, started, failure);
                            throw new OperationCanceledException("状态机已释放。", failure, _lifetimeToken);
                        }
                        SetStatus(StateMachineStatus.Faulted, failure);
                        Complete(context, StateTransitionOutcome.RecoveryFailed, started, failure);
                        return false;
                    }
                }
            }
            finally
            {
                _executionMarker.Value = previousFlowMarker;
                lock (_queueSync)
                {
                    _drainActive = false;
                    _activeMarker = null;
                }
                DropAllPending(null);
                _transitionGate.Release();
            }
        }

        /// <summary>
        /// 核心转换（调用方必须已持门）：规则选路 → 守卫 → Exit/Enter → 提交；失败走显式补偿。
        /// <paramref name="throwOnCallerCancel"/> 为 false 时（队列排空场景）取消只记录不抛出。
        /// </summary>
        private async UniTask<StateTransitionRecord<TState, TTrigger>> ExecuteTransitionAsync(
            TTrigger trigger,
            CancellationToken callerToken,
            bool throwOnCallerCancel)
        {
            TState source;
            lock (_stateSync) source = _currentState;
            long sequence = Interlocked.Increment(ref _sequence);
            long started = Stopwatch.GetTimestamp();
            StateNode sourceNode = _topology[source];

            if (sourceNode.Rules == null || !sourceNode.Rules.TryGetValue(trigger, out TransitionRule[] rules))
            {
                var noMatch = new StateTransitionContext<TState, TTrigger>(
                    sequence, source, source, trigger, true, false, false, false);
                return Complete(noMatch, StateTransitionOutcome.NoTransitionFound, started, null);
            }

            // 规则选路：按声明顺序取首个可用规则。
            // 同状态策略先于守卫求值——Ignore/Reject 不应触发可能昂贵的守卫。
            TransitionRule selected = null;
            StateTransitionContext<TState, TTrigger> context = null;
            using (CancellationTokenSource guardCts = CancellationTokenSource.CreateLinkedTokenSource(
                       callerToken, _lifetimeToken))
            {
                for (int i = 0; i < rules.Length; i++)
                {
                    TransitionRule rule = rules[i];
                    bool sameState = !rule.IsInternal &&
                                     EqualityComparer<TState>.Default.Equals(source, rule.Target);
                    var candidate = new StateTransitionContext<TState, TTrigger>(
                        sequence, source, rule.IsInternal ? source : rule.Target, trigger,
                        hasTrigger: true, isReentry: sameState, isInternal: rule.IsInternal, isRecovery: false);
                    if (sameState)
                    {
                        SameStateTransitionBehavior behavior = rule.SameStateBehavior ?? _sameStateBehavior;
                        if (behavior == SameStateTransitionBehavior.Ignore)
                            return Complete(candidate, StateTransitionOutcome.IgnoredSameState, started, null);
                        if (behavior == SameStateTransitionBehavior.Reject)
                            return Complete(candidate, StateTransitionOutcome.RejectedSameState, started, null);
                    }

                    if (rule.Guard == null)
                    {
                        selected = rule;
                        context = candidate;
                        break;
                    }
                    try
                    {
                        if (await rule.Guard(candidate, guardCts.Token))
                        {
                            selected = rule;
                            context = candidate;
                            break;
                        }
                    }
                    catch (OperationCanceledException ex) when (guardCts.IsCancellationRequested)
                    {
                        // 守卫阶段取消：尚无任何副作用，无需补偿。
                        if (_lifetimeToken.IsCancellationRequested)
                        {
                            SetStatus(StateMachineStatus.Disposed, ex);
                            Complete(candidate, StateTransitionOutcome.Cancelled, started, ex);
                            throw new OperationCanceledException("状态机已释放。", ex, _lifetimeToken);
                        }
                        StateTransitionRecord<TState, TTrigger> cancelled =
                            Complete(candidate, StateTransitionOutcome.Cancelled, started, ex);
                        if (throwOnCallerCancel)
                            throw new OperationCanceledException("状态转换已取消。", ex, callerToken);
                        return cancelled;
                    }
                    catch (Exception ex)
                    {
                        // 守卫异常：视为转换失败；守卫约定无副作用，状态保持 Ready。
                        return Complete(candidate, StateTransitionOutcome.Failed, started, ex);
                    }
                }
            }

            if (selected == null)
            {
                var rejected = new StateTransitionContext<TState, TTrigger>(
                    sequence, source, source, trigger, true, false, false, false);
                return Complete(rejected, StateTransitionOutcome.GuardRejected, started, null);
            }

            TimeSpan timeout = selected.Timeout ?? _defaultTransitionTimeout;
            using (CancellationTokenSource transitionCts = CancellationTokenSource.CreateLinkedTokenSource(
                       callerToken, _lifetimeToken))
            {
                if (timeout != Timeout.InfiniteTimeSpan)
                    transitionCts.CancelAfter(timeout);

                if (selected.IsInternal)
                {
                    return await ExecuteInternalAsync(
                        selected, context, started, transitionCts, callerToken, throwOnCallerCancel);
                }

                StateNode targetNode = _topology[selected.Target];
                bool sourceExitRan = false;
                bool targetEnterRan = false;
                try
                {
                    SetStatus(StateMachineStatus.Transitioning, null);
                    if (sourceNode.ExitHandlers.Length > 0)
                    {
                        sourceExitRan = true;
                        await InvokeHandlers(sourceNode.ExitHandlers, context, transitionCts.Token);
                    }
                    if (targetNode.EnterHandlers.Length > 0)
                    {
                        targetEnterRan = true;
                        await InvokeHandlers(targetNode.EnterHandlers, context, transitionCts.Token);
                    }
                    // 处理器忽略取消令牌也不允许在取消/超时后提交。
                    transitionCts.Token.ThrowIfCancellationRequested();

                    lock (_stateSync)
                    {
                        // 迟到的处理器不得向已释放的机器提交目标状态。
                        if (Volatile.Read(ref _disposed) != 0)
                            throw new OperationCanceledException("状态机已释放。", _lifetimeToken);
                        _currentState = selected.Target;
                        _status = StateMachineStatus.Ready;
                        _lastFailure = null;
                    }
                    NotifyStateChanged(source, selected.Target);
                    return Complete(context, StateTransitionOutcome.Succeeded, started, null);
                }
                catch (OperationCanceledException ex) when (transitionCts.IsCancellationRequested)
                {
                    if (_lifetimeToken.IsCancellationRequested && !sourceExitRan && !targetEnterRan)
                    {
                        SetStatus(StateMachineStatus.Disposed, ex);
                        Complete(context, StateTransitionOutcome.Cancelled, started, ex);
                        throw new OperationCanceledException("状态机已释放。", ex, _lifetimeToken);
                    }
                    Exception combined = await CompensateAsync(
                        sourceNode, targetNode, context, ex, sourceExitRan, targetEnterRan);
                    if (_lifetimeToken.IsCancellationRequested)
                    {
                        Exception failure = combined ?? ex;
                        SetStatus(StateMachineStatus.Disposed, failure);
                        Complete(context, StateTransitionOutcome.Cancelled, started, failure);
                        throw new OperationCanceledException("状态机已释放。", failure, _lifetimeToken);
                    }
                    bool callerCancelled = callerToken.IsCancellationRequested;
                    bool recovered = combined == null;
                    StateTransitionOutcome outcome = recovered
                        ? (callerCancelled ? StateTransitionOutcome.Cancelled : StateTransitionOutcome.TimedOut)
                        : StateTransitionOutcome.Faulted;
                    SetStatus(recovered ? StateMachineStatus.Ready : StateMachineStatus.Faulted, combined ?? ex);
                    StateTransitionRecord<TState, TTrigger> record =
                        Complete(context, outcome, started, combined ?? ex);
                    if (callerCancelled && throwOnCallerCancel)
                        throw new OperationCanceledException("状态转换已取消。", combined ?? ex, callerToken);
                    return record;
                }
                catch (Exception ex)
                {
                    Exception combined = await CompensateAsync(
                        sourceNode, targetNode, context, ex, sourceExitRan, targetEnterRan);
                    if (_lifetimeToken.IsCancellationRequested)
                    {
                        Exception failure = combined ?? ex;
                        SetStatus(StateMachineStatus.Disposed, failure);
                        Complete(context, StateTransitionOutcome.Cancelled, started, failure);
                        throw new OperationCanceledException("状态机已释放。", failure, _lifetimeToken);
                    }
                    bool recovered = combined == null;
                    SetStatus(recovered ? StateMachineStatus.Ready : StateMachineStatus.Faulted, combined ?? ex);
                    return Complete(
                        context,
                        recovered ? StateTransitionOutcome.Failed : StateTransitionOutcome.Faulted,
                        started,
                        combined ?? ex);
                }
            }
        }

        /// <summary>内部转换：只执行规则自带的处理器，不动 Exit/Enter、不改状态；处理器自持一致性，失败无补偿。</summary>
        private async UniTask<StateTransitionRecord<TState, TTrigger>> ExecuteInternalAsync(
            TransitionRule rule,
            StateTransitionContext<TState, TTrigger> context,
            long started,
            CancellationTokenSource transitionCts,
            CancellationToken callerToken,
            bool throwOnCallerCancel)
        {
            try
            {
                await InvokeHandlers(rule.InternalHandlers, context, transitionCts.Token);
                return Complete(context, StateTransitionOutcome.Succeeded, started, null);
            }
            catch (OperationCanceledException ex) when (transitionCts.IsCancellationRequested)
            {
                if (_lifetimeToken.IsCancellationRequested)
                {
                    SetStatus(StateMachineStatus.Disposed, ex);
                    Complete(context, StateTransitionOutcome.Cancelled, started, ex);
                    throw new OperationCanceledException("状态机已释放。", ex, _lifetimeToken);
                }
                bool callerCancelled = callerToken.IsCancellationRequested;
                StateTransitionRecord<TState, TTrigger> record = Complete(
                    context,
                    callerCancelled ? StateTransitionOutcome.Cancelled : StateTransitionOutcome.TimedOut,
                    started,
                    ex);
                if (callerCancelled && throwOnCallerCancel)
                    throw new OperationCanceledException("状态转换已取消。", ex, callerToken);
                return record;
            }
            catch (Exception ex)
            {
                return Complete(context, StateTransitionOutcome.Failed, started, ex);
            }
        }

        /// <summary>
        /// 显式补偿：先清理部分进入的 Target（其 OnRollback），再恢复已退出的 Source（其 OnRollback）。
        /// 需要补偿的状态未配置 OnRollback 视为补偿失败（fail-closed）。
        /// 返回 null 表示补偿成功；否则返回聚合了原始异常与全部补偿异常的 AggregateException。
        /// </summary>
        private async UniTask<Exception> CompensateAsync(
            StateNode sourceNode,
            StateNode targetNode,
            StateTransitionContext<TState, TTrigger> failedContext,
            Exception error,
            bool sourceExitRan,
            bool targetEnterRan)
        {
            List<Exception> failures = null;
            using (CancellationTokenSource rollbackCts = CancellationTokenSource.CreateLinkedTokenSource(
                       _lifetimeToken))
            {
                if (_rollbackTimeout != Timeout.InfiniteTimeSpan)
                    rollbackCts.CancelAfter(_rollbackTimeout);

                if (targetEnterRan)
                {
                    if (targetNode.RollbackHandlers.Length == 0)
                    {
                        (failures = failures ?? new List<Exception>()).Add(new StateMachineCompensationException(
                            $"目标状态 {failedContext.Target} 的 Enter 已开始执行但未配置 OnRollback，无法证明已清理。"));
                    }
                    else
                    {
                        var cleanup = new StateRollbackContext<TState, TTrigger>(
                            failedContext, RollbackPhase.CleanupTarget, error);
                        try { await InvokeRollbacks(targetNode.RollbackHandlers, cleanup, rollbackCts.Token); }
                        catch (Exception ex) { (failures = failures ?? new List<Exception>()).Add(ex); }
                    }
                }

                if (sourceExitRan)
                {
                    if (sourceNode.RollbackHandlers.Length == 0)
                    {
                        (failures = failures ?? new List<Exception>()).Add(new StateMachineCompensationException(
                            $"源状态 {failedContext.Source} 的 Exit 已开始执行但未配置 OnRollback，无法证明已恢复。"));
                    }
                    else
                    {
                        var restore = new StateRollbackContext<TState, TTrigger>(
                            failedContext, RollbackPhase.RestoreSource, error);
                        try { await InvokeRollbacks(sourceNode.RollbackHandlers, restore, rollbackCts.Token); }
                        catch (Exception ex) { (failures = failures ?? new List<Exception>()).Add(ex); }
                    }
                }
            }

            if (failures == null) return null;
            failures.Insert(0, error);
            return new AggregateException("转换失败且补偿未完成，机器进入 Faulted。", failures);
        }

        /// <summary>恢复失败后的尽力清理：目标状态配置了 OnRollback 时执行之；返回聚合失败原因（机器保持 Faulted）。</summary>
        private async UniTask<Exception> CleanupFailedRecoveryAsync(
            StateNode node,
            StateTransitionContext<TState, TTrigger> recoveryContext,
            Exception error,
            bool enterRan)
        {
            if (!enterRan || node.RollbackHandlers.Length == 0 || _lifetimeToken.IsCancellationRequested)
                return error;
            using (CancellationTokenSource cleanupCts = CancellationTokenSource.CreateLinkedTokenSource(
                       _lifetimeToken))
            {
                if (_rollbackTimeout != Timeout.InfiniteTimeSpan)
                    cleanupCts.CancelAfter(_rollbackTimeout);
                var cleanup = new StateRollbackContext<TState, TTrigger>(
                    recoveryContext, RollbackPhase.CleanupTarget, error);
                try
                {
                    await InvokeRollbacks(node.RollbackHandlers, cleanup, cleanupCts.Token);
                    return error;
                }
                catch (Exception ex)
                {
                    return new AggregateException("恢复失败且目标状态清理失败。", error, ex);
                }
            }
        }

        /// <summary>排空处理器内入队的触发（调用方必须已持门）。机器非 Ready 时剩余项丢弃；链式超限判死循环。</summary>
        private async UniTask DrainPendingAsync()
        {
            int chained = 0;
            while (true)
            {
                PendingFire next;
                lock (_queueSync)
                {
                    if (_pending.Count == 0) return;
                    next = _pending.Dequeue();
                }

                if (Status != StateMachineStatus.Ready)
                {
                    RecordPendingOutcome(next, StateTransitionOutcome.Dropped, LastFailure);
                    continue;
                }
                if (next.Token.IsCancellationRequested)
                {
                    RecordPendingOutcome(next, StateTransitionOutcome.Cancelled, null);
                    continue;
                }
                chained++;
                if (chained > _maxChainedTransitions)
                {
                    var limitError = new StateMachineChainLimitException(_maxChainedTransitions);
                    SetStatus(StateMachineStatus.Faulted, limitError);
                    RecordPendingOutcome(next, StateTransitionOutcome.Dropped, limitError);
                    continue;
                }
                try
                {
                    await ExecuteTransitionAsync(next.Trigger, next.Token, throwOnCallerCancel: false);
                }
                catch (OperationCanceledException)
                {
                    // 仅在生命周期取消（Dispose）时到达；剩余项由下一轮的状态检查丢弃。
                }
            }
        }

        /// <summary>同步丢弃全部排队项并审计（异常路径兜底与 Dispose 清扫）。</summary>
        private void DropAllPending(Exception reason)
        {
            while (true)
            {
                PendingFire next;
                lock (_queueSync)
                {
                    if (_pending.Count == 0) return;
                    next = _pending.Dequeue();
                }
                RecordPendingOutcome(next, StateTransitionOutcome.Dropped, reason ?? LastFailure);
            }
        }

        private void RecordPendingOutcome(PendingFire pending, StateTransitionOutcome outcome, Exception error)
        {
            TState current;
            lock (_stateSync) current = _currentState;
            var context = new StateTransitionContext<TState, TTrigger>(
                Interlocked.Increment(ref _sequence), current, current, pending.Trigger,
                hasTrigger: true, isReentry: false, isInternal: false, isRecovery: false);
            Complete(context, outcome, Stopwatch.GetTimestamp(), error);
        }

        private static async UniTask InvokeHandlers(
            StateHandler[] handlers,
            StateTransitionContext<TState, TTrigger> context,
            CancellationToken cancellationToken)
        {
            for (int i = 0; i < handlers.Length; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await handlers[i](context, cancellationToken);
            }
        }

        private static async UniTask InvokeRollbacks(
            RollbackHandler[] handlers,
            StateRollbackContext<TState, TTrigger> context,
            CancellationToken cancellationToken)
        {
            for (int i = 0; i < handlers.Length; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await handlers[i](context, cancellationToken);
            }
        }

        /// <summary>落一条审计记录并发布事件；观察者异常隔离后送 ObserverErrorSink。</summary>
        private StateTransitionRecord<TState, TTrigger> Complete(
            StateTransitionContext<TState, TTrigger> context,
            StateTransitionOutcome outcome,
            long startedTimestamp,
            Exception error)
        {
            var record = new StateTransitionRecord<TState, TTrigger>(
                context, outcome, Elapsed(startedTimestamp), error, Status);
            lock (_historySync)
            {
                while (_history.Count >= _maxHistoryRecords) _history.Dequeue();
                _history.Enqueue(record);
            }
            try { TransitionRecorded?.Invoke(record); }
            catch (Exception ex) { NotifyObserverError(ex); }
            return record;
        }

        private void NotifyStateChanged(TState source, TState target)
        {
            try { StateChanged?.Invoke(source, target); }
            catch (Exception ex) { NotifyObserverError(ex); }
        }

        private void NotifyObserverError(Exception error)
        {
            try { _observerErrorSink?.Invoke(error); }
            catch { /* 诊断出口自身的异常没有更下游的去处，只能吞。 */ }
        }

        private void SetStatus(StateMachineStatus status, Exception failure)
        {
            lock (_stateSync)
            {
                // Disposed 是终态，任何迟到的状态写入都不得覆盖。
                if (Volatile.Read(ref _disposed) != 0 && status != StateMachineStatus.Disposed)
                {
                    _status = StateMachineStatus.Disposed;
                    return;
                }
                _status = status;
                _lastFailure = failure;
            }
        }

        private static TimeSpan Elapsed(long startedTimestamp)
        {
            long ticks = Stopwatch.GetTimestamp() - startedTimestamp;
            return TimeSpan.FromSeconds(Math.Max(0, ticks) / (double)Stopwatch.Frequency);
        }

        private void ThrowIfDisposed()
        {
            if (Volatile.Read(ref _disposed) != 0)
                throw new ObjectDisposedException(nameof(AsyncStateMachine<TState, TTrigger>));
        }

        /// <summary>
        /// 释放：置 Disposed、取消生命周期令牌（在飞转换经补偿收敛后以 OperationCanceledException 结束）、
        /// 丢弃排队项。不显式 Dispose 内部句柄：等待者一律经取消令牌退出，SemaphoreSlim 未创建 WaitHandle、
        /// 生命周期 CTS 无挂接定时器，显式 Dispose 反而与在飞的链接源创建存在竞态。
        /// </summary>
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            lock (_stateSync) _status = StateMachineStatus.Disposed;
            try { _lifetimeCts.Cancel(); } catch { /* 经取消回调抛出的下游异常与释放语义无关。 */ }
            DropAllPending(null);
        }

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
