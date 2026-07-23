using System;
using System.Collections.Generic;
using System.Threading;
using Cysharp.Threading.Tasks;
using Framework.Foundation;
using NUnit.Framework;

namespace Framework.Tests
{
    public class OrchestrationServiceTests
    {
        private sealed class BoolPayload
        {
            public bool Value;
            public bool NotReady;
            public bool Throw;
            /// <summary>写进 NotReady 的 reason，用来区分是哪个子节点未就绪。</summary>
            public string Tag;
        }

        private sealed class BoolRule : IRuleEvaluator<BoolPayload>
        {
            public RuleResult Evaluate(BoolPayload payload, RuleContext context)
            {
                if (payload.Throw) throw new InvalidOperationException("rule boom");
                if (payload.NotReady) return RuleResult.NotReady(payload.Tag ?? "loading");
                return payload.Value ? RuleResult.Passed() : RuleResult.Failed("false");
            }
        }

        private sealed class ManualTriggerPayload { }

        private sealed class ManualTriggerBinder : ITriggerBinder<ManualTriggerPayload>
        {
            private sealed class Handle : IDisposable
            {
                private readonly Action _dispose;
                public Handle(Action dispose) => _dispose = dispose;
                public void Dispose() => _dispose();
            }

            public Action<object> Handler;
            public int DisposeCount;

            public IDisposable Bind(
                ManualTriggerPayload payload,
                TriggerContext context,
                Action<object> onTriggered)
            {
                Handler = onTriggered;
                return new Handle(() =>
                {
                    Handler = null;
                    DisposeCount++;
                });
            }
        }

        private sealed class SynchronousTriggerBinder : ITriggerBinder<ManualTriggerPayload>
        {
            private sealed class Handle : IDisposable
            {
                private readonly Action _onDispose;
                public Handle(Action onDispose) => _onDispose = onDispose;
                public void Dispose() => _onDispose();
            }

            public int DisposeCount;

            public IDisposable Bind(
                ManualTriggerPayload payload,
                TriggerContext context,
                Action<object> onTriggered)
            {
                onTriggered("sync");
                return new Handle(() => DisposeCount++);
            }
        }

        private sealed class ActionPayload { public bool Throw; }

        private sealed class TestAction : IActionExecutor<ActionPayload>
        {
            public UniTask<ActionExecutionResult> ExecuteAsync(
                ActionPayload payload,
                ActionContext context,
                CancellationToken cancellationToken)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (payload.Throw) throw new InvalidOperationException("action boom");
                return UniTask.FromResult(ActionExecutionResult.Succeeded());
            }
        }

        [Test]
        public void Rule支持AllAnyNot并保留NotReady和异常隔离()
        {
            var errors = new List<Exception>();
            var rules = new RuleService { ObserverErrorSink = errors.Add };
            rules.Register(1, new BoolRule());
            rules.Initialize(new RuleCatalog
            {
                Rules = new[]
                {
                    new RuleDefinition { Id = 1, Key = "all", RootNodeId = 10 },
                    new RuleDefinition { Id = 2, Key = "error", RootNodeId = 20 },
                },
                Nodes = new[]
                {
                    new RuleNodeDefinition { Id = 10, RuleId = 1, Kind = RuleNodeKind.All },
                    new RuleNodeDefinition { Id = 11, RuleId = 1, Kind = RuleNodeKind.Predicate, TypeId = 1,
                        Payload = new BoolPayload { Value = true } },
                    new RuleNodeDefinition { Id = 12, RuleId = 1, Kind = RuleNodeKind.Not },
                    new RuleNodeDefinition { Id = 13, RuleId = 1, Kind = RuleNodeKind.Predicate, TypeId = 1,
                        Payload = new BoolPayload { Value = false } },
                    new RuleNodeDefinition { Id = 14, RuleId = 1, Kind = RuleNodeKind.Predicate, TypeId = 1,
                        Payload = new BoolPayload { NotReady = true } },
                    new RuleNodeDefinition { Id = 20, RuleId = 2, Kind = RuleNodeKind.Predicate, TypeId = 1,
                        Payload = new BoolPayload { Throw = true } },
                },
                Edges = new[]
                {
                    new RuleEdgeDefinition { ParentNodeId = 10, ChildNodeId = 11, Order = 1 },
                    new RuleEdgeDefinition { ParentNodeId = 10, ChildNodeId = 12, Order = 2 },
                    new RuleEdgeDefinition { ParentNodeId = 10, ChildNodeId = 14, Order = 3 },
                    new RuleEdgeDefinition { ParentNodeId = 12, ChildNodeId = 13, Order = 1 },
                },
            });

            Assert.AreEqual(RuleStatus.NotReady, rules.Evaluate(1).Status);
            Assert.AreEqual(RuleStatus.Error, rules.Evaluate(2).Status);
            Assert.AreEqual(1, errors.Count);
        }

        [Test]
        public void 多个NotReady子节点时保留首个原因()
        {
            var rules = new RuleService();
            rules.Register(1, new BoolRule());
            rules.Initialize(new RuleCatalog
            {
                Rules = new[]
                {
                    new RuleDefinition { Id = 1, Key = "all", RootNodeId = 10 },
                    new RuleDefinition { Id = 2, Key = "any", RootNodeId = 20 },
                },
                Nodes = new[]
                {
                    new RuleNodeDefinition { Id = 10, RuleId = 1, Kind = RuleNodeKind.All },
                    new RuleNodeDefinition { Id = 11, RuleId = 1, Kind = RuleNodeKind.Predicate, TypeId = 1,
                        Payload = new BoolPayload { NotReady = true, Tag = "first" } },
                    new RuleNodeDefinition { Id = 12, RuleId = 1, Kind = RuleNodeKind.Predicate, TypeId = 1,
                        Payload = new BoolPayload { NotReady = true, Tag = "second" } },
                    new RuleNodeDefinition { Id = 20, RuleId = 2, Kind = RuleNodeKind.Any },
                    new RuleNodeDefinition { Id = 21, RuleId = 2, Kind = RuleNodeKind.Predicate, TypeId = 1,
                        Payload = new BoolPayload { NotReady = true, Tag = "first" } },
                    new RuleNodeDefinition { Id = 22, RuleId = 2, Kind = RuleNodeKind.Predicate, TypeId = 1,
                        Payload = new BoolPayload { NotReady = true, Tag = "second" } },
                },
                Edges = new[]
                {
                    new RuleEdgeDefinition { ParentNodeId = 10, ChildNodeId = 11, Order = 1 },
                    new RuleEdgeDefinition { ParentNodeId = 10, ChildNodeId = 12, Order = 2 },
                    new RuleEdgeDefinition { ParentNodeId = 20, ChildNodeId = 21, Order = 1 },
                    new RuleEdgeDefinition { ParentNodeId = 20, ChildNodeId = 22, Order = 2 },
                },
            });

            // 子节点按 Order 稳定排序，诊断 reason 须确定地指向最先未就绪的那个条件。
            RuleResult all = rules.Evaluate(1);
            Assert.AreEqual(RuleStatus.NotReady, all.Status);
            Assert.AreEqual("first", all.Reason);

            RuleResult any = rules.Evaluate(2);
            Assert.AreEqual(RuleStatus.NotReady, any.Status);
            Assert.AreEqual("first", any.Reason);
        }

        [Test]
        public void Explain逐节点展开求值树_顶层与短路裁决一致且叶子原因可见()
        {
            var rules = new RuleService();
            rules.Register(1, new BoolRule());
            rules.Initialize(new RuleCatalog
            {
                Rules = new[] { new RuleDefinition { Id = 1, Key = "all", RootNodeId = 10 } },
                Nodes = new[]
                {
                    new RuleNodeDefinition { Id = 10, RuleId = 1, Kind = RuleNodeKind.All },
                    new RuleNodeDefinition { Id = 11, RuleId = 1, Kind = RuleNodeKind.Predicate, TypeId = 7,
                        Payload = new BoolPayload { Value = true } },
                    new RuleNodeDefinition { Id = 12, RuleId = 1, Kind = RuleNodeKind.Predicate, TypeId = 8,
                        Payload = new BoolPayload { Value = false } },
                },
                Edges = new[]
                {
                    new RuleEdgeDefinition { ParentNodeId = 10, ChildNodeId = 11, Order = 1 },
                    new RuleEdgeDefinition { ParentNodeId = 10, ChildNodeId = 12, Order = 2 },
                },
            });

            System.Collections.Generic.IReadOnlyList<RuleService.RuleTraceLine> trace = rules.Explain(1);

            // 前序：父在子前，且逐节点求值（不因短路漏掉第二个叶子）。
            Assert.AreEqual(3, trace.Count);
            Assert.AreEqual(RuleNodeKind.All, trace[0].Kind);
            Assert.AreEqual(0, trace[0].Depth);
            Assert.AreEqual(RuleStatus.Failed, trace[0].Status, "顶层状态须与短路裁决一致");
            Assert.AreEqual(RuleResult.Failed().Status, rules.Evaluate(1).Status);

            Assert.AreEqual(11, trace[1].NodeId);
            Assert.AreEqual(1, trace[1].Depth);
            Assert.AreEqual(RuleStatus.Passed, trace[1].Status);

            Assert.AreEqual(12, trace[2].NodeId);
            Assert.AreEqual(RuleStatus.Failed, trace[2].Status);
            Assert.AreEqual("false", trace[2].Reason, "叶子失败原因须透出，供定位具体条件");
        }

        [Test]
        public void TriggerBindOnce对同步触发安全且只回调一次()
        {
            var binder = new SynchronousTriggerBinder();
            var triggers = new TriggerService();
            triggers.Register(1, binder);
            triggers.Initialize(new TriggerCatalog
            {
                Triggers = new[]
                {
                    new TriggerDefinition { Id = 10, Key = "sync", TypeId = 1, Payload = new ManualTriggerPayload() },
                },
            });

            int count = 0;
            IDisposable handle = triggers.BindOnce(10, default, _ => count++);

            Assert.AreEqual(1, count);
            Assert.AreEqual(1, binder.DisposeCount, "同步触发发生在 Binder 返回句柄前，返回后仍必须立即释放");
            Assert.DoesNotThrow(() => handle.Dispose());
            Assert.AreEqual(1, binder.DisposeCount, "释放幂等");
        }

        [Test]
        public void Trigger普通订阅释放后不再收到信号()
        {
            var binder = new ManualTriggerBinder();
            var triggers = new TriggerService();
            triggers.Register(1, binder);
            triggers.Initialize(new TriggerCatalog
            {
                Triggers = new[]
                {
                    new TriggerDefinition { Id = 10, Key = "manual", TypeId = 1, Payload = new ManualTriggerPayload() },
                },
            });
            int count = 0;
            IDisposable handle = triggers.Bind(10, default, _ => count++);

            binder.Handler("first");
            handle.Dispose();

            Assert.AreEqual(1, count);
            Assert.IsNull(binder.Handler);
            Assert.AreEqual(1, binder.DisposeCount);
        }

        [Test]
        public void Action取消与执行器异常都转换为结果()
        {
            var errors = new List<Exception>();
            var actions = new ActionService { ObserverErrorSink = errors.Add };
            actions.Register(1, new TestAction());
            actions.Initialize(new ActionCatalog
            {
                Actions = new[]
                {
                    new ActionDefinition { Id = 10, Key = "ok", TypeId = 1, Payload = new ActionPayload() },
                    new ActionDefinition { Id = 11, Key = "boom", TypeId = 1,
                        Payload = new ActionPayload { Throw = true } },
                },
            });

            var cancelled = new CancellationToken(true);
            ActionExecutionResult cancelResult = actions.ExecuteAsync(10, default, cancelled).GetAwaiter().GetResult();
            ActionExecutionResult failed = actions.ExecuteAsync(11, default).GetAwaiter().GetResult();

            Assert.AreEqual(ActionExecutionStatus.Cancelled, cancelResult.Status);
            Assert.AreEqual(ActionExecutionStatus.Failed, failed.Status);
            Assert.AreEqual(1, errors.Count);
        }
    }
}
