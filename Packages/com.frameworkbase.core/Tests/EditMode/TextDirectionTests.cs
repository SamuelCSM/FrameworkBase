using NUnit.Framework;

namespace Framework.Tests
{
    /// <summary>
    /// 书写方向解析器单测：语言 → 方向映射、区域码/大小写无关、动态字符串强 RTL 检测。
    /// </summary>
    public class TextDirectionTests
    {
        [Test]
        public void RTL语言_判定为从右到左()
        {
            Assert.IsTrue(TextDirectionResolver.IsRightToLeft("ar"));
            Assert.IsTrue(TextDirectionResolver.IsRightToLeft("he"));
            Assert.IsTrue(TextDirectionResolver.IsRightToLeft("fa"));
            Assert.IsTrue(TextDirectionResolver.IsRightToLeft("ur"));
            Assert.AreEqual(TextDirection.RightToLeft, TextDirectionResolver.Of("ar_sa"));
        }

        [Test]
        public void LTR语言_判定为从左到右()
        {
            Assert.IsFalse(TextDirectionResolver.IsRightToLeft("en"));
            Assert.IsFalse(TextDirectionResolver.IsRightToLeft("zh_cn"));
            Assert.AreEqual(TextDirection.LeftToRight, TextDirectionResolver.Of("en_us"));
        }

        [Test]
        public void 希伯来旧码iw_也识别为RTL()
        {
            Assert.IsTrue(TextDirectionResolver.IsRightToLeft("iw"));
        }

        [Test]
        public void 区域码与大小写无关()
        {
            Assert.IsTrue(TextDirectionResolver.IsRightToLeft("AR-SA"));
            Assert.IsTrue(TextDirectionResolver.IsRightToLeft("Fa_IR"));
        }

        [Test]
        public void 空与未知语言_默认LTR()
        {
            Assert.IsFalse(TextDirectionResolver.IsRightToLeft(null));
            Assert.IsFalse(TextDirectionResolver.IsRightToLeft(""));
            Assert.IsFalse(TextDirectionResolver.IsRightToLeft("xx"));
        }

        [Test]
        public void 含阿拉伯字符_检测为RTL()
        {
            Assert.IsTrue(TextDirectionResolver.ContainsRightToLeft("مرحبا"));
        }

        [Test]
        public void 含希伯来字符_检测为RTL()
        {
            Assert.IsTrue(TextDirectionResolver.ContainsRightToLeft("שלום"));
        }

        [Test]
        public void 纯拉丁与中文_非RTL()
        {
            Assert.IsFalse(TextDirectionResolver.ContainsRightToLeft("Hello 世界 123"));
            Assert.IsFalse(TextDirectionResolver.ContainsRightToLeft(""));
            Assert.IsFalse(TextDirectionResolver.ContainsRightToLeft(null));
        }

        [Test]
        public void 混合文本_含RTL即为true()
        {
            Assert.IsTrue(TextDirectionResolver.ContainsRightToLeft("Player: مرحبا"));
        }
    }
}
