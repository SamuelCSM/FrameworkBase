using NUnit.Framework;
using UnityEngine;

namespace Framework.Tests
{
    /// <summary>
    /// UI 适配纯计算单测：安全区 anchor 换算（全屏/刘海/边掩码/非法输入/越界 Clamp）
    /// 与 CanvasScaler match 判定（更宽按高、更窄按宽、等比、非法输入）。
    /// </summary>
    public class UiAdaptationTests
    {
        // ── SafeAreaFitter.TryCalculateAnchors ──────────────────────────────

        [Test]
        public void 安全区_全屏时锚点为0到1()
        {
            var screen = new Vector2Int(1920, 1080);
            var safe = new Rect(0, 0, 1920, 1080);

            Assert.IsTrue(SafeAreaFitter.TryCalculateAnchors(
                safe, screen, SafeAreaFitter.Edge.All, out Vector2 min, out Vector2 max));
            Assert.AreEqual(Vector2.zero, min);
            Assert.AreEqual(Vector2.one, max);
        }

        [Test]
        public void 安全区_顶部刘海与底部Home条_按比例内缩()
        {
            var screen = new Vector2Int(1000, 2000);
            var safe = new Rect(0, 60, 1000, 1840); // 底部 60 Home 条 + 顶部 100 刘海

            SafeAreaFitter.TryCalculateAnchors(
                safe, screen, SafeAreaFitter.Edge.All, out Vector2 min, out Vector2 max);

            Assert.AreEqual(0.03f, min.y, 1e-4f, "底部内缩 60/2000");
            Assert.AreEqual(0.95f, max.y, 1e-4f, "顶部内缩到 (60+1840)/2000");
            Assert.AreEqual(0f, min.x, 1e-4f);
            Assert.AreEqual(1f, max.x, 1e-4f);
        }

        [Test]
        public void 安全区_边掩码_未勾选的边保持贴边()
        {
            var screen = new Vector2Int(1000, 2000);
            var safe = new Rect(0, 60, 1000, 1840);

            SafeAreaFitter.TryCalculateAnchors(
                safe, screen, SafeAreaFitter.Edge.Top, out Vector2 min, out Vector2 max);

            Assert.AreEqual(0f, min.y, 1e-4f, "Bottom 未勾选：不避让 Home 条");
            Assert.AreEqual(0.95f, max.y, 1e-4f, "Top 勾选：避让刘海");
        }

        [Test]
        public void 安全区_非法屏幕尺寸_返回false()
        {
            Assert.IsFalse(SafeAreaFitter.TryCalculateAnchors(
                new Rect(0, 0, 100, 100), new Vector2Int(0, 0),
                SafeAreaFitter.Edge.All, out _, out _));
        }

        [Test]
        public void 安全区_越界safeArea_夹回合法区间()
        {
            var screen = new Vector2Int(1000, 2000);
            var safe = new Rect(-100, -100, 2000, 4000); // 系统偶发脏数据

            SafeAreaFitter.TryCalculateAnchors(
                safe, screen, SafeAreaFitter.Edge.All, out Vector2 min, out Vector2 max);

            Assert.GreaterOrEqual(min.x, 0f);
            Assert.GreaterOrEqual(min.y, 0f);
            Assert.LessOrEqual(max.x, 1f);
            Assert.LessOrEqual(max.y, 1f);
            Assert.LessOrEqual(min.x, max.x, "锚点不得翻转");
            Assert.LessOrEqual(min.y, max.y);
        }

        // ── CanvasScalerAutoMatch.CalculateMatch ────────────────────────────

        [Test]
        public void 匹配_屏幕更宽_按高度缩放()
        {
            // 21:9 带鱼屏 vs 16:9 参考 → match=1，UI 不超出上下
            Assert.AreEqual(1f, CanvasScalerAutoMatch.CalculateMatch(2520, 1080, 1920, 1080));
        }

        [Test]
        public void 匹配_屏幕更窄_按宽度缩放()
        {
            // 4:3 平板（横屏）比 16:9 更"窄" → match=0，UI 不超出左右
            Assert.AreEqual(0f, CanvasScalerAutoMatch.CalculateMatch(1024, 768, 1920, 1080));
        }

        [Test]
        public void 匹配_等比与非法输入()
        {
            Assert.AreEqual(1f, CanvasScalerAutoMatch.CalculateMatch(1920, 1080, 1920, 1080), "等比按高度");
            Assert.AreEqual(1f, CanvasScalerAutoMatch.CalculateMatch(0, 1080, 1920, 1080), "非法输入回退安全默认");
        }
    }
}
