using System;
using System.Reflection;
using Framework.Security;
using NUnit.Framework;

namespace Framework.Tests
{
    /// <summary>
    /// 反作弊值类型单测：混淆正确性（读写往返、隐式互转）、内存里确实不存明文、
    /// 直改混淆字段被校验和抓到。防护边界见 ANTICHEAT_GUIDE.md。
    /// </summary>
    public class AntiCheatValueTests
    {
        private int _tamperCount;
        private Action<string> _handler;

        [SetUp]
        public void SetUp()
        {
            _tamperCount = 0;
            _handler = _ => _tamperCount++;
            AntiCheat.TamperDetected += _handler;
        }

        [TearDown]
        public void TearDown()
        {
            AntiCheat.TamperDetected -= _handler; // 静态事件，必须退订防跨用例污染
        }

        [Test]
        public void 读写往返_三种类型()
        {
            AntiCheatInt i = 12345;
            AntiCheatLong l = long.MaxValue - 7;
            AntiCheatFloat f = 3.14159f;

            Assert.AreEqual(12345, (int)i);
            Assert.AreEqual(long.MaxValue - 7, (long)l);
            Assert.AreEqual(3.14159f, (float)f, 0f, "float 按位混淆应无损往返");
            Assert.AreEqual(0, _tamperCount, "正常读写不得误报篡改");
        }

        [Test]
        public void 负值与零_往返正确()
        {
            AntiCheatInt i = -98765;
            AntiCheatLong l = long.MinValue + 3;
            AntiCheatFloat f = -0.001f;
            AntiCheatInt zero = 0;

            Assert.AreEqual(-98765, (int)i);
            Assert.AreEqual(long.MinValue + 3, (long)l);
            Assert.AreEqual(-0.001f, (float)f, 0f);
            Assert.AreEqual(0, (int)zero, "显式赋 0 也应正常往返");
            Assert.AreEqual(0, _tamperCount);
        }

        [Test]
        public void 隐式互转_算术直接可用()
        {
            AntiCheatInt gold = 100;
            gold = gold + 50;
            gold = gold * 2;

            Assert.AreEqual(300, (int)gold);
            Assert.IsTrue(gold.Equals((AntiCheatInt)300));
            Assert.AreEqual("300", gold.ToString());
        }

        [Test]
        public void 内存不存明文_混淆字段与真值不同()
        {
            var value = new AntiCheatInt(12345);

            var field = typeof(AntiCheatInt).GetField("_obfuscated", BindingFlags.NonPublic | BindingFlags.Instance);
            int stored = (int)field.GetValue(value);

            Assert.AreNotEqual(12345, stored, "内存里存的必须是混淆值，否则搜内存直接锁定");
        }

        [Test]
        public void 直改混淆字段_读取时触发篡改回调()
        {
            object boxed = new AntiCheatInt(12345);

            var field = typeof(AntiCheatInt).GetField("_obfuscated", BindingFlags.NonPublic | BindingFlags.Instance);
            field.SetValue(boxed, (int)field.GetValue(boxed) ^ 0x1000); // 模拟内存改值

            int _ = ((AntiCheatInt)boxed).Value;
            Assert.AreEqual(1, _tamperCount, "校验和失配必须触发 TamperDetected");
        }

        [Test]
        public void 直改校验和_同样被抓()
        {
            object boxed = new AntiCheatLong(999999L);

            var field = typeof(AntiCheatLong).GetField("_checksum", BindingFlags.NonPublic | BindingFlags.Instance);
            field.SetValue(boxed, (int)field.GetValue(boxed) + 1);

            long _ = ((AntiCheatLong)boxed).Value;
            Assert.AreEqual(1, _tamperCount);
        }

        [Test]
        public void Default实例_读0不报警()
        {
            AntiCheatInt i = default;
            AntiCheatLong l = default;
            AntiCheatFloat f = default;

            Assert.AreEqual(0, (int)i);
            Assert.AreEqual(0L, (long)l);
            Assert.AreEqual(0f, (float)f);
            Assert.AreEqual(0, _tamperCount, "未初始化态是合法的零值，不得误报");
        }

        [Test]
        public void 实例密钥互不相同_同值混淆结果不同()
        {
            var a = new AntiCheatInt(777);
            var b = new AntiCheatInt(777);

            var field = typeof(AntiCheatInt).GetField("_obfuscated", BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.AreNotEqual((int)field.GetValue(a), (int)field.GetValue(b),
                "同值不同实例的混淆结果应不同，否则搜到一个等于搜到全部");
            Assert.IsTrue(a.Equals(b), "混淆态不同不影响值语义相等");
        }
    }
}
