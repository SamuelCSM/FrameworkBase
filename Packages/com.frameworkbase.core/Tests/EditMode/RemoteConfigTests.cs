using System.Collections.Generic;
using Cysharp.Threading.Tasks;
using Framework.HotUpdate;
using Framework.RemoteConfig;
using NUnit.Framework;

namespace Framework.Tests
{
    /// <summary>
    /// 远程配置单元测试：JSON 解析、三层取值回退、失败保留现值、磁盘缓存、
    /// 功能开关判定（布尔/条件对象/灰度分桶）、version.json 灰度放量、稳定哈希。
    /// </summary>
    public class RemoteConfigTests
    {
        private RemoteConfigManager _config;
        private FakeBackend _backend;

        [SetUp]
        public void SetUp()
        {
            _config = new RemoteConfigManager();
            _config.OnInit();
            _config.ClearCache(); // 清掉可能存在的历史磁盘缓存，保证用例初态确定
            _backend = new FakeBackend();
            _config.SetBackend(_backend);
        }

        [TearDown]
        public void TearDown()
        {
            _config.ClearCache(); // 不让用例缓存污染下次运行
            _config.OnShutdown();
        }

        private static T Wait<T>(UniTask<T> task) => task.GetAwaiter().GetResult();

        // ── JSON 解析 ────────────────────────────────────────────────────────

        [Test]
        public void Json解析_标量嵌套与数组()
        {
            string json = "{ \"s\":\"hi\", \"i\":42, \"f\":0.5, \"neg\":-3, \"b\":true, \"n\":null, " +
                          "\"o\":{\"k\":\"v\"}, \"a\":[1,\"x\",false] }";

            Assert.IsTrue(RemoteConfigJson.TryParseObject(json, out var result));
            Assert.AreEqual("hi", result["s"]);
            Assert.AreEqual(42L, result["i"]);
            Assert.AreEqual(0.5, (double)result["f"], 1e-9);
            Assert.AreEqual(-3L, result["neg"]);
            Assert.AreEqual(true, result["b"]);
            Assert.IsNull(result["n"]);

            var nested = result["o"] as Dictionary<string, object>;
            Assert.IsNotNull(nested);
            Assert.AreEqual("v", nested["k"]);

            var array = result["a"] as List<object>;
            Assert.IsNotNull(array);
            Assert.AreEqual(3, array.Count);
            Assert.AreEqual(1L, array[0]);
            Assert.AreEqual(false, array[2]);
        }

        [Test]
        public void Json解析_字符串转义与Unicode()
        {
            string json = "{\"k\":\"a\\\"b\\\\c\\nd\\u0041\"}";
            Assert.IsTrue(RemoteConfigJson.TryParseObject(json, out var result));
            Assert.AreEqual("a\"b\\c\nd" + "A", result["k"]);
        }

        [Test]
        public void Json解析_非法输入一律拒绝()
        {
            string[] badInputs = { null, "", "not json", "{", "{}x", "[1,2]", "{\"a\":}", "{\"a\" \"b\"}" };
            foreach (string bad in badInputs)
                Assert.IsFalse(RemoteConfigJson.TryParseObject(bad, out _), $"应拒绝: {bad ?? "(null)"}");
        }

        // ── 取值回退 ─────────────────────────────────────────────────────────

        [Test]
        public void 默认值_未拉取时生效()
        {
            _config.SetDefaults(new Dictionary<string, object>
            {
                { "speed", 10 }, { "name", "x" }, { "on", true }
            });

            Assert.AreEqual(10, _config.GetInt("speed"));
            Assert.AreEqual("x", _config.GetString("name"));
            Assert.IsTrue(_config.GetBool("on"));
            Assert.AreEqual(99, _config.GetInt("missing", 99), "无值键回退兜底参数");
            Assert.IsFalse(_config.HasKey("missing"));
        }

        [Test]
        public void 拉取激活_远端覆盖默认值()
        {
            _config.SetDefaults(new Dictionary<string, object> { { "speed", 10 }, { "local_only", "keep" } });
            _backend.Payload = "{\"speed\":20,\"extra\":\"y\"}";

            Assert.IsTrue(Wait(_config.FetchAndActivateAsync()));
            Assert.IsTrue(_config.FetchedThisSession);
            Assert.AreEqual(20, _config.GetInt("speed"), "远端值覆盖默认值");
            Assert.AreEqual("y", _config.GetString("extra"));
            Assert.AreEqual("keep", _config.GetString("local_only"), "远端没有的键回退默认值");
        }

        [Test]
        public void 拉取失败_保留现值并返回false()
        {
            _backend.Payload = "{\"speed\":20}";
            Assert.IsTrue(Wait(_config.FetchAndActivateAsync()));

            _backend.Payload = null; // 网络失败
            Assert.IsFalse(Wait(_config.FetchAndActivateAsync()));
            Assert.AreEqual(20, _config.GetInt("speed"), "失败必须保留上次激活值");

            _backend.Payload = "garbage not json"; // 解析失败
            Assert.IsFalse(Wait(_config.FetchAndActivateAsync()));
            Assert.AreEqual(20, _config.GetInt("speed"), "解析失败同样保留现值");
        }

        [Test]
        public void 类型化取值_宽容转换()
        {
            _backend.Payload = "{\"i\":\"42\",\"f\":1,\"b\":\"true\",\"s\":7,\"d\":2.5}";
            Wait(_config.FetchAndActivateAsync());

            Assert.AreEqual(42, _config.GetInt("i"), "数字文本转 int");
            Assert.AreEqual(1f, _config.GetFloat("f"), 1e-6f, "整数转 float");
            Assert.IsTrue(_config.GetBool("b"), "布尔文本转 bool");
            Assert.AreEqual("7", _config.GetString("s"), "数字转字符串");
            Assert.AreEqual(2L, _config.GetLong("d"), "小数截断转 long");
        }

        [Test]
        public void 磁盘缓存_下次启动生效()
        {
            _backend.Payload = "{\"cached_key\":\"cached_value\"}";
            Assert.IsTrue(Wait(_config.FetchAndActivateAsync()));

            // 模拟下次启动：新实例 OnInit 应从磁盘缓存加载 last-known-good
            var next = new RemoteConfigManager();
            next.OnInit();
            Assert.IsTrue(next.HasRemoteValues, "缓存应在下次启动加载");
            Assert.IsFalse(next.FetchedThisSession, "缓存值不算本会话拉取");
            Assert.AreEqual("cached_value", next.GetString("cached_key"));
            next.ClearCache();
        }

        // ── 功能开关 ─────────────────────────────────────────────────────────

        [Test]
        public void 功能开关_布尔直读与enabled过滤()
        {
            _backend.Payload = "{\"flag_on\":true,\"flag_off\":false," +
                               "\"flag_disabled\":{\"enabled\":false,\"rollout\":100}}";
            Wait(_config.FetchAndActivateAsync());

            Assert.IsTrue(_config.IsFeatureEnabled("flag_on"));
            Assert.IsFalse(_config.IsFeatureEnabled("flag_off"));
            Assert.IsFalse(_config.IsFeatureEnabled("flag_disabled"), "enabled=false 优先于 rollout");
            Assert.IsFalse(_config.IsFeatureEnabled("flag_missing"));
            Assert.IsTrue(_config.IsFeatureEnabled("flag_missing", defaultValue: true), "无值键回退兜底参数");
        }

        [Test]
        public void 功能开关_rollout边界与确定性()
        {
            _backend.Payload = "{\"f0\":{\"rollout\":0},\"f100\":{\"rollout\":100},\"f50\":{\"rollout\":50}}";
            Wait(_config.FetchAndActivateAsync());

            Assert.IsFalse(_config.IsFeatureEnabled("f0"), "rollout=0 任何设备都不命中");
            Assert.IsTrue(_config.IsFeatureEnabled("f100"), "rollout=100 全量命中");

            bool first = _config.IsFeatureEnabled("f50");
            Assert.AreEqual(first, _config.IsFeatureEnabled("f50"), "同设备同键判定必须稳定");
        }

        [Test]
        public void 功能开关_min_version门控()
        {
            _backend.Payload = "{\"too_new\":{\"rollout\":100,\"min_version\":\"999.0.0\"}," +
                               "\"old_enough\":{\"rollout\":100,\"min_version\":\"0.0.1\"}}";
            Wait(_config.FetchAndActivateAsync());

            Assert.IsFalse(_config.IsFeatureEnabled("too_new"), "当前版本低于 min_version 必须关");
            Assert.IsTrue(_config.IsFeatureEnabled("old_enough"));
        }

        // ── version.json 灰度放量 ────────────────────────────────────────────

        [Test]
        public void 灰度放量_全量与边界()
        {
            Assert.IsTrue(VersionManager.IsDeviceInGrayRollout(null, "d1"), "无服务端版本视为放行");
            Assert.IsTrue(VersionManager.IsDeviceInGrayRollout(MakeVersion(0), "d1"), "0=全量（缺省）");
            Assert.IsTrue(VersionManager.IsDeviceInGrayRollout(MakeVersion(100), "d1"), "100=全量");
            Assert.IsTrue(VersionManager.IsDeviceInGrayRollout(MakeVersion(150), "d1"), "越界按全量");
        }

        [Test]
        public void 灰度放量_确定性与放量单调扩大()
        {
            var v = MakeVersion(50);
            int hit = 0, miss = 0;

            for (int i = 0; i < 200; i++)
            {
                string deviceId = $"device_{i}";
                bool result = VersionManager.IsDeviceInGrayRollout(v, deviceId);
                Assert.AreEqual(result, VersionManager.IsDeviceInGrayRollout(v, deviceId), "同设备判定必须稳定");
                if (result) hit++; else miss++;

                // 单调性：低百分比命中的设备，高百分比必须仍命中（放量只进不出）
                if (VersionManager.IsDeviceInGrayRollout(MakeVersion(5), deviceId))
                    Assert.IsTrue(VersionManager.IsDeviceInGrayRollout(MakeVersion(60), deviceId),
                        "5% 命中的设备在 60% 时不得掉出");
            }

            Assert.Greater(hit, 0, "50% 放量下 200 台设备应有命中");
            Assert.Greater(miss, 0, "50% 放量下 200 台设备应有未命中");
        }

        // ── 稳定哈希 ─────────────────────────────────────────────────────────

        [Test]
        public void 稳定哈希_确定性与桶值域()
        {
            string[] inputs = { "", "a", "device:key", "设备号:功能名", "long-input-" + new string('x', 500) };
            foreach (string input in inputs)
            {
                int bucket = StableHash.Bucket(input);
                Assert.AreEqual(bucket, StableHash.Bucket(input), "同输入必须同桶");
                Assert.GreaterOrEqual(bucket, 0);
                Assert.Less(bucket, 100);
            }
        }

        // ── 工具 ─────────────────────────────────────────────────────────────

        private static UpdateInfo MakeVersion(int grayPercent)
        {
            return new UpdateInfo
            {
                AppVersion = "1.0.0",
                ResourceVersion = 2,
                CodeVersion = 3,
                GrayPercent = grayPercent
            };
        }

        // ── 假后端 ───────────────────────────────────────────────────────────

        private sealed class FakeBackend : IRemoteConfigBackend
        {
            /// <summary>要返回的配置 JSON；null 模拟网络失败。</summary>
            public string Payload;

            public string Name => "fake";

            public UniTask<string> FetchAsync(RemoteConfigRequest request)
            {
                return UniTask.FromResult(Payload);
            }
        }
    }
}
