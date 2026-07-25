using System.Collections.Generic;
using Framework;
using NUnit.Framework;

namespace Framework.Tests
{
    /// <summary>
    /// general 配置文本值解析器单测：可空解包、空值默认、多写法 bool、枚举、数组/List 递归、Parse/ChangeType 回退、
    /// 集合文本拆分。纯逻辑，不碰数据库——ConfigManager 类型强制逻辑拆分后的回归基线。
    /// </summary>
    public class GeneralConfigValueParserTests
    {
        private enum Fruit { Apple, Banana, Cherry }

        [Test]
        public void 字符串原样返回()
        {
            Assert.AreEqual("hello", GeneralConfigValueParser.Parse("hello", typeof(string)));
        }

        [TestCase("true")]
        [TestCase("True")]
        [TestCase("1")]
        [TestCase("yes")]
        [TestCase("y")]
        [TestCase("是")]
        public void 多种写法解析为真(string text)
        {
            Assert.AreEqual(true, GeneralConfigValueParser.Parse(text, typeof(bool)));
        }

        [TestCase("false")]
        [TestCase("0")]
        [TestCase("no")]
        [TestCase("否")]
        public void 其余写法解析为假(string text)
        {
            Assert.AreEqual(false, GeneralConfigValueParser.Parse(text, typeof(bool)));
        }

        [Test]
        public void 整数与浮点数按不变文化解析()
        {
            Assert.AreEqual(42, GeneralConfigValueParser.Parse("42", typeof(int)));
            Assert.AreEqual(3.5f, GeneralConfigValueParser.Parse("3.5", typeof(float)));
        }

        [Test]
        public void 枚举忽略大小写解析()
        {
            Assert.AreEqual(Fruit.Banana, GeneralConfigValueParser.Parse("banana", typeof(Fruit)));
        }

        [Test]
        public void 可空值类型_空值返回底层默认值_有值解包()
        {
            // 可空类型先解包为底层类型：int? 的底层 int 是值类型，空值返回 default(int)=0（非 null，与原实现一致）
            Assert.AreEqual(0, GeneralConfigValueParser.Parse("", typeof(int?)));
            Assert.AreEqual(7, GeneralConfigValueParser.Parse("7", typeof(int?)));
        }

        [Test]
        public void 空白值_值类型返回默认_引用类型返回null()
        {
            Assert.AreEqual(0, GeneralConfigValueParser.Parse("   ", typeof(int)));
            Assert.IsNull(GeneralConfigValueParser.Parse(null, typeof(string)));
        }

        [Test]
        public void 数组_带方括号逗号分隔_递归解析元素()
        {
            var result = (int[])GeneralConfigValueParser.Parse("[1,2,3]", typeof(int[]));
            CollectionAssert.AreEqual(new[] { 1, 2, 3 }, result);
        }

        [Test]
        public void List_分号分隔_递归解析元素()
        {
            var result = (List<int>)GeneralConfigValueParser.Parse("1;2;3", typeof(List<int>));
            CollectionAssert.AreEqual(new List<int> { 1, 2, 3 }, result);
        }

        [Test]
        public void 拆分集合_去方括号_逗号或分号_丢空段()
        {
            // 注意：只 trim 整体、不 trim 单个元素（与原实现一致）
            CollectionAssert.AreEqual(new[] { "a", "b", "c" }, GeneralConfigValueParser.SplitCollection("[a,b,c]"));
            CollectionAssert.AreEqual(new[] { "1", "2" }, GeneralConfigValueParser.SplitCollection("1;;2"));
            CollectionAssert.AreEqual(new string[0], GeneralConfigValueParser.SplitCollection("  "));
        }
    }
}
