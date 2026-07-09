using System;
using System.Collections.Generic;
using NUnit.Framework;

namespace Framework.Tests
{
    /// <summary>
    /// 语言枚举 ↔ 配表列名映射单测：全枚举往返一致、列名唯一、规范化写法容错。
    /// 守住 CodeByType 单一映射源不漏配、不漂移。
    /// </summary>
    public class LanguageMappingTests
    {
        [Test]
        public void 全部枚举_ToCode往返回原枚举()
        {
            foreach (LanguageType type in Enum.GetValues(typeof(LanguageType)))
            {
                string code = Language.ToCode(type);
                Assert.IsFalse(string.IsNullOrEmpty(code), $"{type} 未配列名");
                Assert.AreEqual(type, Language.ToType(code), $"{type} → {code} → 往返不一致");
            }
        }

        [Test]
        public void 列名两两唯一_无重复映射()
        {
            var seen = new HashSet<string>();
            foreach (LanguageType type in Enum.GetValues(typeof(LanguageType)))
            {
                string code = Language.ToCode(type);
                Assert.IsTrue(seen.Add(code), $"列名 {code} 被多个枚举复用");
            }
        }

        [Test]
        public void 常用语言_映射到预期列名()
        {
            Assert.AreEqual("zh_cn", Language.ToCode(LanguageType.ZhCn));
            Assert.AreEqual("en_us", Language.ToCode(LanguageType.EnUs));
            Assert.AreEqual("ja_jp", Language.ToCode(LanguageType.JaJp));
            Assert.AreEqual("ru_ru", Language.ToCode(LanguageType.RuRu));
            Assert.AreEqual("ar_sa", Language.ToCode(LanguageType.ArSa));
        }

        [Test]
        public void ToType_容错连字符与大小写()
        {
            Assert.AreEqual(LanguageType.RuRu, Language.ToType("ru-RU"));
            Assert.AreEqual(LanguageType.JaJp, Language.ToType("JA_JP"));
        }

        [Test]
        public void ToType_未知语言_回退简体中文()
        {
            Assert.AreEqual(LanguageType.ZhCn, Language.ToType("xx_yy"));
            Assert.AreEqual(LanguageType.ZhCn, Language.ToType(""));
            Assert.AreEqual(LanguageType.ZhCn, Language.ToType(null));
        }
    }
}
