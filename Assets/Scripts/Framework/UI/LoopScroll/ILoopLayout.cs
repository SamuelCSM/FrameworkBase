using System;
using UnityEngine;

namespace Framework
{
    /// <summary>循环列表滚动轴向。</summary>
    public enum LoopAxis
    {
        /// <summary>竖向滚动（主轴 = Y，交叉轴 = X）。</summary>
        Vertical,

        /// <summary>横向滚动（主轴 = X，交叉轴 = Y）。</summary>
        Horizontal,
    }

    /// <summary>定位对齐方式（沿主轴，方向无关）。</summary>
    public enum LoopAlign
    {
        /// <summary>目标项贴主轴起始（竖向=顶 / 横向=左）。</summary>
        Start,

        /// <summary>目标项居中。</summary>
        Center,

        /// <summary>目标项贴主轴末尾（竖向=底 / 横向=右）。</summary>
        End,
    }

    /// <summary>
    /// 布局测量参数：由控制器从 View 配置与模板尺寸组装后传入布局策略。
    /// <para>所有几何量统一用「主轴 / 交叉轴」描述，由布局在出参时映射回 X/Y，避免在控制器内分散处理轴向。</para>
    /// </summary>
    public struct LoopLayoutConfig
    {
        /// <summary>滚动轴向。</summary>
        public LoopAxis Axis;

        /// <summary>交叉轴项数（1 = 单列/单行，≥2 = 网格；变长布局恒为 1）。</summary>
        public int CrossCount;

        /// <summary>单项主轴尺寸（定尺寸用；变长布局忽略）。</summary>
        public float CellMain;

        /// <summary>单项交叉轴尺寸。</summary>
        public float CellCross;

        /// <summary>主轴相邻行/列间距。</summary>
        public float SpacingMain;

        /// <summary>交叉轴相邻项间距（网格列间距）。</summary>
        public float SpacingCross;

        /// <summary>主轴起始内边距（竖向=上 / 横向=左）。</summary>
        public float PadStart;

        /// <summary>主轴末尾内边距（竖向=下 / 横向=右）。</summary>
        public float PadEnd;

        /// <summary>交叉轴起始内边距（竖向=左 / 横向=上）。</summary>
        public float PadCrossStart;

        /// <summary>主轴方向视口外上下各保留的缓冲行数（≥0）。</summary>
        public int Buffer;
    }

    /// <summary>
    /// 循环列表布局策略（仿 RecyclerView LayoutManager）：控制器只管回收复用，
    /// 「内容尺寸 / 可视区间 / 各项位置 / 定位偏移」全部委托给本接口的实现，从而支持竖/横/网格/变长多模式。
    /// <para>
    /// 实现可持有 <see cref="Measure"/> 后的状态（变长布局需缓存前缀和）；除 <see cref="Measure"/> 外的方法须为只读查询。
    /// 几何坐标系：内容根取左上锚点、pivot 左上，<see cref="GetAnchoredPosition"/> 返回相对内容根的锚点坐标。
    /// </para>
    /// </summary>
    public interface ILoopLayout
    {
        /// <summary>主轴内容总尺寸（竖向=高 / 横向=宽）；空数据为 0。</summary>
        float ContentSize { get; }

        /// <summary>
        /// 按数据条数与配置重新测量（数据 / 尺寸 / 配置变化后调用，变长布局在此建前缀和）。
        /// </summary>
        /// <param name="count">数据条数。</param>
        /// <param name="config">布局配置。</param>
        /// <param name="mainSizeOf">变长布局的单项主轴尺寸查询；定尺寸布局忽略，可为空。</param>
        void Measure(int count, in LoopLayoutConfig config, Func<int, float> mainSizeOf);

        /// <summary>
        /// 按当前主轴滚动偏移与视口主轴尺寸求需实例化的可视项闭区间 [first, last]（已含缓冲并夹紧）。
        /// </summary>
        /// <param name="scrollOffset">主轴滚动偏移（≥0）。</param>
        /// <param name="viewportMain">视口主轴尺寸。</param>
        /// <param name="first">输出：首个可视项下标。</param>
        /// <param name="last">输出：末个可视项下标；无可视项时为 first=0、last=-1。</param>
        void GetVisibleRange(float scrollOffset, float viewportMain, out int first, out int last);

        /// <summary>第 index 项相对内容根的锚点坐标。</summary>
        /// <param name="index">数据下标。</param>
        /// <returns>锚点坐标。</returns>
        Vector2 GetAnchoredPosition(int index);

        /// <summary>第 index 项主轴起始边距内容主轴起始的距离（用于增删时钉定锚点项）。</summary>
        /// <param name="index">数据下标。</param>
        /// <returns>主轴起始偏移。</returns>
        float GetItemMainStart(int index);

        /// <summary>
        /// 计算使指定项按对齐方式出现的主轴目标滚动偏移（已夹紧到 [0, 最大滚动]）。
        /// </summary>
        /// <param name="index">目标项下标。</param>
        /// <param name="align">对齐方式。</param>
        /// <param name="viewportMain">视口主轴尺寸。</param>
        /// <returns>目标滚动偏移。</returns>
        float GetScrollOffset(int index, LoopAlign align, float viewportMain);
    }
}
