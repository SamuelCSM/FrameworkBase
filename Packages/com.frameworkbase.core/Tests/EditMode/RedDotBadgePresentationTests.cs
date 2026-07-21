using NUnit.Framework;

namespace Framework.Tests
{
    public class RedDotBadgePresentationTests
    {
        [Test]
        public void 计数为零一律隐藏且无文本()
        {
            RedDotBadgeDisplay display = RedDotBadgePresentation.Resolve(0, RedDotBadge.DisplayMode.Number, 99);
            Assert.IsFalse(display.Visible);
            Assert.AreEqual(string.Empty, display.Text);
        }

        [Test]
        public void Dot样式只显隐不给文本()
        {
            RedDotBadgeDisplay display = RedDotBadgePresentation.Resolve(5, RedDotBadge.DisplayMode.DotOnly, 99);
            Assert.IsTrue(display.Visible);
            Assert.AreEqual(string.Empty, display.Text);
        }

        [Test]
        public void Number样式显示计数并在超过封顶时显示上限加号()
        {
            Assert.AreEqual("7", RedDotBadgePresentation.Resolve(7, RedDotBadge.DisplayMode.Number, 99).Text);
            Assert.AreEqual("99+", RedDotBadgePresentation.Resolve(150, RedDotBadge.DisplayMode.Number, 99).Text);
        }

        [Test]
        public void Number样式封顶为零表示不封顶()
        {
            Assert.AreEqual("12345", RedDotBadgePresentation.Resolve(12345, RedDotBadge.DisplayMode.Number, 0).Text);
        }

        [Test]
        public void New与感叹号样式给出固定提示文本()
        {
            Assert.AreEqual(
                RedDotBadgePresentation.NewText,
                RedDotBadgePresentation.Resolve(1, RedDotBadge.DisplayMode.New, 99).Text);
            Assert.AreEqual(
                RedDotBadgePresentation.ExclamationText,
                RedDotBadgePresentation.Resolve(3, RedDotBadge.DisplayMode.Exclamation, 99).Text);
        }
    }
}
