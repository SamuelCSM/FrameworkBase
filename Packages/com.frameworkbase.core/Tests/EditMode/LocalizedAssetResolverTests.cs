using System;
using System.Linq;
using NUnit.Framework;

namespace Framework.Tests
{
    public class LocalizedAssetResolverTests
    {
        [SetUp]
        [TearDown]
        public void ResetChains()
        {
            LocalizedAssetResolver.ClearFallbackChains();
        }

        [Test]
        public void 默认候选链_当前语言到默认语言到原始地址()
        {
            CollectionAssert.AreEqual(
                new[] { "UI/banner@en_us", "UI/banner@zh_cn", "UI/banner" },
                LocalizedAssetResolver.GetCandidates("UI/banner", "en_us").ToArray());
        }

        [Test]
        public void 当前语言即默认语言_不重复出现()
        {
            CollectionAssert.AreEqual(
                new[] { "UI/banner@zh_cn", "UI/banner" },
                LocalizedAssetResolver.GetCandidates("UI/banner", "zh_cn").ToArray());
        }

        [Test]
        public void 自定义回退链_按序插入且与默认链去重()
        {
            LocalizedAssetResolver.SetFallbackChain("zh_tw", "zh_cn", "en_us");

            CollectionAssert.AreEqual(
                new[] { "UI/banner@zh_tw", "UI/banner@zh_cn", "UI/banner@en_us", "UI/banner" },
                LocalizedAssetResolver.GetCandidates("UI/banner", "zh_tw").ToArray(),
                "链中已含默认语言 zh_cn 时不再追加重复项");

            // 覆盖式登记：清掉旧链
            LocalizedAssetResolver.SetFallbackChain("zh_tw");
            CollectionAssert.AreEqual(
                new[] { "UI/banner@zh_tw", "UI/banner@zh_cn", "UI/banner" },
                LocalizedAssetResolver.GetCandidates("UI/banner", "zh_tw").ToArray());
        }

        [Test]
        public void 语言代码规范化_横线大写与下划线小写等价()
        {
            Assert.AreEqual("UI/banner@zh_tw", LocalizedAssetResolver.Localize("UI/banner", "zh-TW"));

            LocalizedAssetResolver.SetFallbackChain("zh-TW", "zh-CN");
            CollectionAssert.AreEqual(
                new[] { "UI/banner@zh_tw", "UI/banner@zh_cn", "UI/banner" },
                LocalizedAssetResolver.GetCandidates("UI/banner", "ZH_TW").ToArray());
        }

        [Test]
        public void 基础地址校验_空地址与已含语言分隔符拒绝()
        {
            Assert.Throws<ArgumentException>(() => LocalizedAssetResolver.GetCandidates(null, "zh_cn"));
            Assert.Throws<ArgumentException>(() => LocalizedAssetResolver.GetCandidates("  ", "zh_cn"));
            Assert.Throws<ArgumentException>(
                () => LocalizedAssetResolver.GetCandidates("UI/banner@en_us", "zh_cn"),
                "传已本地化的地址属使用错误，会二次拼接出 a@x@y");
            Assert.Throws<ArgumentException>(() => LocalizedAssetResolver.Localize("", "zh_cn"));
        }

        [Test]
        public void 空语言输入_按规范化规则落到默认语言()
        {
            // NormalizeLanguage(null/空白) → 默认语言：候选只剩默认变体 + 原始地址
            CollectionAssert.AreEqual(
                new[] { "UI/banner@zh_cn", "UI/banner" },
                LocalizedAssetResolver.GetCandidates("UI/banner", null).ToArray());
        }
    }
}
