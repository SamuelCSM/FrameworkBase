using System;
using System.Collections.Generic;
using System.Threading;
using Cysharp.Threading.Tasks;
using Framework.Foundation;
using NUnit.Framework;

namespace Framework.Tests
{
    public class GuideRunnerTests
    {
        private sealed class BoolPayload { public bool Value; }
        private sealed class BoolRule : IRuleEvaluator<BoolPayload>
        {
            public RuleResult Evaluate(BoolPayload payload, RuleContext context)
                => payload.Value ? RuleResult.Passed() : RuleResult.Failed();
        }

        private sealed class ChannelPayload { public int Channel; }

        private sealed class ManualBinder : ITriggerBinder<ChannelPayload>
        {
            private sealed class Handle : IDisposable
            {
                private ManualBinder _owner;
                private readonly int _channel;
                private readonly Action<object> _handler;
                public Handle(ManualBinder owner, int channel, Action<object> handler)
                {
                    _owner = owner;
                    _channel = channel;
                    _handler = handler;
                }
                public void Dispose()
                {
                    ManualBinder owner = _owner;
                    _owner = null;
                    owner?.Remove(_channel, _handler);
                }
            }

            private readonly Dictionary<int, List<Action<object>>> _handlers =
                new Dictionary<int, List<Action<object>>>();

            public IDisposable Bind(ChannelPayload payload, TriggerContext context, Action<object> onTriggered)
            {
                if (!_handlers.TryGetValue(payload.Channel, out List<Action<object>> list))
                {
                    list = new List<Action<object>>();
                    _handlers.Add(payload.Channel, list);
                }
                list.Add(onTriggered);
                return new Handle(this, payload.Channel, onTriggered);
            }

            public void Fire(int channel, object data = null)
            {
                if (!_handlers.TryGetValue(channel, out List<Action<object>> list)) return;
                Action<object>[] snapshot = list.ToArray();
                for (int i = 0; i < snapshot.Length; i++) snapshot[i](data);
            }

            private void Remove(int channel, Action<object> handler)
            {
                if (!_handlers.TryGetValue(channel, out List<Action<object>> list)) return;
                list.Remove(handler);
                if (list.Count == 0) _handlers.Remove(channel);
            }
        }

        /// <summary>指定通道在 Bind 时立即同步发火，用于验证同步完成不会误判失败或被吞掉。</summary>
        private sealed class SyncFireBinder : ITriggerBinder<ChannelPayload>
        {
            private sealed class Handle : IDisposable
            {
                private SyncFireBinder _owner;
                private readonly int _channel;
                private readonly Action<object> _handler;
                public Handle(SyncFireBinder owner, int channel, Action<object> handler)
                {
                    _owner = owner;
                    _channel = channel;
                    _handler = handler;
                }
                public void Dispose()
                {
                    SyncFireBinder owner = _owner;
                    _owner = null;
                    owner?.Remove(_channel, _handler);
                }
            }

            private readonly HashSet<int> _autoFire;
            private readonly Dictionary<int, List<Action<object>>> _handlers =
                new Dictionary<int, List<Action<object>>>();

            public SyncFireBinder(params int[] autoFireChannels)
                => _autoFire = new HashSet<int>(autoFireChannels);

            public IDisposable Bind(ChannelPayload payload, TriggerContext context, Action<object> onTriggered)
            {
                if (!_handlers.TryGetValue(payload.Channel, out List<Action<object>> list))
                {
                    list = new List<Action<object>>();
                    _handlers.Add(payload.Channel, list);
                }
                list.Add(onTriggered);
                var handle = new Handle(this, payload.Channel, onTriggered);
                if (_autoFire.Contains(payload.Channel)) onTriggered(null);
                return handle;
            }

            public void Fire(int channel, object data = null)
            {
                if (!_handlers.TryGetValue(channel, out List<Action<object>> list)) return;
                Action<object>[] snapshot = list.ToArray();
                for (int i = 0; i < snapshot.Length; i++) snapshot[i](data);
            }

            private void Remove(int channel, Action<object> handler)
            {
                if (!_handlers.TryGetValue(channel, out List<Action<object>> list)) return;
                list.Remove(handler);
                if (list.Count == 0) _handlers.Remove(channel);
            }
        }

        private sealed class RecordPayload { public string Name; public bool Fail; }
        private sealed class RecordingAction : IActionExecutor<RecordPayload>
        {
            public readonly List<string> Calls = new List<string>();
            public UniTask<ActionExecutionResult> ExecuteAsync(
                RecordPayload payload,
                ActionContext context,
                CancellationToken cancellationToken)
            {
                Calls.Add(payload.Name);
                return UniTask.FromResult(payload.Fail
                    ? ActionExecutionResult.Failed(payload.Name)
                    : ActionExecutionResult.Succeeded());
            }
        }

        private sealed class MemoryProgress : IGuideRuntimeProgressStore
        {
            public readonly Dictionary<int, GuideProgress> Values = new Dictionary<int, GuideProgress>();
            public GuideProgress Get(int guideId)
                => Values.TryGetValue(guideId, out GuideProgress value) ? value : default;
            public void SetCurrentStep(int guideId, int stepId)
                => Values[guideId] = new GuideProgress(stepId, false);
            public void MarkCompleted(int guideId)
            {
                int step = Get(guideId).CurrentStepId;
                Values[guideId] = new GuideProgress(step, true);
            }
            public void Clear(int guideId) => Values.Remove(guideId);
        }

        [Test]
        public void StartTrigger与StartRule分离_步骤动作和完成触发器全配置推进()
        {
            CreateRuntime(true, out GuideRunner runner, out ManualBinder binder,
                out RecordingAction action, out MemoryProgress progress);
            var events = new List<string>();
            runner.GuideStarted += id => events.Add("start:" + id);
            runner.StepEntered += (guideId, stepId) => events.Add($"step:{guideId}:{stepId}");
            runner.GuideCompleted += id => events.Add("done:" + id);

            runner.StartListening();
            binder.Fire(1, "window-ready");

            Assert.IsTrue(runner.IsRunning);
            Assert.AreEqual(100, runner.CurrentGuideId);
            Assert.AreEqual(10, runner.CurrentStepId);
            Assert.AreEqual(10, progress.Get(100).CurrentStepId);
            CollectionAssert.AreEqual(new[] { "focus" }, action.Calls);

            binder.Fire(2, "clicked");

            Assert.IsFalse(runner.IsRunning);
            Assert.IsTrue(progress.Get(100).IsCompleted);
            CollectionAssert.AreEqual(new[] { "focus", "clear" }, action.Calls);
            CollectionAssert.AreEqual(new[] { "start:100", "step:100:10", "done:100" }, events);
            runner.Dispose();
        }

        [Test]
        public void StartRule不满足时触发信号不会启动且不轮询()
        {
            CreateRuntime(false, out GuideRunner runner, out ManualBinder binder,
                out RecordingAction action, out _);
            runner.StartListening();

            binder.Fire(1);

            Assert.IsFalse(runner.IsRunning);
            Assert.IsEmpty(action.Calls);
            runner.Dispose();
        }

        [Test]
        public void 断点按StepId恢复_Order变化不影响定位()
        {
            CreateRuntime(true, out GuideRunner runner, out _, out _, out MemoryProgress progress,
                includeSecondStep: true);
            progress.SetCurrentStep(100, 20);

            Assert.AreEqual(GuideStartResult.Started,
                runner.TryStartAsync(100).GetAwaiter().GetResult());

            Assert.AreEqual(20, runner.CurrentStepId);
            runner.Dispose();
        }

        [Test]
        public void Action失败按AbortGuide取消并执行Cancel动作()
        {
            CreateRuntime(true, out GuideRunner runner, out ManualBinder binder,
                out RecordingAction action, out _, enterFails: true);
            string failure = null;
            runner.GuideFailed += (_, reason) => failure = reason;
            runner.StartListening();

            binder.Fire(1);

            Assert.IsFalse(runner.IsRunning);
            CollectionAssert.AreEqual(new[] { "focus", "cancel" }, action.Calls);
            Assert.IsNotNull(failure);
            runner.Dispose();
        }

        [Test]
        public void Always引导完成后再次触发从第一步开始而不是续在末步()
        {
            CreateRuntime(true, out GuideRunner runner, out ManualBinder binder,
                out _, out _, includeSecondStep: false, repeatMode: GuideRepeatMode.Always);
            runner.StartListening();
            binder.Fire(1);
            binder.Fire(2);
            Assert.IsFalse(runner.IsRunning);

            binder.Fire(1);

            Assert.IsTrue(runner.IsRunning);
            Assert.AreEqual(10, runner.CurrentStepId);
            runner.Dispose();
        }

        [Test]
        public void 步骤进入时同步完成_应链式推进而非误判失败也不吞信号()
        {
            var rules = new RuleService();
            rules.Register(1, new BoolRule());
            rules.Initialize(new RuleCatalog
            {
                Rules = new[] { new RuleDefinition { Id = 10, Key = "start", RootNodeId = 11 } },
                Nodes = new[]
                {
                    new RuleNodeDefinition { Id = 11, RuleId = 10, Kind = RuleNodeKind.Predicate,
                        TypeId = 1, Payload = new BoolPayload { Value = true } },
                },
            });

            // 通道 2、3（step10/step20 的完成触发器）在 Bind 时同步发火；通道 4（step30）保持手动。
            var binder = new SyncFireBinder(2, 3);
            var triggers = new TriggerService();
            triggers.Register(1, binder);
            triggers.Initialize(new TriggerCatalog
            {
                Triggers = new[]
                {
                    new TriggerDefinition { Id = 20, Key = "start", TypeId = 1,
                        Payload = new ChannelPayload { Channel = 1 } },
                    new TriggerDefinition { Id = 21, Key = "c1", TypeId = 1,
                        Payload = new ChannelPayload { Channel = 2 } },
                    new TriggerDefinition { Id = 22, Key = "c2", TypeId = 1,
                        Payload = new ChannelPayload { Channel = 3 } },
                    new TriggerDefinition { Id = 23, Key = "c3", TypeId = 1,
                        Payload = new ChannelPayload { Channel = 4 } },
                },
            });

            var action = new RecordingAction();
            var actions = new ActionService();
            actions.Register(1, action);
            actions.Initialize(new ActionCatalog
            {
                Actions = new[]
                {
                    new ActionDefinition { Id = 31, Key = "e1", TypeId = 1,
                        Payload = new RecordPayload { Name = "enter10" } },
                    new ActionDefinition { Id = 32, Key = "e2", TypeId = 1,
                        Payload = new RecordPayload { Name = "enter20" } },
                    new ActionDefinition { Id = 33, Key = "e3", TypeId = 1,
                        Payload = new RecordPayload { Name = "enter30" } },
                },
            });

            var progress = new MemoryProgress();
            var runner = new GuideRunner(rules, triggers, actions, progress);
            runner.Initialize(new GuideCatalog
            {
                Guides = new[]
                {
                    new GuideDefinition { Id = 100, Key = "chain", StartRuleId = 10,
                        StartTriggerId = 20, Priority = 10, RepeatMode = GuideRepeatMode.Once },
                },
                Steps = new[]
                {
                    new GuideStepDefinition { GuideId = 100, StepId = 10, Order = 100,
                        CompleteTriggerId = 21, Key = "s1" },
                    new GuideStepDefinition { GuideId = 100, StepId = 20, Order = 200,
                        CompleteTriggerId = 22, Key = "s2" },
                    new GuideStepDefinition { GuideId = 100, StepId = 30, Order = 300,
                        CompleteTriggerId = 23, Key = "s3" },
                },
                StepActions = new[]
                {
                    new GuideStepActionDefinition { GuideId = 100, StepId = 10,
                        Phase = GuideActionPhase.Enter, ActionId = 31, Order = 10,
                        FailurePolicy = GuideActionFailurePolicy.AbortGuide },
                    new GuideStepActionDefinition { GuideId = 100, StepId = 20,
                        Phase = GuideActionPhase.Enter, ActionId = 32, Order = 10,
                        FailurePolicy = GuideActionFailurePolicy.AbortGuide },
                    new GuideStepActionDefinition { GuideId = 100, StepId = 30,
                        Phase = GuideActionPhase.Enter, ActionId = 33, Order = 10,
                        FailurePolicy = GuideActionFailurePolicy.AbortGuide },
                },
            });

            string failure = null;
            runner.GuideFailed += (_, reason) => failure = reason;
            runner.StartListening();

            binder.Fire(1);

            // step10、step20 在进入时即同步完成，应正确链式推进到 step30 等待，且不误判失败。
            Assert.IsNull(failure);
            Assert.IsTrue(runner.IsRunning);
            Assert.AreEqual(30, runner.CurrentStepId);
            CollectionAssert.AreEqual(new[] { "enter10", "enter20", "enter30" }, action.Calls);

            binder.Fire(4);

            Assert.IsFalse(runner.IsRunning);
            Assert.IsTrue(progress.Get(100).IsCompleted);
            Assert.IsNull(failure);
            runner.Dispose();
        }

        /// <summary>可手动推进的假时钟：避免 EditMode 下依赖 PlayerLoop 驱动 UniTask.Delay。</summary>
        private sealed class ManualDelay
        {
            private UniTaskCompletionSource _source;

            public int Requests { get; private set; }
            public TimeSpan LastTimeout { get; private set; }

            public UniTask Delay(TimeSpan timeout, CancellationToken cancellationToken)
            {
                Requests++;
                LastTimeout = timeout;
                var source = new UniTaskCompletionSource();
                _source = source;
                // 步骤推进/会话结束会取消看门狗令牌，这里如实把等待也取消掉。
                cancellationToken.Register(() => source.TrySetCanceled());
                return source.Task;
            }

            /// <summary>让当前等待到期。</summary>
            public void Elapse() => _source?.TrySetResult();
        }

        [Test]
        public void 步骤等待完成信号超时_引导按失败收尾并打点()
        {
            CreateRuntime(true, out GuideRunner runner, out ManualBinder binder,
                out RecordingAction action, out MemoryProgress progress);
            var clock = new ManualDelay();
            runner.StepTimeout = TimeSpan.FromSeconds(30);
            runner.StepTimeoutDelay = clock.Delay;

            var timedOut = new List<(int guideId, int stepId)>();
            var failed = new List<int>();
            runner.StepTimedOut += (guideId, stepId) => timedOut.Add((guideId, stepId));
            runner.GuideFailed += (guideId, _) => failed.Add(guideId);

            runner.StartListening();
            binder.Fire(1);
            Assert.IsTrue(runner.IsRunning, "引导应已启动并停在第一步等待完成信号");
            Assert.AreEqual(1, clock.Requests, "进入步骤应挂上看门狗");
            Assert.AreEqual(TimeSpan.FromSeconds(30), clock.LastTimeout);

            // 完成信号永不到达（配错 TriggerId / 目标被挡）——看门狗到期即中止，避免玩家被遮罩锁死。
            clock.Elapse();

            CollectionAssert.AreEqual(new[] { (100, 10) }, timedOut);
            CollectionAssert.AreEqual(new[] { 100 }, failed);
            Assert.IsFalse(runner.IsRunning);
            Assert.IsFalse(progress.Get(100).IsCompleted, "超时不应把引导记成已完成");
            CollectionAssert.Contains(action.Calls, "cancel");
        }

        [Test]
        public void 步骤正常完成后看门狗失效_不再误判超时()
        {
            CreateRuntime(true, out GuideRunner runner, out ManualBinder binder,
                out _, out MemoryProgress progress);
            var clock = new ManualDelay();
            runner.StepTimeout = TimeSpan.FromSeconds(30);
            runner.StepTimeoutDelay = clock.Delay;

            var timedOut = new List<int>();
            runner.StepTimedOut += (guideId, _) => timedOut.Add(guideId);

            runner.StartListening();
            binder.Fire(1);
            binder.Fire(2);   // 完成信号如期到达

            Assert.IsFalse(runner.IsRunning);
            Assert.IsTrue(progress.Get(100).IsCompleted);

            // 看门狗已随步骤订阅一起取消；此时再推进时钟不得触发任何超时。
            clock.Elapse();
            CollectionAssert.IsEmpty(timedOut);
        }

        [Test]
        public void 步骤配了TimeoutMs时按步覆盖运行器级时限()
        {
            // 运行器级 30s，但该步配了 5s——看门狗须取步骤值。
            CreateRuntime(true, out GuideRunner runner, out ManualBinder binder, out _, out _,
                firstStepTimeoutMs: 5000);
            var clock = new ManualDelay();
            runner.StepTimeout = TimeSpan.FromSeconds(30);
            runner.StepTimeoutDelay = clock.Delay;

            runner.StartListening();
            binder.Fire(1);

            Assert.AreEqual(1, clock.Requests);
            Assert.AreEqual(TimeSpan.FromMilliseconds(5000), clock.LastTimeout,
                "按步 TimeoutMs 应覆盖运行器级 StepTimeout");
        }

        [Test]
        public void StepTimeout置零时不挂看门狗()
        {
            CreateRuntime(true, out GuideRunner runner, out ManualBinder binder, out _, out _);
            var clock = new ManualDelay();
            runner.StepTimeout = TimeSpan.Zero;
            runner.StepTimeoutDelay = clock.Delay;

            runner.StartListening();
            binder.Fire(1);

            Assert.IsTrue(runner.IsRunning);
            Assert.AreEqual(0, clock.Requests, "置零表示不设限，不应产生任何等待");
        }

        private static void CreateRuntime(
            bool startRule,
            out GuideRunner runner,
            out ManualBinder binder,
            out RecordingAction action,
            out MemoryProgress progress,
            bool includeSecondStep = false,
            bool enterFails = false,
            GuideRepeatMode repeatMode = GuideRepeatMode.Once,
            int firstStepTimeoutMs = 0)
        {
            var rules = new RuleService();
            rules.Register(1, new BoolRule());
            rules.Initialize(new RuleCatalog
            {
                Rules = new[] { new RuleDefinition { Id = 10, Key = "start", RootNodeId = 11 } },
                Nodes = new[]
                {
                    new RuleNodeDefinition { Id = 11, RuleId = 10, Kind = RuleNodeKind.Predicate,
                        TypeId = 1, Payload = new BoolPayload { Value = startRule } },
                },
            });

            binder = new ManualBinder();
            var triggers = new TriggerService();
            triggers.Register(1, binder);
            triggers.Initialize(new TriggerCatalog
            {
                Triggers = new[]
                {
                    new TriggerDefinition { Id = 20, Key = "start", TypeId = 1,
                        Payload = new ChannelPayload { Channel = 1 } },
                    new TriggerDefinition { Id = 21, Key = "complete1", TypeId = 1,
                        Payload = new ChannelPayload { Channel = 2 } },
                    new TriggerDefinition { Id = 22, Key = "complete2", TypeId = 1,
                        Payload = new ChannelPayload { Channel = 3 } },
                },
            });

            action = new RecordingAction();
            var actions = new ActionService();
            actions.Register(1, action);
            actions.Initialize(new ActionCatalog
            {
                Actions = new[]
                {
                    new ActionDefinition { Id = 30, Key = "focus", TypeId = 1,
                        Payload = new RecordPayload { Name = "focus", Fail = enterFails } },
                    new ActionDefinition { Id = 31, Key = "clear", TypeId = 1,
                        Payload = new RecordPayload { Name = "clear" } },
                    new ActionDefinition { Id = 32, Key = "cancel", TypeId = 1,
                        Payload = new RecordPayload { Name = "cancel" } },
                },
            });

            var steps = new List<GuideStepDefinition>
            {
                new GuideStepDefinition { GuideId = 100, StepId = 10, Order = 100,
                    CompleteTriggerId = 21, Key = "first", TimeoutMs = firstStepTimeoutMs },
            };
            if (includeSecondStep)
                steps.Add(new GuideStepDefinition { GuideId = 100, StepId = 20, Order = 200,
                    CompleteTriggerId = 22, Key = "second" });

            progress = new MemoryProgress();
            runner = new GuideRunner(rules, triggers, actions, progress);
            runner.Initialize(new GuideCatalog
            {
                Guides = new[]
                {
                    new GuideDefinition { Id = 100, Key = "shop", StartRuleId = 10,
                        StartTriggerId = 20, Priority = 10, RepeatMode = repeatMode },
                },
                Steps = steps.ToArray(),
                StepActions = new[]
                {
                    new GuideStepActionDefinition { GuideId = 100, StepId = 10,
                        Phase = GuideActionPhase.Enter, ActionId = 30, Order = 10,
                        FailurePolicy = GuideActionFailurePolicy.AbortGuide },
                    new GuideStepActionDefinition { GuideId = 100, StepId = 10,
                        Phase = GuideActionPhase.Exit, ActionId = 31, Order = 10,
                        FailurePolicy = GuideActionFailurePolicy.AbortGuide },
                    new GuideStepActionDefinition { GuideId = 100, StepId = 10,
                        Phase = GuideActionPhase.Cancel, ActionId = 32, Order = 10,
                        FailurePolicy = GuideActionFailurePolicy.Continue },
                },
            });
        }
    }
}
