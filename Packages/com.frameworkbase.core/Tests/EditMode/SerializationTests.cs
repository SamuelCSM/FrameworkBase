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
