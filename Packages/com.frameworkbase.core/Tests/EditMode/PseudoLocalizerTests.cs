using NUnit.Framework;

namespace Framework.Tests
{
    /// <summary>
    /// 伪本地化单测：界标包裹、格式占位符原样保留、长度膨胀、重音映射、空输入安全。
    /// </summary>
    public class PseudoLocalizerTests
    {
        [Test]
        public void 变形_界标包裹且长度膨胀()
        {
            string result = PseudoLocalizer.Transform("Welcome");

            StringAssert.StartsWith("⟦", result);
            StringAssert.EndsWith("⟧", result);
            Assert.Greater(result.Length, "Welcome".Length + 2, "应有填充字符模拟更长语言");
        }

        [Test]
        public void 变形_拉丁字母替换为重音变体()
        {
            string result = PseudoLocalizer.Transform("aeiou");
            StringAssert.Contains("áéíóú", result);
        }

        [Test]
        public void 变形_中文与数字原样保留()
        {
            string result = PseudoLocalizer.Transform("金币128个");
            StringAssert.Contains("金币128个", result, "非拉丁字符不变形，只加界标和填充");
        }

        [Test]
        public void 变形_格式占位符原样保留()
        {
            string result = PseudoLocalizer.Transform("You have {0} coins, {1:N0} gems");

            StringAssert.Contains("{0}", result, "简单占位符必须原样保留");
            StringAssert.Contains("{1:N0}", result, "带格式说明的占位符必须原样保留");

            // 变形后的文案仍可安全 Format
            Assert.DoesNotThrow(() => string.Format(result, 5, 12345));
        }

        [Test]
        public void 变形_未闭合花括号_不吞后续文本()
        {
            string result = PseudoLocalizer.Transform("broken { brace");
            StringAssert.Contains("brá", result.Replace("⟦", ""), "未闭合花括号后的文本仍应正常变形");
        }

        [Test]
        public void 变形_空输入原样返回()
        {
            Assert.IsNull(PseudoLocalizer.Transform(null));
            Assert.AreEqual("", PseudoLocalizer.Transform(""));
        }
    }
}
