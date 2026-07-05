using Framework;
using NUnit.Framework;
using UnityEngine;

namespace Framework.Tests
{
    /// <summary>
    /// VariableLoopLayout 变长主轴布局单元测试。
    /// 基准：主轴起始内边距 20、主轴间距 10、末尾内边距 20、各项尺寸 [50,100,30,80]。
    /// 由此得前缀：起[20,80,190,230] 止[70,180,220,310]，内容高 310+20=330。
    /// </summary>
    public class VariableLoopLayoutTests
    {
        private static readonly float[] Sizes = { 50f, 100f, 30f, 80f };

        private static float SizeOf(int index) => Sizes[index];

        private static LoopLayoutConfig Config(int buffer = 0)
        {
            return new LoopLayoutConfig
            {
                Axis = LoopAxis.Vertical,
                CrossCount = 1,
                CellMain = 0f,
                CellCross = 200f,
                SpacingMain = 10f,
                SpacingCross = 0f,
                PadStart = 20f,
                PadEnd = 20f,
                PadCrossStart = 0f,
                Buffer = buffer,
            };
        }

        /// <summary>前缀和、内容高与项起始坐标正确。</summary>
        [Test]
        public void Measure_PrefixSums()
        {
            var layout = new VariableLoopLayout();
            layout.Measure(4, Config(), SizeOf);

            Assert.AreEqual(330f, layout.ContentSize, 1e-4f);
            Assert.AreEqual(20f, layout.GetItemMainStart(0), 1e-4f);
            Assert.AreEqual(190f, layout.GetItemMainStart(2), 1e-4f);
            Assert.AreEqual(new Vector2(0f, -190f), layout.GetAnchoredPosition(2));
        }

        /// <summary>可视区间用二分定位，随滚动整体推移。</summary>
        [Test]
        public void VisibleRange_BinarySearch()
        {
            var layout = new VariableLoopLayout();
            layout.Measure(4, Config(), SizeOf);

            // 视口 150 / scroll 0：项0[20,70]、项1[80,180] 命中 → [0,1]。
            layout.GetVisibleRange(0f, 150f, out int first, out int last);
            Assert.AreEqual(0, first);
            Assert.AreEqual(1, last);

            // scroll 100，下沿 250：首个止>100 为项1，末个起<250 为项3 → [1,3]。
            layout.GetVisibleRange(100f, 150f, out first, out last);
            Assert.AreEqual(1, first);
            Assert.AreEqual(3, last);
        }

        /// <summary>缓冲向两端扩展并夹紧。</summary>
        [Test]
        public void VisibleRange_Buffer()
        {
            var layout = new VariableLoopLayout();
            layout.Measure(4, Config(buffer: 1), SizeOf);

            layout.GetVisibleRange(0f, 150f, out int first, out int last);
            Assert.AreEqual(0, first);
            Assert.AreEqual(2, last);
        }

        /// <summary>定位偏移按变长尺寸计算，越界夹紧到最大滚动。</summary>
        [Test]
        public void ScrollOffset_VariableSizeAndClamp()
        {
            var layout = new VariableLoopLayout();
            layout.Measure(4, Config(), SizeOf);

            // 项1 起始 80（视口 150，最大滚动 330-150=180，不夹）。
            Assert.AreEqual(80f, layout.GetScrollOffset(1, LoopAlign.Start, 150f), 1e-4f);
            // 项1 居中：80+50-75=55；底对齐：80+100-150=30。
            Assert.AreEqual(55f, layout.GetScrollOffset(1, LoopAlign.Center, 150f), 1e-4f);
            Assert.AreEqual(30f, layout.GetScrollOffset(1, LoopAlign.End, 150f), 1e-4f);
            // 项3 起始 230 超过最大滚动 180，夹到 180。
            Assert.AreEqual(180f, layout.GetScrollOffset(3, LoopAlign.Start, 150f), 1e-4f);
        }

        /// <summary>空数据：内容 0，可视区间空。</summary>
        [Test]
        public void Empty_NoVisible()
        {
            var layout = new VariableLoopLayout();
            layout.Measure(0, Config(), SizeOf);

            Assert.AreEqual(0f, layout.ContentSize, 1e-4f);
            layout.GetVisibleRange(0f, 150f, out int first, out int last);
            Assert.AreEqual(0, first);
            Assert.AreEqual(-1, last);
        }
    }
}
