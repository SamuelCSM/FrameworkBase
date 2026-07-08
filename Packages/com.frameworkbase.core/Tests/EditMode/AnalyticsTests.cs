using System.Collections.Generic;
using Cysharp.Threading.Tasks;
using Framework.Analytics;
using NUnit.Framework;

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
            AnalyticsSchemaRegistry.Shared = AnalyticsSchemaRegistry.CreateWithFrameworkEvents();
            RegisterTestEvents("e1", "e2", "purchase", "after_drop");
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
            AnalyticsSchemaRegistry.Shared = AnalyticsSchemaRegistry.CreateWithFrameworkEvents();
        }

        private static T Wait<T>(UniTask<T> task) => task.GetAwaiter().GetResult();

        private static void RegisterTestEvents(params string[] eventNames)
        {
            foreach (string eventName in eventNames)
                AnalyticsSchemaRegistry.Shared.Register(new AnalyticsEventSchema(eventName));
        }

        private static void RegisterEventRange(string prefix, int count)
        {
            for (int i = 0; i < count; i++)
                AnalyticsSchemaRegistry.Shared.Register(new AnalyticsEventSchema($"{prefix}{i}"));
        }

        /// <summary>从事件 JSON 中取出 "field":"value" 的 value（测试用，仅支持字符串字段）。</summary>
        private static string ExtractField(string json, string field)
        {
            string token = $"\"{field}\":\"";
            int start = json.IndexOf(token, System.StringComparison.Ordinal);
            if (start < 0) return null;
            start += token.Length;
            int end = json.IndexOf('"', start);
            return end < 0 ? null : json.Substring(start, end - start);
        }

        // ── JSON 信封 ────────────────────────────────────────────────────────

        [Test]
        public void 信封序列化_固定字段与属性齐全()
        {
            string json = AnalyticsJson.SerializeEvent(
                "evt_abc", "login", 1720000000000, "s1", "d1", "u1", "1.0", "mock",
                new Dictionary<string, object>
                {
                    { "attempt", 3 },
                    { "auto", true },
                    { "ratio", 0.5f },
                    { "note", "he said \"hi\"\nline2" }
                });

            StringAssert.Contains("\"event_id\":\"evt_abc\"", json);
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
                "evt_x", "boot", 1, "s", "d", "", "1.0", "", null);
            StringAssert.DoesNotContain("props", json);
        }

        [Test]
        public void 每条事件_event_id唯一()
        {
            RegisterEventRange("e", 200);
            for (int i = 0; i < 200; i++)
                _analytics.Track($"e{i}");
            Wait(_analytics.FlushAsync());

            var ids = new HashSet<string>();
            foreach (var batch in _backend.Batches)
                foreach (string json in batch)
                    ids.Add(ExtractField(json, "event_id"));

            Assert.AreEqual(200, ids.Count, "每条事件的 event_id 必须唯一（去重锚点）");
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
        public void 排水式冲刷_一次发完积压且单批不超50()
        {
            RegisterEventRange("e", 120);
            _backend.AlwaysFail = true; // 阻止自动冲刷，累积队列
            for (int i = 0; i < 120; i++)
                _analytics.Track($"e{i}");
            Assert.AreEqual(120, _analytics.QueuedCount);

            _backend.AlwaysFail = false;
            _backend.Batches.Clear();
            Wait(_analytics.FlushAsync());

            Assert.AreEqual(3, _backend.Batches.Count, "120 条应分 50+50+20 三批");
            Assert.AreEqual(50, _backend.Batches[0].Count, "单批不超过 50");
            Assert.AreEqual(20, _backend.Batches[2].Count);
            Assert.AreEqual(0, _analytics.QueuedCount, "排水循环应一次触发发完整个队列");
        }

        [Test]
        public void 单次排水有上限_不会无限连发()
        {
            RegisterEventRange("e", 1050);
            _backend.AlwaysFail = true;
            // 21 批 = 1050 条，但队列上限 500，实际约 10 批即可排空——
            // 用远超上限的量确认循环有 MaxBatchesPerFlush 保护（不因巨量积压卡死一次调用）。
            for (int i = 0; i < 1050; i++)
                _analytics.Track($"e{i}");

            _backend.AlwaysFail = false;
            _backend.Batches.Clear();
            Wait(_analytics.FlushAsync());

            Assert.LessOrEqual(_backend.Batches.Count, 20, "单次冲刷批次数不得超过 MaxBatchesPerFlush");
        }

        [Test]
        public void 队列排空后_删除落盘快照()
        {
            // 造一次落盘（模拟切后台），确认发完后文件被清掉，避免重启重复补报
            _analytics.Track("e1");
            _analytics.OnApplicationPause(true); // 内部先落盘再尽力发一批
            Wait(_analytics.FlushAsync());        // 排空队列 → 应删除 pending 文件

            string pendingPath = System.IO.Path.Combine(
                UnityEngine.Application.persistentDataPath, "analytics_pending.jsonl");
            Assert.AreEqual(0, _analytics.QueuedCount);
            Assert.IsFalse(System.IO.File.Exists(pendingPath), "队列排空后落盘快照应被删除");
        }

        [Test]
        public void 队列溢出_丢最旧并随后补报丢弃计数()
        {
            RegisterEventRange("e", 520);
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
