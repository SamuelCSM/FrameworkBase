using System.Collections.Generic;
using Framework.Serialization;
using NUnit.Framework;

namespace Framework.Tests
{
    public class SerializationTests
    {
        [Test]
        public void JsonObjectParser_ParsesNestedObjectAndArray()
        {
            string json = "{\"name\":\"demo\",\"count\":2,\"flags\":[true,false],\"meta\":{\"ratio\":0.5}}";

            Assert.IsTrue(JsonObjectParser.TryParseObject(json, out Dictionary<string, object> result));
            Assert.AreEqual("demo", result["name"]);
            Assert.AreEqual(2L, result["count"]);

            var flags = result["flags"] as List<object>;
            Assert.IsNotNull(flags);
            Assert.AreEqual(true, flags[0]);

            var meta = result["meta"] as Dictionary<string, object>;
            Assert.IsNotNull(meta);
            Assert.AreEqual(0.5, (double)meta["ratio"], 1e-9);
        }

        [Test]
        public void JsonObjectParser_接受上限内的嵌套深度()
        {
            // 32 层是解析器的硬上限，边界值本身必须仍然可解析。
            Assert.IsTrue(JsonObjectParser.TryParseObject(NestedObjectJson(32), out Dictionary<string, object> result));
            Assert.IsNotNull(result);
        }

        [Test]
        public void JsonObjectParser_超过深度上限一律拒绝()
        {
            // 递归下降在深嵌套上会耗尽调用栈，而 StackOverflowException 捕获不了、直接杀进程。
            // 因此深度必须在解析器内部就被挡住——用例刻意只测"略超上限"，不构造真能爆栈的输入：
            // 真爆栈会连测试运行器一起带走，无法作为可重复的回归。
            Assert.IsFalse(JsonObjectParser.TryParseObject(NestedObjectJson(33), out _), "对象嵌套超限须拒绝");
            Assert.IsFalse(JsonObjectParser.TryParseObject(NestedArrayJson(64), out _), "数组嵌套超限须拒绝");
        }

        /// <summary>构造 <paramref name="depth"/> 层对象嵌套：<c>{"a":{"a":…{}}}</c>。</summary>
        private static string NestedObjectJson(int depth)
        {
            var sb = new System.Text.StringBuilder();
            for (int i = 0; i < depth - 1; i++)
                sb.Append("{\"a\":");
            sb.Append("{}");
            for (int i = 0; i < depth - 1; i++)
                sb.Append('}');
            return sb.ToString();
        }

        /// <summary>构造顶层对象内 <paramref name="depth"/> 层数组嵌套：<c>{"a":[[[…]]]}</c>。</summary>
        private static string NestedArrayJson(int depth)
        {
            var sb = new System.Text.StringBuilder("{\"a\":");
            sb.Append('[', depth);
            sb.Append(']', depth);
            sb.Append('}');
            return sb.ToString();
        }

        [Test]
        public void JsonWriter_SerializesDynamicValues()
        {
            string json = JsonWriter.SerializeObject(new Dictionary<string, object>
            {
                { "text", "a\"b\nc" },
                { "enabled", true },
                { "count", 3 },
                { "items", new object[] { 1, "x" } }
            });

            StringAssert.Contains("\"text\":\"a\\\"b\\nc\"", json);
            StringAssert.Contains("\"enabled\":true", json);
            StringAssert.Contains("\"count\":3", json);
            StringAssert.Contains("\"items\":[1,\"x\"]", json);
        }
    }
}
