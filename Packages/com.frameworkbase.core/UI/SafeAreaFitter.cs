using System;
using UnityEngine;

namespace Framework
{
    /// <summary>
    /// 安全区适配器：把所在 RectTransform 锚定到 <c>Screen.safeArea</c>，
    /// 自动避让刘海 / 挖孔 / 圆角 / Home 条。
    ///
    /// 用法：挂在 UI prefab 的内容根 Panel 上（全屏背景图不挂，保持出血铺满；
    /// 按钮、文本等交互内容挂，避免被异形屏遮挡）。各边避让可单独关闭——
    /// 例如底部沉浸式面板只需避让顶部刘海时，取消勾选 Bottom。
    ///
    /// 分辨率 / 朝向 / safeArea 变化时自动重新应用（每帧零分配比对缓存，转屏立即生效）。
    /// 要求所在节点位于以整屏为界的 Canvas 层级下（框架各层 Canvas 即是）。
    /// </summary>
    [RequireComponent(typeof(RectTransform))]
    [DisallowMultipleComponent]
    public sealed class SafeAreaFitter : MonoBehaviour
    {
        /// <summary>避让边选择。</summary>
        [Flags]
        public enum Edge
        {
            None = 0,
            Left = 1 << 0,
            Right = 1 << 1,
            Top = 1 << 2,
            Bottom = 1 << 3,
            All = Left | Right | Top | Bottom,
        }

        [Tooltip("需要避让的边（未勾选的边保持贴屏幕边缘）")]
        [SerializeField] private Edge _edges = Edge.All;

        private RectTransform _rectTransform;
        private Rect _appliedSafeArea = new Rect(-1, -1, -1, -1);
        private Vector2Int _appliedScreen;

        /// <summary>运行时改避让边（改完立即重算）。</summary>
        public Edge Edges
        {
            get => _edges;
            set
            {
                _edges = value;
                _appliedSafeArea = new Rect(-1, -1, -1, -1); // 失效缓存，下帧强制重应用
            }
        }

        private void Awake()
        {
            _rectTransform = (RectTransform)transform;
        }

        private void OnEnable()
        {
            Apply();
        }

        private void Update()
        {
            // 分辨率 / 朝向 / 系统 safeArea 任一变化才重算；无变化零成本
            if (Screen.safeArea != _appliedSafeArea ||
                Screen.width != _appliedScreen.x || Screen.height != _appliedScreen.y)
            {
                Apply();
            }
        }

        private void Apply()
        {
            Rect safeArea = Screen.safeArea;
            var screen = new Vector2Int(Screen.width, Screen.height);

            if (!TryCalculateAnchors(safeArea, screen, _edges, out Vector2 anchorMin, out Vector2 anchorMax))
                return; // 屏幕尺寸非法（极早期帧），下帧再试

            _rectTransform.anchorMin = anchorMin;
            _rectTransform.anchorMax = anchorMax;
            _rectTransform.offsetMin = Vector2.zero;
            _rectTransform.offsetMax = Vector2.zero;

            _appliedSafeArea = safeArea;
            _appliedScreen = screen;
        }

        /// <summary>
        /// 把安全区矩形换算成归一化 anchor（纯计算，可单测）。
        /// 未勾选的边保持 0/1（贴屏幕边缘）；屏幕尺寸非法返回 false。
        /// </summary>
        public static bool TryCalculateAnchors(
            Rect safeArea, Vector2Int screen, Edge edges,
            out Vector2 anchorMin, out Vector2 anchorMax)
        {
            anchorMin = Vector2.zero;
            anchorMax = Vector2.one;

            if (screen.x <= 0 || screen.y <= 0)
                return false;

            if ((edges & Edge.Left) != 0)
                anchorMin.x = safeArea.xMin / screen.x;
            if ((edges & Edge.Bottom) != 0)
                anchorMin.y = safeArea.yMin / screen.y;
            if ((edges & Edge.Right) != 0)
                anchorMax.x = safeArea.xMax / screen.x;
            if ((edges & Edge.Top) != 0)
                anchorMax.y = safeArea.yMax / screen.y;

            // 系统偶发给出越界/翻转的 safeArea：夹回合法区间，宁可不避让也不能把 UI 翻出屏幕
            anchorMin.x = Mathf.Clamp01(anchorMin.x);
            anchorMin.y = Mathf.Clamp01(anchorMin.y);
            anchorMax.x = Mathf.Clamp(anchorMax.x, anchorMin.x, 1f);
            anchorMax.y = Mathf.Clamp(anchorMax.y, anchorMin.y, 1f);
            return true;
        }
    }
}
