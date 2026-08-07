using Framework.Core.Telemetry;
using NUnit.Framework;

namespace Framework.Telemetry.Bugly.Tests
{
    /// <summary>
    /// Bugly 后端接管判定单测。
    /// <para>
    /// 守的是一条容易被"顺手改回去"的约束：<c>CrashReporter</c> 只保留一个后端，本包一旦注册就顶掉主干的
    /// <c>LocalFileCrashBackend</c>。骨架状态（AppId 留空）或原生层是空壳时无条件接管，会让崩溃回捞整条链
    /// 静默失效——原生崩溃抓不到、托管异常进无操作原生缝、本地兜底也没了。
    /// </para>
    /// <para>
    /// 原生链接状态在装配时取自 <c>BuglyNative.IsLinked</c>（Editor 恒为 false），故判定接口把它作为参数
    /// 注入，正反两种情况才都能在 EditMode 覆盖。
    /// </para>
    /// </summary>
    public class BuglyBootstrapTests
    {
        /// <summary>构造一份 AppId 已填的参数，代表"项目已真正接入 Bugly"。</summary>
        private static BuglyOptions ConfiguredOptions() => new BuglyOptions { AppId = "test-app-id" };

        [TearDown]
        public void TearDown()
        {
            // CrashReporter.Shutdown 只在已 Install 时复位后端，故先 Install 一次兜底：
            // 否则"只 Register 未 Install"的用例会把后端残留给后续用例（含主干那条"未注册时落本地后端"的断言）。
            CrashReporter.Install();
            CrashReporter.Shutdown();
        }

        [Test]
        public void 未配置AppId_不接管崩溃后端()
        {
            bool takeOver = BuglyBootstrap.ShouldTakeOver(new BuglyOptions(), nativeLinked: true, out string reason);

            Assert.IsFalse(takeOver);
            StringAssert.Contains("AppId", reason, "让位原因需指向未配置 AppId，便于排查为什么没接管");
        }

        [Test]
        public void 参数为null_不接管崩溃后端()
        {
            Assert.IsFalse(BuglyBootstrap.ShouldTakeOver(null, nativeLinked: true, out _));
        }

        [Test]
        public void 原生未链接_即使配置了AppId也不接管()
        {
            bool takeOver = BuglyBootstrap.ShouldTakeOver(ConfiguredOptions(), nativeLinked: false, out string reason);

            // 填了 AppId 但原生层是空壳（没加宏 / Editor / 非移动平台），接管只会换来一个什么都不做的后端。
            Assert.IsFalse(takeOver);
            StringAssert.Contains("原生", reason);
        }

        [Test]
        public void 配置齐全且原生已链接_接管崩溃后端()
        {
            bool takeOver = BuglyBootstrap.ShouldTakeOver(ConfiguredOptions(), nativeLinked: true, out string reason);

            Assert.IsTrue(takeOver);
            Assert.IsNull(reason, "接管时不应留下让位原因");
        }

        [Test]
        public void 让位时_崩溃回捞回落到主干本地落盘后端()
        {
            bool registered = BuglyBootstrap.TryRegisterBackend(new BuglyOptions(), nativeLinked: false);
            CrashReporter.Install();

            Assert.IsFalse(registered);
            // 这正是本轮修复要保住的结果：托管异常仍有人记录，而不是既不上报也不落盘。
            Assert.AreEqual("local-file", CrashReporter.BackendName);
        }

        [Test]
        public void 接管时_崩溃回捞使用Bugly后端()
        {
            bool registered = BuglyBootstrap.TryRegisterBackend(ConfiguredOptions(), nativeLinked: true);
            CrashReporter.Install();

            Assert.IsTrue(registered);
            Assert.AreEqual("bugly", CrashReporter.BackendName);
        }
    }
}
