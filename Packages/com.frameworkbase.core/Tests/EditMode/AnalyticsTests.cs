using System.Collections.Generic;
using Cysharp.Threading.Tasks;
using Framework.Analytics;
using NUnit.Framework;
using UnityEngine.TestTools;

namespace Framework.Tests
{
    /// <summary>
    /// 埋点管道单元测试：信封序列化、批量冲刷、失败保留重试、溢出丢弃补报。
    /// FakeBackend 同步完成；注意 Track 在队列达批量阈值时会同步自动 flush，
    /// 需要累积队列的用例先把后端置为失败态（AlwaysFail）。
    /// </summary>
    public class AnalyticsTests
    {
        private AnalyticsManager _analytics;
        private FakeBackend _backend;

        [SetUp]
        public void SetUp()
        {
            LogAssert.ignoreFailingMessages = true;
            _analytics = new AnalyticsManager();
            _analytics.OnInit();
            _analytics.ClearQueue(); // 清掉可能存在的历史落盘补报，保证用例计数确定
            _backend = new FakeBackend();
            _analytics.SetBackend(_backend);
        }

        [TearDown]
        public void TearDown()
        {
            _analytics.ClearQueue(); // 不让用例事件落盘污染下次运行
            _analytics.OnShutdown();
            LogAssert.ignoreFailingMessages = false;
        }

        private static T Wait<T>(UniTask<T> task) => task.GetAwaiter().GetResult();

        // ── JSON 信封 ────────────────────────────────────────────────────────

        [Test]
        public void 信封序列化_固定字段与属性齐全()
        {
            string json = AnalyticsJson.SerializeEvent(
                "login", 1720000000000, "s1", "d1", "u1", "1.0", "mock",
                new Dictionary<string, object>
                {
                    { "attempt", 3 },
                    { "auto", true },
                    { "ratio", 0.5f },
                    { "note", "he said \"hi\"\nline2" }
                });

            StringAssert.Contains("\"event\":\"login\"", json);
            StringAssert.Contains("\"ts\":1720000000000", json);
            StringAssert.Contains("\"session_id\":\"s1\"", json);
            StringAssert.Contains("\"user_id\":\"u1\"", json);
            StringAssert.Contains("\"channel\":\"mock\"", json);
            StringAssert.Contains("\"attempt\":3", json);
            StringAssert.Contains("\"auto\":true", json);
            StringAssert.Contains("\"ratio\":0.5", json);
            StringAssert.Contains("\\\"hi\\\"", json, "双引号必须转义");
            StringAssert.Contains("\\n", json, "换行必须转义");
        }

        [Test]
        public void 信封序列化_无属性时不输出props()
        {
            string json = AnalyticsJson.SerializeEvent(
                "boot", 1, "s", "d", "", "1.0", "", null);
            StringAssert.DoesNotContain("props", json);
        }

        // ── 管道行为 ─────────────────────────────────────────────────────────

        [Test]
        public void Track后Flush_事件送达后端并出队()
        {
            _analytics.Track("e1");
            _analytics.Track("e2", new Dictionary<string, object> { { "k", 1 } });
            Assert.AreEqual(2, _analytics.QueuedCount);

            Assert.IsTrue(Wait(_analytics.FlushAsync()));

            Assert.AreEqual(0, _analytics.QueuedCount);
            Assert.AreEqual(1, _backend.Batches.Count);
            Assert.AreEqual(2, _backend.Batches[0].Count);
            StringAssert.Contains("\"event\":\"e1\"", _backend.Batches[0][0]);
        }

        [Test]
        public void 后端失败_事件保留并可重试成功()
        {
            _backend.AlwaysFail = true;
            _analytics.Track("e1");

            Assert.IsFalse(Wait(_analytics.FlushAsync()));
            Assert.AreEqual(1, _analytics.QueuedCount, "失败批次必须保留");

            _backend.AlwaysFail = false;
            Assert.IsTrue(Wait(_analytics.FlushAsync()), "后端恢复后重发成功");
            Assert.AreEqual(0, _analytics.QueuedCount);
            Assert.GreaterOrEqual(_backend.Batches.Count, 2, "同一事件应被发送至少两次（重试）");
        }

        [Test]
        public void 设置用户后_事件带用户维度()
        {
            _analytics.SetUserId("user_42");
            _analytics.Track("purchase");
            Wait(_analytics.FlushAsync());

            StringAssert.Contains("\"user_id\":\"user_42\"", _backend.Batches[0][0]);
        }

        [Test]
        public void 单批上限50_大队列分批出()
        {
            _backend.AlwaysFail = true; // 阻止自动冲刷，累积队列
            for (int i = 0; i < 120; i++)
                _analytics.Track($"e{i}");
            Assert.AreEqual(120, _analytics.QueuedCount);

            _backend.AlwaysFail = false;
            _backend.Batches.Clear();
            Wait(_analytics.FlushAsync());

            Assert.AreEqual(50, _backend.Batches[0].Count, "单批不超过 50");
            Assert.AreEqual(70, _analytics.QueuedCount);
        }

        [Test]
        public void 队列溢出_丢最旧并随后补报丢弃计数()
        {
            _backend.AlwaysFail = true; // 阻止自动冲刷，制造溢出
            for (int i = 0; i < 520; i++)
                _analytics.Track($"e{i}");
            Assert.LessOrEqual(_analytics.QueuedCount, 500, "队列必须封顶");

            _backend.AlwaysFail = false;
            _backend.Batches.Clear();
            _analytics.Track("after_drop"); // 触发 analytics_dropped 补报入队

            // 冲刷全队列，检查补报事件存在
            int guard = 0;
            while (_analytics.QueuedCount > 0 && guard++ < 30)
                Wait(_analytics.FlushAsync());

            bool foundDropReport = false;
            foreach (var batch in _backend.Batches)
                foreach (string json in batch)
                    if (json.Contains("\"event\":\"analytics_dropped\""))
                        foundDropReport = true;

            Assert.IsTrue(foundDropReport, "溢出丢弃必须以 analytics_dropped 事件补报");
        }

        // ── 假后端 ───────────────────────────────────────────────────────────

        private sealed class FakeBackend : IAnalyticsBackend
        {
            public readonly List<List<string>> Batches = new List<List<string>>();

            /// <summary>为 true 时所有批次发送失败（事件保留在管道内）。</summary>
            public bool AlwaysFail;

            public string Name => "fake";

            public UniTask<bool> SendAsync(IReadOnlyList<string> eventJsonBatch)
            {
                Batches.Add(new List<string>(eventJsonBatch));
                return UniTask.FromResult(!AlwaysFail);
            }
        }
    }
}
