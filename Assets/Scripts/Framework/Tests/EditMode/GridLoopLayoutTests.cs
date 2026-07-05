using Framework;
using NUnit.Framework;
using UnityEngine;

namespace Framework.Tests
{
    /// <summary>
    /// GridLoopLayout 定尺寸线性 / 网格布局单元测试。
    /// 线性基准：主轴尺寸 100、主轴间距 10、主轴上下内边距各 20、视口 300、数据 10 条（step=110、内容高 1130、最大滚动 830）。
    /// </summary>
    public class GridLoopLayoutTests
    {
        /// <summary>组装一份布局配置。</summary>
        private static LoopLayoutConfig Config(LoopAxis axis, int cross, int buffer = 0)
        {
            return new LoopLayoutConfig
            {
                Axis = axis,
                CrossCount = cross,
                CellMain = 100f,
                CellCross = 200f,
                SpacingMain = 10f,
                SpacingCross = 10f,
                PadStart = 20f,
                PadEnd = 20f,
                PadCrossStart = 0f,
                Buffer = buffer,
            };
        }

        /// <summary>竖向单列：内容高、可视区间、行位置、定位偏移均正确。</summary>
        [Test]
        public void VerticalLinear_Basics()
        {
            var layout = new GridLoopLayout();
            layout.Measure(10, Config(LoopAxis.Vertical, 1), null);

            Assert.AreEqual(1130f, layout.ContentSize, 1e-4f);

            layout.GetVisibleRange(0f, 300f, out int first, out int last);
            Assert.AreEqual(0, first);
            Assert.AreEqual(2, last);

            layout.GetVisibleRange(500f, 300f, out first, out last);
            Assert.AreEqual(4, first);
            Assert.AreEqual(7, last);

            // 行3锚点：竖向 (cross, -main) = (0, -(20+3*110)) = (0,-350)。
            Assert.AreEqual(new Vector2(0f, -350f), layout.GetAnchoredPosition(3));

            // 行5居中：570+50-150=470；末行起始 1010 夹到 830。
            Assert.AreEqual(470f, layout.GetScrollOffset(5, LoopAlign.Center, 300f), 1e-4f);
            Assert.AreEqual(830f, layout.GetScrollOffset(9, LoopAlign.Start, 300f), 1e-4f);
        }

        /// <summary>缓冲向上下扩展并夹紧。</summary>
        [Test]
        public void VerticalLinear_Buffer()
        {
            var layout = new GridLoopLayout();
            layout.Measure(10, Config(LoopAxis.Vertical, 1, buffer: 1), null);

            layout.GetVisibleRange(0f, 300f, out int first, out int last);
            Assert.AreEqual(0, first);
            Assert.AreEqual(3, last);
        }

        /// <summary>横向单行：主轴换到 X，锚点位置轴映射正确。</summary>
        [Test]
        public void HorizontalLinear_AnchorMapping()
        {
            var layout = new GridLoopLayout();
            layout.Measure(10, Config(LoopAxis.Horizontal, 1), null);

            // 行3锚点：横向 (main, -cross) = (20+3*110, -0) = (350,0)。
            Assert.AreEqual(new Vector2(350f, 0f), layout.GetAnchoredPosition(3));
        }

        /// <summary>2 列网格：行数、内容高、可视区间映射到下标、单元列位置正确。</summary>
        [Test]
        public void Grid_TwoColumns()
        {
            var layout = new GridLoopLayout();
            layout.Measure(10, Config(LoopAxis.Vertical, 2), null);

            // 行数 ceil(10/2)=5；内容高=20+20+5*100+4*10=580。
            Assert.AreEqual(580f, layout.ContentSize, 1e-4f);

            // 视口 300 / scroll 0：行0~2 可见 → 下标 0~5。
            layout.GetVisibleRange(0f, 300f, out int first, out int last);
            Assert.AreEqual(0, first);
            Assert.AreEqual(5, last);

            // 下标3：line=1,col=1；cross=0+1*(200+10)=210，main=20+1*110=130 → (210,-130)。
            Assert.AreEqual(new Vector2(210f, -130f), layout.GetAnchoredPosition(3));

            // 下标4 在 line2，起始 main=20+2*110=240。
            Assert.AreEqual(240f, layout.GetScrollOffset(4, LoopAlign.Start, 300f), 1e-4f);
        }

        /// <summary>空数据：内容 0，可视区间空。</summary>
        [Test]
        public void Empty_NoVisible()
        {
            var layout = new GridLoopLayout();
            layout.Measure(0, Config(LoopAxis.Vertical, 1), null);

            Assert.AreEqual(0f, layout.ContentSize, 1e-4f);
            layout.GetVisibleRange(0f, 300f, out int first, out int last);
            Assert.AreEqual(0, first);
            Assert.AreEqual(-1, last);
        }
    }
}
