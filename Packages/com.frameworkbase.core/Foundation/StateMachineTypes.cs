using System;
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
}
