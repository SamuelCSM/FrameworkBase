using NUnit.Framework;

namespace Framework.Tests
{
    /// <summary>
    /// 复数规则分类器单测：覆盖 6 大规则家族的类别边界、小数退化、未登记语言安全回退、key 后缀映射。
    /// </summary>
    public class PluralRulesTests
    {
        [Test]
        public void 东亚语言_恒为other()
        {
            Assert.AreEqual(PluralCategory.Other, PluralRules.Select("zh_cn", 0));
            Assert.AreEqual(PluralCategory.Other, PluralRules.Select("zh_cn", 1));
            Assert.AreEqual(PluralCategory.Other, PluralRules.Select("ja", 5));
            Assert.AreEqual(PluralCategory.Other, PluralRules.Select("ko", 100));
        }

        [Test]
        public void 英语_one与other()
        {
            Assert.AreEqual(PluralCategory.One, PluralRules.Select("en_us", 1));
            Assert.AreEqual(PluralCategory.Other, PluralRules.Select("en_us", 0));
            Assert.AreEqual(PluralCategory.Other, PluralRules.Select("en_us", 2));
            // 带小数：i==1 但 v!=0 → other
            Assert.AreEqual(PluralCategory.Other, PluralRules.Select("en_us", 1.5));
        }

        [Test]
        public void 法语_零和一都是one()
        {
            Assert.AreEqual(PluralCategory.One, PluralRules.Select("fr", 0));
            Assert.AreEqual(PluralCategory.One, PluralRules.Select("fr", 1));
            Assert.AreEqual(PluralCategory.Other, PluralRules.Select("fr", 2));
        }

        [Test]
        public void 俄语_one_few_many()
        {
            Assert.AreEqual(PluralCategory.One, PluralRules.Select("ru", 1));
            Assert.AreEqual(PluralCategory.One, PluralRules.Select("ru", 21));
            Assert.AreEqual(PluralCategory.Few, PluralRules.Select("ru", 2));
            Assert.AreEqual(PluralCategory.Few, PluralRules.Select("ru", 23));
            Assert.AreEqual(PluralCategory.Many, PluralRules.Select("ru", 5));
            Assert.AreEqual(PluralCategory.Many, PluralRules.Select("ru", 11)); // 11 特例 → many
            Assert.AreEqual(PluralCategory.Many, PluralRules.Select("ru", 0));
        }

        [Test]
        public void 波兰语_one_few_many()
        {
            Assert.AreEqual(PluralCategory.One, PluralRules.Select("pl", 1));
            Assert.AreEqual(PluralCategory.Few, PluralRules.Select("pl", 2));
            Assert.AreEqual(PluralCategory.Few, PluralRules.Select("pl", 22));
            Assert.AreEqual(PluralCategory.Many, PluralRules.Select("pl", 5));
            Assert.AreEqual(PluralCategory.Many, PluralRules.Select("pl", 12));
            Assert.AreEqual(PluralCategory.Many, PluralRules.Select("pl", 0));
        }

        [Test]
        public void 阿拉伯语_全六类()
        {
            Assert.AreEqual(PluralCategory.Zero, PluralRules.Select("ar", 0));
            Assert.AreEqual(PluralCategory.One, PluralRules.Select("ar", 1));
            Assert.AreEqual(PluralCategory.Two, PluralRules.Select("ar", 2));
            Assert.AreEqual(PluralCategory.Few, PluralRules.Select("ar", 3));
            Assert.AreEqual(PluralCategory.Few, PluralRules.Select("ar", 10));
            Assert.AreEqual(PluralCategory.Many, PluralRules.Select("ar", 11));
            Assert.AreEqual(PluralCategory.Many, PluralRules.Select("ar", 99));
            Assert.AreEqual(PluralCategory.Other, PluralRules.Select("ar", 100));
        }

        [Test]
        public void 未登记语言_安全回退other()
        {
            Assert.AreEqual(PluralCategory.Other, PluralRules.Select("xx", 1));
            Assert.AreEqual(PluralCategory.Other, PluralRules.Select("", 1));
            Assert.AreEqual(PluralCategory.Other, PluralRules.Select(null, 1));
        }

        [Test]
        public void 负数按绝对值处理()
        {
            Assert.AreEqual(PluralCategory.One, PluralRules.Select("en", -1));
            Assert.AreEqual(PluralCategory.Few, PluralRules.Select("ru", -2));
        }

        [Test]
        public void 主语言子标签解析_大小写与连字符无关()
        {
            Assert.AreEqual(PluralCategory.One, PluralRules.Select("en-US", 1));
            Assert.AreEqual(PluralCategory.One, PluralRules.Select("EN_us", 1));
        }

        [Test]
        public void key后缀映射()
        {
            Assert.AreEqual("zero", PluralCategory.Zero.KeySuffix());
            Assert.AreEqual("one", PluralCategory.One.KeySuffix());
            Assert.AreEqual("two", PluralCategory.Two.KeySuffix());
            Assert.AreEqual("few", PluralCategory.Few.KeySuffix());
            Assert.AreEqual("many", PluralCategory.Many.KeySuffix());
            Assert.AreEqual("other", PluralCategory.Other.KeySuffix());
        }
    }
}
