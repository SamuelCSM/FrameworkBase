using System.IO;
using Cysharp.Threading.Tasks;
using Framework.Core.Telemetry;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace Framework.Tests
{
    /// <summary>
    /// 崩溃后端抽象单测：
    ///  - CrashReporter 编排（注册时序、Install 装配、归因转发、flush 路由、监听挂接）；
    ///  - LocalFileCrashBackend 落盘（字段、会话上限、无 URL 不上报）；
    ///  - MockCrashBackend 透传。
    /// 每个用例结束 Shutdown()，避免 static 监听残留污染后续测试。
    /// </summary>
    public class CrashReporterTests
    {
        [TearDown]
        public void TearDown()
        {
            CrashReporter.Shutdown();
        }

        // ── CrashReporter 编排 ──────────────────────────────────────────────

        [Test]
        public void Install_WithoutRegister_UsesDefaultLocalBackend()
        {
            CrashReporter.Install();
            Assert.AreEqual("local-file", CrashReporter.BackendName);
        }

        [Test]
        public void Install_ForwardsSessionToRegisteredBackend()
        {
            var mock = new MockCrashBackend();
            CrashReporter.Register(mock);
            CrashReporter.Install();

            Assert.AreEqual("mock", CrashReporter.BackendName);
            Assert.IsTrue(mock.Installed);
            Assert.AreEqual(Application.version, mock.Session.AppVersion);
            Assert.IsNotNull(mock.Session.PersistentDataPath);
        }

        [Test]
        public void RegisterAfterInstall_IsRejected()
        {
            var first = new MockCrashBackend();
            CrashReporter.Register(first);
            CrashReporter.Install();

            LogAssert.Expect(LogType.Error, new System.Text.RegularExpressions.Regex("拒绝注册后端"));
            CrashReporter.Register(new MockCrashBackend());

            Assert.AreEqual("mock", CrashReporter.BackendName);
        }

        [Test]
        public void AttributionCalls_ForwardToBackend()
        {
            var mock = new MockCrashBackend();
            CrashReporter.Register(mock);
            CrashReporter.Install();

            CrashReporter.SetUser("u_42");
            CrashReporter.SetCustomKey("channel", "taptap");
            CrashReporter.LeaveBreadcrumb("enter_lobby");

            Assert.AreEqual("u_42", mock.UserId);
            Assert.AreEqual("taptap", mock.CustomKeys["channel"]);
            CollectionAssert.Contains(mock.Breadcrumbs, "enter_lobby");
        }

        [Test]
        public void AttributionCalls_BeforeInstall_AreSilentlyIgnored()
        {
            // 未 Install：无后端可转发，不得抛异常。
            Assert.DoesNotThrow(() => CrashReporter.SetUser("u_1"));
            Assert.AreEqual("none", CrashReporter.BackendName);
        }

        [Test]
        public void TryUploadPending_RoutesToBackendResult()
        {
            var mock = new MockCrashBackend { FlushResult = true };
            CrashReporter.Register(mock);
            CrashReporter.Install();

            bool ok = CrashReporter.TryUploadPendingAsync().GetAwaiter().GetResult();

            Assert.IsTrue(ok);
            Assert.AreEqual(1, mock.FlushCallCount);
        }

        [Test]
        public void LoggedException_IsForwardedAsManagedException()
        {
            var mock = new MockCrashBackend();
            CrashReporter.Register(mock);
            CrashReporter.Install();

            LogAssert.Expect(LogType.Exception, new System.Text.RegularExpressions.Regex("boom"));
            Debug.LogException(new System.InvalidOperationException("boom"));

            Assert.AreEqual(1, mock.ManagedExceptions.Count);
            StringAssert.Contains("boom", mock.ManagedExceptions[0].Message);
        }

        // ── LocalFileCrashBackend 落盘 ──────────────────────────────────────

        [Test]
        public void LocalBackend_RecordsExceptionWithAttribution()
        {
            string dir = TempDir();
            var backend = new LocalFileCrashBackend();
            backend.Install(new CrashSessionInfo("1.2.3", "editor", dir, "dev_x"));

            backend.SetUser("u_7");
            backend.SetCustomKey("stage", "battle");
            backend.LeaveBreadcrumb("tap_start");
            backend.RecordManagedException(
                new ManagedExceptionInfo(1700000000, "NRE msg", "at Foo()", LogType.Exception));

            string content = File.ReadAllText(Path.Combine(dir, "crash_reports.jsonl"));
            StringAssert.Contains("NRE msg", content);
            StringAssert.Contains("u_7", content);
            StringAssert.Contains("battle", content);
            StringAssert.Contains("tap_start", content);
            StringAssert.Contains("1.2.3", content);

            Directory.Delete(dir, true);
        }

        [Test]
        public void LocalBackend_CapsRecordsPerSession()
        {
            string dir = TempDir();
            var backend = new LocalFileCrashBackend();
            backend.Install(new CrashSessionInfo("1.0", "editor", dir, "d"));

            for (int i = 0; i < 60; i++)
                backend.RecordManagedException(
                    new ManagedExceptionInfo(1700000000 + i, "e" + i, "stack", LogType.Exception));

            string[] lines = File.ReadAllLines(Path.Combine(dir, "crash_reports.jsonl"));
            Assert.AreEqual(50, lines.Length, "单会话应至多落盘 50 条");

            Directory.Delete(dir, true);
        }

        [Test]
        public void LocalBackend_NoUploadUrl_ReturnsFalse()
        {
            string dir = TempDir();
            var backend = new LocalFileCrashBackend();
            backend.Install(new CrashSessionInfo("1.0", "editor", dir, "d"));
            backend.RecordManagedException(
                new ManagedExceptionInfo(1700000000, "e", "s", LogType.Exception));

            // 无 AppConfig / 空 CrashReportUrl：不上报，返回 false，保留本地文件。
            bool ok = backend.TryFlushPendingAsync().GetAwaiter().GetResult();
            Assert.IsFalse(ok);
            Assert.IsTrue(File.Exists(Path.Combine(dir, "crash_reports.jsonl")));

            Directory.Delete(dir, true);
        }

        // ── MockCrashBackend 透传 ───────────────────────────────────────────

        [Test]
        public void MockBackend_RecordsAllCalls()
        {
            var mock = new MockCrashBackend();
            mock.Install(new CrashSessionInfo("1", "editor", "/tmp", "d"));
            mock.SetUser("u");
            mock.SetCustomKey("k", "v");
            mock.LeaveBreadcrumb("b");
            mock.RecordManagedException(new ManagedExceptionInfo(1, "m", "s", LogType.Exception));

            Assert.IsTrue(mock.Installed);
            Assert.AreEqual("u", mock.UserId);
            Assert.AreEqual("v", mock.CustomKeys["k"]);
            Assert.AreEqual(1, mock.Breadcrumbs.Count);
            Assert.AreEqual(1, mock.ManagedExceptions.Count);
        }

        private static string TempDir()
        {
            string dir = Path.Combine(Application.temporaryCachePath, "CrashTest_" + Path.GetRandomFileName());
            Directory.CreateDirectory(dir);
            return dir;
        }
    }
}
