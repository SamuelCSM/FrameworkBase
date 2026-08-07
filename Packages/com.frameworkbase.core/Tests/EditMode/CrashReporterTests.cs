using System.IO;
using System.Reflection;
using System.Text;
using Cysharp.Threading.Tasks;
using Framework.Core;
using Framework.Core.Telemetry;
using Framework.Http;
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
        private IHttpClient _originalHttpClient;

        [SetUp]
        public void SetUp()
        {
            _originalHttpClient = HttpClients.Shared;
        }

        [TearDown]
        public void TearDown()
        {
            CrashReporter.Shutdown();
            // 上报用例会替换全局 HTTP 客户端与 AppConfig 缓存，必须还原，否则污染同批其它用例。
            HttpClients.Shared = _originalHttpClient;
            AppConfig.ClearCache();
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

        // ── 上报轮转（快照隔离在途批次）──────────────────────────────────────

        [Test]
        public void LocalBackend_上报期间新增的崩溃记录不被清理带走()
        {
            string dir = TempDir();
            var backend = new LocalFileCrashBackend();
            backend.Install(new CrashSessionInfo("1.0", "editor", dir, "d"));
            backend.RecordManagedException(new ManagedExceptionInfo(1700000000, "old_1", "s", LogType.Exception));

            UseUploadUrl();
            // 上报进行到一半时又崩了一条：它写进轮转后重建的活动文件，不属于本批。
            var http = new FakeHttpClient(succeed: true, duringSend: () =>
                backend.RecordManagedException(
                    new ManagedExceptionInfo(1700000002, "during_upload", "s", LogType.Exception)));
            HttpClients.Shared = http;

            bool ok = backend.TryFlushPendingAsync().GetAwaiter().GetResult();

            Assert.IsTrue(ok);
            StringAssert.Contains("old_1", http.LastBody);
            StringAssert.DoesNotContain("during_upload", http.LastBody, "在途期间新增的记录不应混进本批");

            // 删整个活动文件会把 during_upload 一起删掉——这正是本轮要守住的地方。
            string active = Path.Combine(dir, LocalFileName);
            Assert.IsTrue(File.Exists(active), "上报期间新增的记录必须留在盘上");
            string remaining = File.ReadAllText(active);
            StringAssert.Contains("during_upload", remaining);
            StringAssert.DoesNotContain("old_1", remaining, "已上报成功的记录不应残留");
            Assert.IsFalse(File.Exists(Path.Combine(dir, SnapshotFileName)), "上报成功后快照应删除");

            Directory.Delete(dir, true);
        }

        [Test]
        public void LocalBackend_上报失败保留快照_下次连同新记录一起重试()
        {
            string dir = TempDir();
            var backend = new LocalFileCrashBackend();
            backend.Install(new CrashSessionInfo("1.0", "editor", dir, "d"));
            backend.RecordManagedException(new ManagedExceptionInfo(1700000000, "old_1", "s", LogType.Exception));

            UseUploadUrl();
            HttpClients.Shared = new FakeHttpClient(succeed: false);
            Assert.IsFalse(backend.TryFlushPendingAsync().GetAwaiter().GetResult());
            Assert.IsTrue(File.Exists(Path.Combine(dir, SnapshotFileName)), "失败时快照必须保留待重试");

            backend.RecordManagedException(new ManagedExceptionInfo(1700000003, "after_fail", "s", LogType.Exception));

            var retry = new FakeHttpClient(succeed: true);
            HttpClients.Shared = retry;
            Assert.IsTrue(backend.TryFlushPendingAsync().GetAwaiter().GetResult());

            // 重试时快照与新记录合并成一批，顺序保持"旧的在前"。
            StringAssert.Contains("old_1", retry.LastBody);
            StringAssert.Contains("after_fail", retry.LastBody);
            Assert.Less(retry.LastBody.IndexOf("old_1", System.StringComparison.Ordinal),
                retry.LastBody.IndexOf("after_fail", System.StringComparison.Ordinal),
                "合并后应保持时间顺序");
            Assert.IsFalse(File.Exists(Path.Combine(dir, SnapshotFileName)));
            Assert.IsFalse(File.Exists(Path.Combine(dir, LocalFileName)));

            Directory.Delete(dir, true);
        }

        private const string LocalFileName = "crash_reports.jsonl";
        private const string SnapshotFileName = LocalFileName + ".uploading";

        /// <summary>注入一个非空 CrashReportUrl，让上报路径真正跑起来（默认工程配置里它是空的）。</summary>
        private static void UseUploadUrl()
        {
            AppConfigAsset config = ScriptableObject.CreateInstance<AppConfigAsset>();
            config.CrashReportUrl = "https://crash.invalid/report";
            typeof(AppConfig)
                .GetField("_cached", BindingFlags.Static | BindingFlags.NonPublic)
                ?.SetValue(null, config);
        }

        /// <summary>可控成败的假 HTTP 客户端；<paramref name="duringSend"/> 用于确定性模拟"上报在途"发生的事。</summary>
        private sealed class FakeHttpClient : IHttpClient
        {
            private readonly bool _succeed;
            private readonly System.Action _duringSend;

            public FakeHttpClient(bool succeed, System.Action duringSend = null)
            {
                _succeed = succeed;
                _duringSend = duringSend;
            }

            /// <summary>最近一次请求的正文文本（JSON Lines）。</summary>
            public string LastBody { get; private set; } = string.Empty;

            public UniTask<HttpResponse> SendAsync(HttpRequest request)
            {
                LastBody = request.Body != null ? Encoding.UTF8.GetString(request.Body) : string.Empty;
                _duringSend?.Invoke();
                return UniTask.FromResult(_succeed
                    ? new HttpResponse(200, null, null)
                    : HttpResponse.Failed("fake upload failure"));
            }
        }

        private static string TempDir()
        {
            string dir = Path.Combine(Application.temporaryCachePath, "CrashTest_" + Path.GetRandomFileName());
            Directory.CreateDirectory(dir);
            return dir;
        }
    }
}
