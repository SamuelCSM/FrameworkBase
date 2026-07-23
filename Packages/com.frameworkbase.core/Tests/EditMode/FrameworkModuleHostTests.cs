using System;
using System.Collections.Generic;
using System.Threading;
using Cysharp.Threading.Tasks;
using Framework.Core;
using NUnit.Framework;

namespace Framework.Tests
{
    /// <summary>
    /// 中间层模块宿主（ADR-008）的阶段语义测试：启动阶段 fail-fast 并标记 Faulted、
    /// 运行阶段异常隔离且跳过 Faulted 模块、Dispose 逆序且不跳过 Faulted。
    /// </summary>
    public class FrameworkModuleHostTests
    {
        /// <summary>可编程失败阶段的测试模块，记录自身收到的每个回调名以便断言驱动序列。</summary>
        private sealed class ProbeModule : FrameworkModuleBase
        {
            private readonly string _failPhase;
            /// <summary>可选的跨模块共享日志，用于断言宿主驱动模块的先后顺序。</summary>
            private readonly List<string> _sharedLog;

            public ProbeModule(string name, string failPhase = null, List<string> sharedLog = null)
            {
                Name = name;
                _failPhase = failPhase;
                _sharedLog = sharedLog;
            }

            public string Name { get; }
            public List<string> Calls { get; } = new List<string>();

            private void Enter(string phase)
            {
                Calls.Add(phase);
                _sharedLog?.Add($"{Name}.{phase}");
                if (_failPhase == phase) throw new InvalidOperationException($"{Name} 在 {phase} 故意失败");
            }

            public override void RegisterCapabilities() => Enter(nameof(RegisterCapabilities));

            public override UniTask StartAsync()
            {
                Enter(nameof(StartAsync));
                return UniTask.CompletedTask;
            }

            public override void OnLowMemory() => Enter(nameof(OnLowMemory));

            public override UniTask OnAccountEnterAsync(CancellationToken cancellationToken)
            {
                Enter(nameof(OnAccountEnterAsync));
                return UniTask.CompletedTask;
            }

            public override void OnAccountExit() => Enter(nameof(OnAccountExit));

            public override void OnLateUpdate(float deltaTime) => Enter(nameof(OnLateUpdate));

            public override void Dispose()
            {
                Calls.Add(nameof(Dispose));
                _sharedLog?.Add($"{Name}.{nameof(Dispose)}");
            }
        }

        /// <summary>模块全部同步完成，直接取结果不会挂起（EditMode 无 PlayerLoop 驱动）。</summary>
        private static void Wait(UniTask task) => task.GetAwaiter().GetResult();

        private static FrameworkModuleHost NewSilentHost()
            => new FrameworkModuleHost { ModuleErrorSink = (_, __, ___) => { } };

        [Test]
        public void 能力注册失败_原样抛出且标记Faulted()
        {
            var bad = new ProbeModule("bad", nameof(FrameworkModuleBase.RegisterCapabilities));
            FrameworkModuleHost host = NewSilentHost();
            host.Use(bad);

            Assert.Throws<InvalidOperationException>(() => host.RegisterCapabilities());
            Assert.IsTrue(host.IsFaulted(bad));
            Assert.AreEqual(1, host.FaultedCount);
        }

        [Test]
        public void 能力注册失败后_该模块不再收到启动与运行期回调()
        {
            var bad = new ProbeModule("bad", nameof(FrameworkModuleBase.RegisterCapabilities));
            FrameworkModuleHost host = NewSilentHost();
            host.Use(bad);

            Assert.Throws<InvalidOperationException>(() => host.RegisterCapabilities());
            // 登录重试路径：RegisterCapabilities 幂等不重跑，后续阶段须跳过已失败模块。
            Wait(host.StartAsync());
            Wait(host.OnAccountEnterAsync(CancellationToken.None));
            host.BroadcastLowMemory();
            host.BroadcastLateUpdate(0.016f);
            host.OnAccountExit();

            CollectionAssert.AreEqual(new[] { nameof(FrameworkModuleBase.RegisterCapabilities) }, bad.Calls);
        }

        [Test]
        public void 启动失败_原样抛出且后续模块不被启动()
        {
            var bad = new ProbeModule("bad", nameof(FrameworkModuleBase.StartAsync));
            var later = new ProbeModule("later");
            FrameworkModuleHost host = NewSilentHost();
            host.Use(bad).Use(later);
            host.RegisterCapabilities();

            Assert.Throws<InvalidOperationException>(() => Wait(host.StartAsync()));
            Assert.IsTrue(host.IsFaulted(bad));
            Assert.IsFalse(host.IsFaulted(later));
            // fail-fast：装配错误不继续往下启动，避免半装配状态进入业务。
            CollectionAssert.DoesNotContain(later.Calls, nameof(FrameworkModuleBase.StartAsync));
        }

        [Test]
        public void 运行期异常被隔离_不影响其它模块()
        {
            var bad = new ProbeModule("bad", nameof(FrameworkModuleBase.OnLateUpdate));
            var good = new ProbeModule("good");
            FrameworkModuleHost host = NewSilentHost();
            host.Use(bad).Use(good);
            host.RegisterCapabilities();
            Wait(host.StartAsync());

            Assert.DoesNotThrow(() => host.BroadcastLateUpdate(0.016f));
            CollectionAssert.Contains(good.Calls, nameof(FrameworkModuleBase.OnLateUpdate));
            // 运行期失败不标记 Faulted：帧回调偶发异常不应让模块永久停摆。
            Assert.IsFalse(host.IsFaulted(bad));
        }

        [Test]
        public void Dispose逆序执行_且不跳过Faulted模块()
        {
            var order = new List<string>();
            var bad = new ProbeModule("bad", nameof(FrameworkModuleBase.StartAsync), order);
            var good = new ProbeModule("good", sharedLog: order);
            FrameworkModuleHost host = NewSilentHost();
            host.Use(bad).Use(good);
            host.RegisterCapabilities();
            Assert.Throws<InvalidOperationException>(() => Wait(host.StartAsync()));
            order.Clear();

            host.DisposeAll();

            // Faulted 模块可能在失败前已分配资源，Dispose 必须照常执行，且按登记逆序。
            CollectionAssert.AreEqual(new[] { "good.Dispose", "bad.Dispose" }, order);
            Assert.AreEqual(0, host.Modules.Count);
            Assert.AreEqual(0, host.FaultedCount);
        }

        [Test]
        public void 进入启动阶段后不得再登记模块()
        {
            FrameworkModuleHost host = NewSilentHost();
            host.RegisterCapabilities();

            Assert.Throws<InvalidOperationException>(() => host.Use(new ProbeModule("late")));
        }
    }
}
