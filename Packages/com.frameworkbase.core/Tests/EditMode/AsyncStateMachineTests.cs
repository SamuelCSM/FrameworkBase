using System;
using System.Collections.Generic;
using System.Threading;
using Cysharp.Threading.Tasks;
using Framework.Foundation;
using NUnit.Framework;
using UnityEngine.TestTools;

namespace Framework.Tests
{
    public class AsyncStateMachineTests
    {
        private enum State { Idle, Loading, Ready, Error }
        private enum Trigger { Start, Complete, Reset, Refresh, Unknown }

        private static T Wait<T>(UniTask<T> task) => task.GetAwaiter().GetResult();

        [Test]
        public void 同步转换_ExitEnter按序且成功后才提交状态()
        {
            var order = new List<string>();
            AsyncStateMachine<State, Trigger> machine = null;
            machine = AsyncStateMachine<State, Trigger>.Build(State.Idle, b =>
            {
                b.State(State.Idle)
                    .OnExit(_ =>
                    {
                        Assert.AreEqual(State.Idle, machine.CurrentState, "Exit 阶段尚未提交目标状态");
                        order.Add("exit-idle");
                    })
                    .Permit(Trigger.Start, State.Ready);
                b.State(State.Ready)
                    .OnEnter(_ =>
                    {
                        Assert.AreEqual(State.Idle, machine.CurrentState, "Enter 全部成功前不得暴露目标状态");
                        order.Add("enter-ready");
                    });
            });
            try
            {
                StateTransitionRecord<State, Trigger> result = Wait(machine.FireAsync(Trigger.Start));

                Assert.IsTrue(result.Succeeded);
                Assert.AreEqual(State.Ready, machine.CurrentState);
                CollectionAssert.AreEqual(new[] { "exit-idle", "enter-ready" }, order);
                Assert.AreEqual(StateTransitionOutcome.Succeeded, machine.GetHistorySnapshot()[0].Outcome);

                Assert.AreEqual(
                    StateTransitionOutcome.NoTransitionFound,
                    Wait(machine.FireAsync(Trigger.Unknown)).Outcome);
            }
            finally
            {
                machine.Dispose();
            }
        }

        [Test]
        public void Guard拒绝_不执行生命周期且状态不变()
        {
            int handlers = 0;
            using var machine = AsyncStateMachine<State, Trigger>.Build(State.Idle, b =>
            {
                b.State(State.Idle)
                    .OnExit(_ => handlers++)
                    .Permit(Trigger.Start, State.Ready, guard: _ => false);
                b.State(State.Ready).OnEnter(_ => handlers++);
            });

            StateTransitionRecord<State, Trigger> result = Wait(machine.FireAsync(Trigger.Start));

            Assert.AreEqual(StateTransitionOutcome.GuardRejected, result.Outcome);
            Assert.AreEqual(State.Idle, machine.CurrentState);
            Assert.AreEqual(0, handlers);
        }

        [Test]
        public void 同触发器多规则_按声明序取首个通过守卫者()
        {
            int firstGuardCalls = 0;
            using var machine = AsyncStateMachine<State, Trigger>.Build(State.Idle, b =>
            {
                b.State(State.Idle)
                    .Permit(Trigger.Start, State.Ready, guard: _ => { firstGuardCalls++; return false; })
                    .Permit(Trigger.Start, State.Loading, guard: _ => true);
                b.State(State.Ready);
                b.State(State.Loading);
            });

            StateTransitionRecord<State, Trigger> result = Wait(machine.FireAsync(Trigger.Start));

            Assert.AreEqual(StateTransitionOutcome.Succeeded, result.Outcome);
            Assert.AreEqual(State.Loading, machine.CurrentState);
            Assert.AreEqual(1, firstGuardCalls);
        }

        [Test]
        public void 构建期校验_非法拓扑与非法配置全部立即拒绝()
        {
            // 转换目标未声明。
            Assert.Throws<InvalidOperationException>(() =>
                AsyncStateMachine<State, Trigger>.Build(State.Idle, b =>
                    b.State(State.Idle).Permit(Trigger.Start, State.Ready)));

            // 初始状态未声明。
            Assert.Throws<InvalidOperationException>(() =>
                AsyncStateMachine<State, Trigger>.Build(State.Ready, b =>
                    b.State(State.Idle)));

            // 无守卫规则之后再声明同触发器规则（不可达）。
            Assert.Throws<InvalidOperationException>(() =>
                AsyncStateMachine<State, Trigger>.Build(State.Idle, b =>
                {
                    b.State(State.Ready);
                    b.State(State.Idle)
                        .Permit(Trigger.Start, State.Ready)
                        .Permit(Trigger.Start, State.Ready, guard: _ => true);
                }));

            // 重复声明状态。
            Assert.Throws<InvalidOperationException>(() =>
                AsyncStateMachine<State, Trigger>.Build(State.Idle, b =>
                {
                    b.State(State.Idle);
                    b.State(State.Idle);
                }));

            // 非法超时。
            Assert.Throws<ArgumentOutOfRangeException>(() =>
                AsyncStateMachine<State, Trigger>.Build(State.Idle, b =>
                {
                    b.DefaultTransitionTimeout = TimeSpan.Zero;
                    b.State(State.Idle);
                }));
        }

        [Test]
        public void 同状态策略_Ignore先于守卫且三种策略语义明确()
        {
            int guardCalls = 0;
            int handlers = 0;
            using (var ignored = AsyncStateMachine<State, Trigger>.Build(State.Ready, b =>
                   {
                       b.State(State.Ready)
                           .OnExit(_ => handlers++)
                           .OnEnter(_ => handlers++)
                           .Permit(Trigger.Refresh, State.Ready, guard: _ => { guardCalls++; return true; });
                   }))
            {
                Assert.AreEqual(
                    StateTransitionOutcome.IgnoredSameState,
                    Wait(ignored.FireAsync(Trigger.Refresh)).Outcome);
                Assert.AreEqual(0, guardCalls, "Ignore 策略下不得执行守卫");
                Assert.AreEqual(0, handlers);
            }

            using (var rejected = AsyncStateMachine<State, Trigger>.Build(State.Ready, b =>
                   {
                       b.SameStateBehavior = SameStateTransitionBehavior.Reject;
                       b.State(State.Ready).Permit(Trigger.Refresh, State.Ready);
                   }))
            {
                Assert.AreEqual(
                    StateTransitionOutcome.RejectedSameState,
                    Wait(rejected.FireAsync(Trigger.Refresh)).Outcome);
            }

            int reenteredHandlers = 0;
            using (var reentered = AsyncStateMachine<State, Trigger>.Build(State.Ready, b =>
                   {
                       b.SameStateBehavior = SameStateTransitionBehavior.Reenter;
                       b.State(State.Ready)
                           .OnExit(_ => reenteredHandlers++)
                           .OnEnter(_ => reenteredHandlers++)
                           .Permit(Trigger.Refresh, State.Ready);
                   }))
            {
                StateTransitionRecord<State, Trigger> record = Wait(reentered.FireAsync(Trigger.Refresh));
                Assert.IsTrue(record.Succeeded);
                Assert.IsTrue(record.IsReentry);
                Assert.AreEqual(2, reenteredHandlers);
            }
        }

        [Test]
        public void 内部转换_只执行自带处理器且不动生命周期不改状态()
        {
            int lifecycle = 0;
            int internalRuns = 0;
            using var machine = AsyncStateMachine<State, Trigger>.Build(State.Ready, b =>
            {
                b.State(State.Ready)
                    .OnEnter(_ => lifecycle++)
                    .OnExit(_ => lifecycle++)
                    .PermitInternal(Trigger.Refresh, _ => internalRuns++);
            });

            StateTransitionRecord<State, Trigger> record = Wait(machine.FireAsync(Trigger.Refresh));

            Assert.AreEqual(StateTransitionOutcome.Succeeded, record.Outcome);
            Assert.IsTrue(record.IsInternal);
            Assert.AreEqual(State.Ready, machine.CurrentState);
            Assert.AreEqual(1, internalRuns);
            Assert.AreEqual(0, lifecycle);
        }

        [UnityTest]
        public System.Collections.IEnumerator 异步转换取消_显式补偿后传播取消() => UniTask.ToCoroutine(async () =>
        {
            var phases = new List<RollbackPhase>();
            using var machine = AsyncStateMachine<State, Trigger>.Build(State.Idle, b =>
            {
                b.State(State.Idle).Permit(Trigger.Start, State.Loading);
                b.State(State.Loading)
                    .OnEnterAsync(async (context, token) =>
                        await UniTask.Delay(TimeSpan.FromSeconds(10), cancellationToken: token))
                    .OnRollback(context => phases.Add(context.Phase));
            });

            using var cts = new CancellationTokenSource();
            UniTask<StateTransitionRecord<State, Trigger>> transition = machine.FireAsync(Trigger.Start, cts.Token);
            await UniTask.Yield();
            cts.Cancel();

            bool cancelled = false;
            try { await transition; }
            catch (OperationCanceledException) { cancelled = true; }
            Assert.IsTrue(cancelled);
            Assert.AreEqual(State.Idle, machine.CurrentState);
            Assert.AreEqual(StateMachineStatus.Ready, machine.Status);
            CollectionAssert.AreEqual(new[] { RollbackPhase.CleanupTarget }, phases);
            Assert.AreEqual(StateTransitionOutcome.Cancelled, machine.GetHistorySnapshot()[0].Outcome);
        });

        [UnityTest]
        public System.Collections.IEnumerator 转换超时_TimedOut且先清理Target再恢复Source() => UniTask.ToCoroutine(async () =>
        {
            var order = new List<string>();
            using var machine = AsyncStateMachine<State, Trigger>.Build(State.Idle, b =>
            {
                b.State(State.Idle)
                    .OnExit(_ => order.Add("exit-idle"))
                    .OnRollback(_ => order.Add("restore-source"))
                    .Permit(Trigger.Start, State.Loading, timeout: TimeSpan.FromMilliseconds(30));
                b.State(State.Loading)
                    .OnEnterAsync(async (context, token) =>
                        await UniTask.Delay(TimeSpan.FromSeconds(2), cancellationToken: token))
                    .OnRollback(_ => order.Add("cleanup-target"));
            });

            StateTransitionRecord<State, Trigger> result = await machine.FireAsync(Trigger.Start);

            Assert.AreEqual(StateTransitionOutcome.TimedOut, result.Outcome);
            Assert.AreEqual(State.Idle, machine.CurrentState);
            Assert.AreEqual(StateMachineStatus.Ready, machine.Status);
            CollectionAssert.AreEqual(new[] { "exit-idle", "cleanup-target", "restore-source" }, order);
        });

        [Test]
        public void TargetEnter失败_补偿成功回到源状态()
        {
            var order = new List<string>();
            using var machine = AsyncStateMachine<State, Trigger>.Build(State.Idle, b =>
            {
                b.State(State.Idle)
                    .OnExit(_ => order.Add("exit-idle"))
                    .OnRollback(_ => order.Add("restore-source"))
                    .Permit(Trigger.Start, State.Ready);
                b.State(State.Ready)
                    .OnEnter(_ => throw new InvalidOperationException("enter failed"))
                    .OnRollback(_ => order.Add("cleanup-target"));
            });

            StateTransitionRecord<State, Trigger> result = Wait(machine.FireAsync(Trigger.Start));

            Assert.AreEqual(StateTransitionOutcome.Failed, result.Outcome);
            Assert.IsInstanceOf<InvalidOperationException>(result.Error);
            Assert.AreEqual(State.Idle, machine.CurrentState);
            Assert.AreEqual(StateMachineStatus.Ready, machine.Status);
            CollectionAssert.AreEqual(new[] { "exit-idle", "cleanup-target", "restore-source" }, order);
        }

        [Test]
        public void 缺OnRollback_失败直接Faulted且Recover恢复()
        {
            int recovered = 0;
            using var machine = AsyncStateMachine<State, Trigger>.Build(State.Idle, b =>
            {
                b.State(State.Idle)
                    .OnExit(_ => { })
                    .Permit(Trigger.Start, State.Ready);
                b.State(State.Ready)
                    .OnEnter(_ => throw new InvalidOperationException("target failed"));
                b.State(State.Error)
                    .OnEnter(context => { if (context.IsRecovery) recovered++; });
            });

            StateTransitionRecord<State, Trigger> failed = Wait(machine.FireAsync(Trigger.Start));

            Assert.AreEqual(StateTransitionOutcome.Faulted, failed.Outcome);
            Assert.AreEqual(StateMachineStatus.Faulted, machine.Status);
            var aggregate = machine.LastFailure as AggregateException;
            Assert.IsNotNull(aggregate, "失败原因应聚合原始异常与补偿缺失说明");
            Assert.IsTrue(
                aggregate.InnerExceptions.Count >= 2 &&
                ContainsException<StateMachineCompensationException>(aggregate),
                "应包含 StateMachineCompensationException 说明缺失 OnRollback");
            Assert.Throws<InvalidOperationException>(() => Wait(machine.FireAsync(Trigger.Unknown)));

            Assert.IsTrue(Wait(machine.RecoverAsync(State.Error)));
            Assert.AreEqual(State.Error, machine.CurrentState);
            Assert.AreEqual(StateMachineStatus.Ready, machine.Status);
            Assert.AreEqual(1, recovered);
            IReadOnlyList<StateTransitionRecord<State, Trigger>> history = machine.GetHistorySnapshot();
            StateTransitionRecord<State, Trigger> recovery = history[history.Count - 1];
            Assert.AreEqual(StateTransitionOutcome.RecoverySucceeded, recovery.Outcome);
            Assert.IsFalse(recovery.HasTrigger, "恢复流程没有触发器，不得伪装成枚举 default 值");
        }

        [Test]
        public void 补偿失败_Faulted且异常聚合可诊断()
        {
            using var machine = AsyncStateMachine<State, Trigger>.Build(State.Idle, b =>
            {
                b.State(State.Idle)
                    .OnExit(_ => { })
                    .OnRollback(_ => throw new InvalidOperationException("rollback failed"))
                    .Permit(Trigger.Start, State.Ready);
                b.State(State.Ready)
                    .OnEnter(_ => throw new InvalidOperationException("target failed"))
                    .OnRollback(_ => { });
            });

            StateTransitionRecord<State, Trigger> result = Wait(machine.FireAsync(Trigger.Start));

            Assert.AreEqual(StateTransitionOutcome.Faulted, result.Outcome);
            var aggregate = result.Error as AggregateException;
            Assert.IsNotNull(aggregate);
            Assert.AreEqual("target failed", aggregate.InnerExceptions[0].Message, "首个内层异常应为原始失败");
            Assert.IsTrue(
                ContainsMessage(aggregate, "rollback failed"),
                "补偿异常不得被吞掉，必须进入聚合异常");
        }

        [Test]
        public void 处理器内重入_入队并在当前转换提交后串行执行()
        {
            StateTransitionOutcome? enqueuedOutcome = null;
            AsyncStateMachine<State, Trigger> machine = null;
            machine = AsyncStateMachine<State, Trigger>.Build(State.Idle, b =>
            {
                b.State(State.Idle).Permit(Trigger.Start, State.Loading);
                b.State(State.Loading)
                    .OnEnterAsync(async (context, _) =>
                    {
                        StateTransitionRecord<State, Trigger> inner = await machine.FireAsync(Trigger.Complete);
                        enqueuedOutcome = inner.Outcome;
                    })
                    .Permit(Trigger.Complete, State.Ready);
                b.State(State.Ready);
            });
            try
            {
                StateTransitionRecord<State, Trigger> outer = Wait(machine.FireAsync(Trigger.Start));

                Assert.IsTrue(outer.Succeeded, "外层转换（Idle→Loading）应正常提交");
                Assert.AreEqual(StateTransitionOutcome.Enqueued, enqueuedOutcome, "处理器内重入应入队而非死锁或抛异常");
                Assert.AreEqual(State.Ready, machine.CurrentState, "入队触发应在外层提交后串行执行");
                IReadOnlyList<StateTransitionRecord<State, Trigger>> history = machine.GetHistorySnapshot();
                StateTransitionRecord<State, Trigger> chained = history[history.Count - 1];
                Assert.AreEqual(StateTransitionOutcome.Succeeded, chained.Outcome);
                Assert.AreEqual(State.Ready, chained.Target);
            }
            finally
            {
                machine.Dispose();
            }
        }

        [Test]
        public void 链式转换超限_判定死循环并Faulted()
        {
            AsyncStateMachine<State, Trigger> machine = null;
            machine = AsyncStateMachine<State, Trigger>.Build(State.Idle, b =>
            {
                b.MaxChainedTransitions = 2;
                b.State(State.Idle).Permit(Trigger.Start, State.Loading);
                b.State(State.Loading)
                    .OnEnter(_ => machine.FireAsync(Trigger.Refresh).Forget())
                    .Permit(Trigger.Refresh, State.Ready);
                b.State(State.Ready)
                    .OnEnter(_ => machine.FireAsync(Trigger.Refresh).Forget())
                    .Permit(Trigger.Refresh, State.Loading);
            });
            try
            {
                StateTransitionRecord<State, Trigger> outer = Wait(machine.FireAsync(Trigger.Start));

                Assert.IsTrue(outer.Succeeded);
                Assert.IsTrue(machine.IsFaulted, "互跳超过链式上限应判死循环并 Faulted");
                Assert.IsInstanceOf<StateMachineChainLimitException>(machine.LastFailure);
                IReadOnlyList<StateTransitionRecord<State, Trigger>> history = machine.GetHistorySnapshot();
                Assert.AreEqual(StateTransitionOutcome.Dropped, history[history.Count - 1].Outcome);
                Assert.Throws<InvalidOperationException>(() => Wait(machine.FireAsync(Trigger.Start)));
            }
            finally
            {
                machine.Dispose();
            }
        }

        [UnityTest]
        public System.Collections.IEnumerator 并发触发_严格串行且后项读取前项提交状态() => UniTask.ToCoroutine(async () =>
        {
            int activeHandlers = 0;
            int maxConcurrentHandlers = 0;
            var entered = new UniTaskCompletionSource();
            var release = new UniTaskCompletionSource();
            using var machine = AsyncStateMachine<State, Trigger>.Build(State.Idle, b =>
            {
                b.State(State.Idle).Permit(Trigger.Start, State.Loading);
                b.State(State.Loading)
                    .OnEnterAsync(async (context, token) =>
                    {
                        activeHandlers++;
                        maxConcurrentHandlers = Math.Max(maxConcurrentHandlers, activeHandlers);
                        entered.TrySetResult();
                        await release.Task;
                        activeHandlers--;
                    })
                    .Permit(Trigger.Complete, State.Ready);
                b.State(State.Ready);
            });

            UniTask<StateTransitionRecord<State, Trigger>> first = machine.FireAsync(Trigger.Start);
            await entered.Task;
            // SuppressFlow 模拟"与当前转换无关的并发调用方"（UniTask 的同步段会把 AsyncLocal
            // 泄漏给调用线程，不隔离的话该调用会被判为重入而入队——那是另一条已测路径）。
            System.Threading.Tasks.Task<StateTransitionRecord<State, Trigger>> second;
            using (ExecutionContext.SuppressFlow())
            {
                second = System.Threading.Tasks.Task.Run(
                    async () => await machine.FireAsync(Trigger.Complete));
            }
            await UniTask.Yield();
            Assert.AreEqual(State.Idle, machine.CurrentState);

            release.TrySetResult();
            Assert.IsTrue((await first).Succeeded);
            Assert.IsTrue((await second).Succeeded);
            Assert.AreEqual(State.Ready, machine.CurrentState);
            Assert.AreEqual(1, maxConcurrentHandlers);
        });

        [UnityTest]
        public System.Collections.IEnumerator 处理器派生任务_外层转换结束后触发走正常门径() => UniTask.ToCoroutine(async () =>
        {
            var releaseDeferred = new UniTaskCompletionSource();
            System.Threading.Tasks.Task<StateTransitionRecord<State, Trigger>> deferred = null;
            AsyncStateMachine<State, Trigger> machine = null;
            machine = AsyncStateMachine<State, Trigger>.Build(State.Idle, b =>
            {
                b.State(State.Idle).Permit(Trigger.Start, State.Loading);
                b.State(State.Loading)
                    .OnEnter(_ =>
                    {
                        deferred = System.Threading.Tasks.Task.Run(async () =>
                        {
                            await releaseDeferred.Task;
                            return await machine.FireAsync(Trigger.Complete);
                        });
                    })
                    .Permit(Trigger.Complete, State.Ready);
                b.State(State.Ready);
            });
            try
            {
                Assert.IsTrue((await machine.FireAsync(Trigger.Start)).Succeeded);
                releaseDeferred.TrySetResult();
                StateTransitionRecord<State, Trigger> deferredResult = await deferred;
                Assert.IsTrue(deferredResult.Succeeded, "外层已结束，陈旧异步流标记不得误判为重入");
                Assert.AreEqual(State.Ready, machine.CurrentState);
            }
            finally
            {
                machine.Dispose();
            }
        });

        [Test]
        public void 观察者异常隔离_送往Sink且历史有界()
        {
            var sinkErrors = new List<Exception>();
            using var machine = AsyncStateMachine<State, Trigger>.Build(State.Ready, b =>
            {
                b.SameStateBehavior = SameStateTransitionBehavior.Reenter;
                b.MaxHistoryRecords = 2;
                b.ObserverErrorSink = sinkErrors.Add;
                b.State(State.Ready).Permit(Trigger.Refresh, State.Ready);
            });
            machine.TransitionRecorded += _ => throw new InvalidOperationException("observer failed");

            Assert.DoesNotThrow(() => Wait(machine.FireAsync(Trigger.Refresh)));
            Assert.DoesNotThrow(() => Wait(machine.FireAsync(Trigger.Refresh)));
            Assert.DoesNotThrow(() => Wait(machine.FireAsync(Trigger.Refresh)));

            Assert.AreEqual(2, machine.GetHistorySnapshot().Count);
            Assert.AreEqual(3, sinkErrors.Count, "观察者异常应逐次送达诊断出口而非静默消失");
        }

        [UnityTest]
        public System.Collections.IEnumerator Dispose_取消活动转换且终态保持Disposed() => UniTask.ToCoroutine(async () =>
        {
            var entered = new UniTaskCompletionSource();
            var machine = AsyncStateMachine<State, Trigger>.Build(State.Idle, b =>
            {
                b.State(State.Idle).Permit(Trigger.Start, State.Loading);
                b.State(State.Loading).OnEnterAsync(async (context, token) =>
                {
                    entered.TrySetResult();
                    await UniTask.Delay(TimeSpan.FromSeconds(10), cancellationToken: token);
                });
            });

            UniTask<StateTransitionRecord<State, Trigger>> transition = machine.FireAsync(Trigger.Start);
            await entered.Task;
            machine.Dispose();

            bool cancelled = false;
            try { await transition; }
            catch (OperationCanceledException) { cancelled = true; }
            Assert.IsTrue(cancelled);
            Assert.AreEqual(StateMachineStatus.Disposed, machine.Status);
            Assert.AreEqual(StateTransitionOutcome.Cancelled, machine.GetHistorySnapshot()[0].Outcome);
        });

        [UnityTest]
        public System.Collections.IEnumerator Dispose_处理器忽略取消也不能提交目标状态() => UniTask.ToCoroutine(async () =>
        {
            var entered = new UniTaskCompletionSource();
            var release = new UniTaskCompletionSource();
            var machine = AsyncStateMachine<State, Trigger>.Build(State.Idle, b =>
            {
                b.State(State.Idle).Permit(Trigger.Start, State.Loading);
                b.State(State.Loading).OnEnterAsync(async (context, token) =>
                {
                    entered.TrySetResult();
                    await release.Task;
                });
            });

            UniTask<StateTransitionRecord<State, Trigger>> transition = machine.FireAsync(Trigger.Start);
            await entered.Task;
            machine.Dispose();
            release.TrySetResult();

            bool cancelled = false;
            try { await transition; }
            catch (OperationCanceledException) { cancelled = true; }
            Assert.IsTrue(cancelled);
            Assert.AreEqual(State.Idle, machine.CurrentState, "Dispose 后禁止迟到处理器提交目标状态");
            Assert.AreEqual(StateMachineStatus.Disposed, machine.Status);
        });

        [Test]
        public void Recover非法调用_参数与状态前置校验()
        {
            using var machine = AsyncStateMachine<State, Trigger>.Build(State.Idle, b =>
            {
                b.State(State.Idle);
            });

            // 未声明状态不能作为恢复目标。
            Assert.Throws<ArgumentException>(() => Wait(machine.RecoverAsync(State.Error)));
            // 非 Faulted 机器不允许恢复。
            Assert.Throws<InvalidOperationException>(() => Wait(machine.RecoverAsync(State.Idle)));
        }

        private static bool ContainsException<T>(AggregateException aggregate) where T : Exception
        {
            foreach (Exception inner in aggregate.InnerExceptions)
            {
                if (inner is T) return true;
            }
            return false;
        }

        private static bool ContainsMessage(AggregateException aggregate, string message)
        {
            foreach (Exception inner in aggregate.InnerExceptions)
            {
                if (inner.Message == message) return true;
            }
            return false;
        }
    }
}
