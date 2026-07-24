using System;
using Cysharp.Threading.Tasks;
using Framework.Sdk;
using NUnit.Framework;

namespace Framework.Tests
{
    /// <summary>
    /// 防沉迷门控纯核单测：验证 ApplyVerdict 的"状态变化才抛、同状态去重、封玩判定"语义。
    /// 周期心跳/拉取属异步集成路径，靠真机/集成验证。
    /// </summary>
    public class AntiAddictionGateTests
    {
        /// <summary>最小合规桩：仅供构造，测试直接调 ApplyVerdict 不触其异步方法。</summary>
        private sealed class FakeCompliance : ISdkComplianceService
        {
            public event Action<SdkPlaytimeVerdict> OnPlaytimeVerdictChanged { add { } remove { } }
            public UniTask<SdkResult<SdkRealNameStatus>> QueryRealNameAsync() => default;
            public UniTask<SdkResult<SdkRealNameStatus>> ShowRealNameAuthAsync() => default;
            public UniTask<SdkResult<SdkPlaytimeVerdict>> QueryPlaytimeAsync() => default;
            public UniTask<SdkResult> ReportPlaytimeHeartbeatAsync(int elapsedSeconds) => default;
        }

        private static SdkPlaytimeVerdict V(SdkPlaytimeState state, int remain = -1)
            => new SdkPlaytimeVerdict { State = state, RemainingSeconds = remain };

        private AntiAddictionGate _gate;
        private int _raised;
        private SdkPlaytimeVerdict _last;

        [SetUp]
        public void SetUp()
        {
            _gate = new AntiAddictionGate(new FakeCompliance());
            _raised = 0;
            _last = null;
            _gate.RestrictionChanged += v => { _raised++; _last = v; };
        }

        [Test]
        public void 初始Allowed_不触发()
        {
            _gate.ApplyVerdict(V(SdkPlaytimeState.Allowed));
            Assert.AreEqual(0, _raised, "基线即 Allowed，收到 Allowed 无需惊动业务");
            Assert.IsFalse(_gate.IsBlocked);
        }

        [Test]
        public void Allowed转Blocked_触发且IsBlocked()
        {
            _gate.ApplyVerdict(V(SdkPlaytimeState.Blocked, 0));
            Assert.AreEqual(1, _raised);
            Assert.IsTrue(_gate.IsBlocked, "禁玩态");
            Assert.AreEqual(SdkPlaytimeState.Blocked, _last.State);
        }

        [Test]
        public void Blocked重复_不再触发()
        {
            _gate.ApplyVerdict(V(SdkPlaytimeState.Blocked, 0));
            _gate.ApplyVerdict(V(SdkPlaytimeState.Blocked, 0));
            Assert.AreEqual(1, _raised, "同状态去重，不每次心跳都惊动业务");
        }

        [Test]
        public void Blocked转Allowed_触发解封()
        {
            _gate.ApplyVerdict(V(SdkPlaytimeState.Blocked, 0));
            _gate.ApplyVerdict(V(SdkPlaytimeState.Allowed));
            Assert.AreEqual(2, _raised, "封→解各抛一次");
            Assert.IsFalse(_gate.IsBlocked);
        }

        [Test]
        public void Restricted_透传剩余秒数()
        {
            _gate.ApplyVerdict(V(SdkPlaytimeState.Restricted, 600));
            Assert.AreEqual(1, _raised);
            Assert.AreEqual(SdkPlaytimeState.Restricted, _gate.CurrentVerdict.State);
            Assert.AreEqual(600, _gate.CurrentVerdict.RemainingSeconds, "剩余可玩秒数透传给业务倒计时");
            Assert.IsFalse(_gate.IsBlocked, "限时内仍可玩，非禁玩");
        }

        [Test]
        public void null裁决_忽略()
        {
            _gate.ApplyVerdict(null);
            Assert.AreEqual(0, _raised);
            Assert.IsNull(_gate.CurrentVerdict);
        }
    }
}
