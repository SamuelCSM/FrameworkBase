using System.Collections.Generic;
using Framework.Analytics;
using NUnit.Framework;

namespace Framework.Tests
{
    /// <summary>
    /// 埋点事件字典单测：注册/覆盖、必带属性存在性与类型、可选属性类型、
    /// Strict 字典外属性、未注册事件去重告警、整数→浮点无损放行、框架内置事件预注册。
    /// </summary>
    public class AnalyticsSchemaTests
    {
        private AnalyticsSchemaRegistry _registry;

        [SetUp]
        public void SetUp()
        {
            _registry = new AnalyticsSchemaRegistry();
            _registry.Register(new AnalyticsEventSchema("stage_enter")
                .Require("stage", AnalyticsPropType.String)
                .Require("attempt", AnalyticsPropType.Integer)
                .Optional("from", AnalyticsPropType.String));
        }

        private static Dictionary<string, object> Props(params (string key, object value)[] pairs)
        {
            var dict = new Dictionary<string, object>();
            foreach ((string key, object value) in pairs)
                dict[key] = value;
            return dict;
        }

        [Test]
        public void 合规事件_零违规()
        {
            var violations = _registry.Validate("stage_enter",
                Props(("stage", "main"), ("attempt", 2), ("from", "login")));

            Assert.IsEmpty(violations, string.Join("\n", violations));
        }

        [Test]
        public void 缺必带属性_报违规()
        {
            var violations = _registry.Validate("stage_enter", Props(("stage", "main")));

            Assert.AreEqual(1, violations.Count);
            StringAssert.Contains("attempt", violations[0]);
        }

        [Test]
        public void 属性类型不匹配_报违规()
        {
            var violations = _registry.Validate("stage_enter",
                Props(("stage", 123), ("attempt", "two"), ("from", true)));

            Assert.AreEqual(3, violations.Count, "必带 ×2 + 可选 ×1 类型全错");
        }

        [Test]
        public void 整数传给Float_无损放行_反向拒绝()
        {
            var registry = new AnalyticsSchemaRegistry();
            registry.Register(new AnalyticsEventSchema("e")
                .Require("ratio", AnalyticsPropType.Float)
                .Require("count", AnalyticsPropType.Integer));

            Assert.IsEmpty(registry.Validate("e", Props(("ratio", 5), ("count", 5))),
                "整数当浮点用无损，应放行");
            Assert.AreEqual(1, registry.Validate("e", Props(("ratio", 0.5f), ("count", 0.5f))).Count,
                "浮点当整数有损，应报违规");
        }

        [Test]
        public void 非Strict事件_额外属性放行_Strict则违规()
        {
            Assert.IsEmpty(_registry.Validate("stage_enter",
                Props(("stage", "m"), ("attempt", 1), ("extra", "x"))), "默认允许字典外属性");

            _registry.Register(new AnalyticsEventSchema("purchase_done")
                .Require("order_id", AnalyticsPropType.String)
                .Strict());
            var violations = _registry.Validate("purchase_done",
                Props(("order_id", "o1"), ("extra", "x")));

            Assert.AreEqual(1, violations.Count);
            StringAssert.Contains("extra", violations[0]);
        }

        [Test]
        public void 未注册事件_告警且同名只报一次()
        {
            Assert.AreEqual(1, _registry.Validate("unknown_event", null).Count, "首次报未注册");
            Assert.IsEmpty(_registry.Validate("unknown_event", null), "同名第二次不再刷屏");
            Assert.AreEqual(1, _registry.Validate("another_unknown", null).Count, "不同事件名各报一次");
        }

        [Test]
        public void 重复注册_后者覆盖()
        {
            _registry.Register(new AnalyticsEventSchema("stage_enter")); // 覆盖为无约束
            Assert.IsEmpty(_registry.Validate("stage_enter", null), "覆盖后旧必带属性约束应消失");
        }

        [Test]
        public void 框架内置事件_预注册且契约与实发一致()
        {
            var registry = AnalyticsSchemaRegistry.CreateWithFrameworkEvents();

            Assert.IsTrue(registry.IsRegistered("launch_run"));
            Assert.IsTrue(registry.IsRegistered("launch_phase"));
            Assert.IsTrue(registry.IsRegistered("analytics_dropped"));
            Assert.IsTrue(registry.IsRegistered("server_error"));

            // 按 LaunchTelemetryHelper / AnalyticsManager / ErrorCenter 的实发属性构造，必须零违规
            Assert.IsEmpty(registry.Validate("launch_run", Props(
                ("run_id", "r1"), ("success", true), ("end_reason", "ok"),
                ("total_ms", 1234L), ("phase_count", 9))));
            Assert.IsEmpty(registry.Validate("launch_phase", Props(
                ("run_id", "r1"), ("phase", "step01"), ("success", true),
                ("duration_ms", 88L), ("detail", ""))));
            Assert.IsEmpty(registry.Validate("analytics_dropped", Props(("count", 3))));
            Assert.IsEmpty(registry.Validate("server_error", Props(("code", 101), ("reaction", "Toast"))));
        }
    }
}
