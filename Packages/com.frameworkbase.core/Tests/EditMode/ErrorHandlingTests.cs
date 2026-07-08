using System.Collections.Generic;
using Framework.Core.Errors;
using NUnit.Framework;
using UnityEngine.TestTools;

namespace Framework.Tests
{
    /// <summary>
    /// 错误码字典与统一处理单测：解析优先级（精确 > 窄区间 > 宽区间 > 默认）、
    /// 文案回退链（localizer → key → 服务端 message → 默认）、Handle 流程与埋点限流。
    /// </summary>
    public class ErrorHandlingTests
    {
        [SetUp]
        public void SetUp()
        {
            LogAssert.ignoreFailingMessages = true;
        }

        [TearDown]
        public void TearDown()
        {
            LogAssert.ignoreFailingMessages = false;
        }

        // ── 注册表解析 ───────────────────────────────────────────────────────

        [Test]
        public void 解析优先级_精确大于窄区间大于宽区间大于默认()
        {
            var reg = new ErrorCodeRegistry();
            reg.RegisterRange(1000, 1999, ErrorReaction.Toast, "宽段");
            reg.RegisterRange(1100, 1199, ErrorReaction.Popup, "窄段");
            reg.Register(1105, ErrorReaction.ForceLogout, "精确");

            Assert.AreEqual("精确", reg.ResolveRule(1105).MessageKey, "精确注册最优先");
            Assert.AreEqual("窄段", reg.ResolveRule(1150).MessageKey, "窄区间优先于宽区间");
            Assert.AreEqual("宽段", reg.ResolveRule(1500).MessageKey, "只命中宽区间");
            Assert.AreEqual(reg.DefaultRule.MessageKey, reg.ResolveRule(9999).MessageKey, "未命中走默认规则");
        }

        [Test]
        public void 同跨度区间_后注册覆盖()
        {
            var reg = new ErrorCodeRegistry();
            reg.RegisterRange(100, 199, ErrorReaction.Toast, "先");
            reg.RegisterRange(100, 199, ErrorReaction.Popup, "后");

            Assert.AreEqual("后", reg.ResolveRule(150).MessageKey);
        }

        [Test]
        public void 区间端点_含两端()
        {
            var reg = new ErrorCodeRegistry();
            reg.RegisterRange(100, 200, ErrorReaction.Toast, "段");

            Assert.AreEqual("段", reg.ResolveRule(100).MessageKey);
            Assert.AreEqual("段", reg.ResolveRule(200).MessageKey);
            Assert.AreEqual(reg.DefaultRule.MessageKey, reg.ResolveRule(99).MessageKey);
            Assert.AreEqual(reg.DefaultRule.MessageKey, reg.ResolveRule(201).MessageKey);
        }

        [Test]
        public void 文案回退链_逐级降级()
        {
            var reg = new ErrorCodeRegistry();
            reg.Register(1, ErrorReaction.Toast, "key_a");
            reg.Register(2, ErrorReaction.Toast); // 无 key → 用服务端 message
            reg.SetLocalizer(key => key == "key_a" ? "本地化文案A" : null);

            Assert.AreEqual("本地化文案A", reg.Resolve(1).Message, "localizer 翻出用译文");

            reg.SetLocalizer(null);
            Assert.AreEqual("key_a", reg.Resolve(1).Message, "无 localizer 时 key 原样显示");

            Assert.AreEqual("服务端说的", reg.Resolve(2, "服务端说的").Message, "无 key 用服务端 message");
            Assert.AreEqual(reg.DefaultRule.MessageKey, reg.Resolve(2).Message, "都没有用默认文案");
        }

        [Test]
        public void 框架默认注册表_内置客户端本地码()
        {
            var reg = ErrorCodeRegistry.CreateWithFrameworkDefaults();

            Assert.AreEqual(ErrorReaction.PopupRetry, reg.ResolveRule(ClientErrorCodes.Timeout).Reaction);
            Assert.AreEqual(ErrorReaction.PopupRetry, reg.ResolveRule(ClientErrorCodes.Disconnected).Reaction);
            Assert.AreEqual(ErrorReaction.Toast, reg.ResolveRule(ClientErrorCodes.ParseError).Reaction);
        }

        // ── ErrorCenter 处理流程 ─────────────────────────────────────────────

        [Test]
        public void 成功码_静默且不触发呈现器()
        {
            var presenter = new RecordingPresenter();
            var center = new ErrorCenter(new ErrorCodeRegistry(), presenter, () => 0);

            ErrorDecision decision = center.Handle(0);

            Assert.AreEqual(ErrorReaction.Silent, decision.Reaction);
            Assert.AreEqual(0, presenter.Presented.Count, "成功码不产生任何动作");
        }

        [Test]
        public void 错误码_按字典决策并交呈现器()
        {
            var reg = new ErrorCodeRegistry();
            reg.Register(101, ErrorReaction.ForceLogout, "会话已失效");
            var presenter = new RecordingPresenter();
            var center = new ErrorCenter(reg, presenter, () => 0);

            ErrorDecision decision = center.Handle(101);

            Assert.AreEqual(ErrorReaction.ForceLogout, decision.Reaction);
            Assert.AreEqual("会话已失效", decision.Message);
            Assert.AreEqual(1, presenter.Presented.Count);
            Assert.AreEqual(101, presenter.Presented[0].Code);
        }

        [Test]
        public void 呈现器异常_不打断调用方()
        {
            var presenter = new RecordingPresenter { Throw = true };
            var center = new ErrorCenter(new ErrorCodeRegistry(), presenter, () => 0);

            Assert.DoesNotThrow(() => center.Handle(1), "呈现器炸了不能反过来炸业务错误分支");
        }

        [Test]
        public void 埋点限流_同码60秒一次_不同码互不影响()
        {
            double now = 0;
            var presenter = new RecordingPresenter();
            var center = new ErrorCenter(new ErrorCodeRegistry(), presenter, () => now);

            // ErrorCenter 属 Kernel 层，不直连 Analytics；埋点经 ErrorReported 事件外发（ADR-002），
            // 单测直接订阅该事件即可验证限流：同码 60 秒窗口内只上报一次，跨窗口与不同码各自计数。
            var reported = new List<int>();
            center.ErrorReported += d => reported.Add(d.Code);

            center.Handle(1);   // 上报
            center.Handle(1);   // 限流（同码同窗口）
            now = 30;
            center.Handle(1);   // 限流（<60）
            now = 61;
            center.Handle(1);   // 上报（>60，新窗口）
            center.Handle(2);   // 上报（不同码）

            Assert.AreEqual(5, presenter.Presented.Count, "限流只限埋点，呈现每次都执行");
            CollectionAssert.AreEqual(new[] { 1, 1, 2 }, reported, "限流后仅这三次触发埋点上报");
        }

        // ── 假呈现器 ─────────────────────────────────────────────────────────

        private sealed class RecordingPresenter : IErrorPresenter
        {
            public readonly List<ErrorDecision> Presented = new List<ErrorDecision>();
            public bool Throw;

            public void Present(ErrorDecision decision)
            {
                if (Throw)
                    throw new System.InvalidOperationException("presenter boom");
                Presented.Add(decision);
            }
        }
    }
}
