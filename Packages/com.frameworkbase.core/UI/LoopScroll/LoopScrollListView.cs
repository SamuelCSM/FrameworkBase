using UnityEngine;
using UnityEngine.UI;

namespace Framework
{
    /// <summary>
    /// 循环滚动列表视图：只持有 Inspector 显式拖拽的引用与布局参数，不写任何调度逻辑。
    /// <para>
    /// 行高 / 列宽从 <see cref="CellTemplate"/> 的 RectTransform 自动读取；内容根 <see cref="Content"/> 须为左上锚点、
    /// pivot 左上。布局模式由 <see cref="Axis"/>、<see cref="CrossAxisCount"/>、<see cref="VariableSize"/> 决定，
    /// 所有调度由 <see cref="LoopScrollList"/> 驱动。
    /// </para>
    /// </summary>
    public sealed class LoopScrollListView : UISubView
    {
        [Header("滚动轴向（竖向 / 横向）")]
        [SerializeField] private LoopAxis axis = LoopAxis.Vertical;

        [Header("交叉轴项数（1=单列/单行，≥2=网格；仅定尺寸有效）")]
        [SerializeField] private int crossAxisCount = 1;

        [Header("变长主轴尺寸（每项高/宽不同；需数据源实现 ILoopVariableSource，且交叉轴项数须为 1）")]
        [SerializeField] private bool variableSize = false;

        [Header("滚动容器 ScrollRect")]
        [SerializeField] private ScrollRect scrollRect;

        [Header("可视视口 RectTransform（决定可视项数）")]
        [SerializeField] private RectTransform viewport;

        [Header("内容根 RectTransform（左上锚点 / pivot 左上，尺寸由列表驱动）")]
        [SerializeField] private RectTransform content;

        [Header("行模板（Prefab 内隐藏节点，行高/列宽取其 RectTransform 尺寸）")]
        [SerializeField] private UISubView cellTemplate;

        [Header("空列表提示节点（可选，无数据时显示）")]
        [SerializeField] private GameObject emptyHint;

        [Header("主轴行/列间距（像素）")]
        [SerializeField] private float spacingMain = 0f;

        [Header("交叉轴间距（网格列间距，像素）")]
        [SerializeField] private float spacingCross = 0f;

        [Header("主轴起始内边距（竖向=上 / 横向=左，像素）")]
        [SerializeField] private float padStart = 0f;

        [Header("主轴末尾内边距（竖向=下 / 横向=右，像素）")]
        [SerializeField] private float padEnd = 0f;

        [Header("交叉轴起始内边距（竖向=左 / 横向=上，像素）")]
        [SerializeField] private float padCrossStart = 0f;

        [Header("主轴方向视口外保留的缓冲行数")]
        [SerializeField] private int buffer = 1;

        /// <summary>滚动轴向。</summary>
        public LoopAxis Axis => axis;

        /// <summary>交叉轴项数（至少 1）。</summary>
        public int CrossAxisCount => crossAxisCount < 1 ? 1 : crossAxisCount;

        /// <summary>是否变长主轴尺寸。</summary>
        public bool VariableSize => variableSize;

        /// <summary>滚动容器。</summary>
        public ScrollRect ScrollRect => scrollRect;

        /// <summary>可视视口，可视项数取其主轴尺寸。</summary>
        public RectTransform Viewport => viewport;

        /// <summary>内容根，尺寸由列表运行时驱动。</summary>
        public RectTransform Content => content;

        /// <summary>行模板节点。</summary>
        public UISubView CellTemplate => cellTemplate;

        /// <summary>空列表提示节点，可为空。</summary>
        public GameObject EmptyHint => emptyHint;

        /// <summary>主轴行/列间距（像素）。</summary>
        public float SpacingMain => spacingMain;

        /// <summary>交叉轴间距（像素）。</summary>
        public float SpacingCross => spacingCross;

        /// <summary>主轴起始内边距（像素）。</summary>
        public float PadStart => padStart;

        /// <summary>主轴末尾内边距（像素）。</summary>
        public float PadEnd => padEnd;

        /// <summary>交叉轴起始内边距（像素）。</summary>
        public float PadCrossStart => padCrossStart;

        /// <summary>主轴方向缓冲行数。</summary>
        public int Buffer => buffer < 0 ? 0 : buffer;
    }
}
