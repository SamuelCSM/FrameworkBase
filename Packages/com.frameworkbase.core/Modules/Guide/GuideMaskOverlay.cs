using System;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace Framework
{
    /// <summary>
    /// 引导遮罩：全屏半透明压暗层 + 目标控件上的矩形挖孔。
    /// 孔内点击<b>穿透</b>给真实控件（真按钮真响应，业务在按钮回调里 <see cref="GuideFlow.CompleteStep"/>），
    /// 孔外点击被遮罩挡下并触发 <see cref="DimClicked"/>（做「请点击高亮处」抖动提示用）。
    /// <para>
    /// 框架四原语之「挖孔 + 触发接线」。用法：全屏 UI 上挂本组件（引导层级），
    /// <see cref="Focus"/> 对准目标、<see cref="ClearFocus"/> 全屏压暗（对话步骤）。
    /// 目标随布局 / 滚动移动时孔每帧跟随。视觉主体是压暗色（Graphic.color），
    /// 高亮描边 / 手指指针等表现件由业务按需叠加，不进框架。
    /// </para>
    /// </summary>
    [RequireComponent(typeof(CanvasRenderer))]
    public class GuideMaskOverlay : MaskableGraphic, ICanvasRaycastFilter, IPointerClickHandler
    {
        [Tooltip("挖孔相对目标边界的外扩像素")]
        [SerializeField] private float _holePadding = 8f;

        private RectTransform _focusTarget;
        private Rect _holeRect;
        private bool _hasHole;
        private readonly Vector3[] _cornersBuffer = new Vector3[4];

        /// <summary>孔外（压暗区）被点击时触发，做引导提示用。</summary>
        public event Action DimClicked;

        /// <summary>对准目标挖孔（每帧跟随目标）。padding 传负值用 Inspector 配置值。</summary>
        public void Focus(RectTransform target, float padding = -1f)
        {
            _focusTarget = target != null ? target : throw new ArgumentNullException(nameof(target));
            if (padding >= 0f)
                _holePadding = padding;
            UpdateHole();
        }

        /// <summary>清除挖孔：整屏压暗、整屏拦截点击（无交互目标的对话步骤）。</summary>
        public void ClearFocus()
        {
            _focusTarget = null;
            if (!_hasHole)
                return;
            _hasHole = false;
            SetVerticesDirty();
        }

        private void LateUpdate()
        {
            if (_focusTarget != null)
                UpdateHole();
        }

        /// <summary>把目标世界包围盒换算到本地坐标并外扩 padding；有变化才重建网格。</summary>
        private void UpdateHole()
        {
            if (_focusTarget == null)
                return;

            _focusTarget.GetWorldCorners(_cornersBuffer);
            Vector2 min = new Vector2(float.MaxValue, float.MaxValue);
            Vector2 max = new Vector2(float.MinValue, float.MinValue);
            for (int i = 0; i < 4; i++)
            {
                Vector3 local = rectTransform.InverseTransformPoint(_cornersBuffer[i]);
                min = Vector2.Min(min, local);
                max = Vector2.Max(max, local);
            }

            var hole = Rect.MinMaxRect(
                min.x - _holePadding, min.y - _holePadding,
                max.x + _holePadding, max.y + _holePadding);

            if (_hasHole && hole == _holeRect)
                return;
            _holeRect = hole;
            _hasHole = true;
            SetVerticesDirty();
        }

        /// <summary>压暗层网格：无孔时整面一个四边形；有孔时孔四周上下左右四条带。</summary>
        protected override void OnPopulateMesh(VertexHelper vh)
        {
            vh.Clear();
            Rect full = GetPixelAdjustedRect();

            if (!_hasHole)
            {
                AddQuad(vh, full.xMin, full.yMin, full.xMax, full.yMax);
                return;
            }

            float hx0 = Mathf.Clamp(_holeRect.xMin, full.xMin, full.xMax);
            float hx1 = Mathf.Clamp(_holeRect.xMax, full.xMin, full.xMax);
            float hy0 = Mathf.Clamp(_holeRect.yMin, full.yMin, full.yMax);
            float hy1 = Mathf.Clamp(_holeRect.yMax, full.yMin, full.yMax);

            AddQuad(vh, full.xMin, full.yMin, full.xMax, hy0); // 下
            AddQuad(vh, full.xMin, hy1, full.xMax, full.yMax); // 上
            AddQuad(vh, full.xMin, hy0, hx0, hy1);             // 左
            AddQuad(vh, hx1, hy0, full.xMax, hy1);             // 右
        }

        private void AddQuad(VertexHelper vh, float xMin, float yMin, float xMax, float yMax)
        {
            if (xMax - xMin <= 0f || yMax - yMin <= 0f)
                return;

            int start = vh.currentVertCount;
            Color32 color32 = color;
            var vertex = UIVertex.simpleVert;
            vertex.color = color32;

            vertex.position = new Vector3(xMin, yMin); vh.AddVert(vertex);
            vertex.position = new Vector3(xMin, yMax); vh.AddVert(vertex);
            vertex.position = new Vector3(xMax, yMax); vh.AddVert(vertex);
            vertex.position = new Vector3(xMax, yMin); vh.AddVert(vertex);
            vh.AddTriangle(start, start + 1, start + 2);
            vh.AddTriangle(start + 2, start + 3, start);
        }

        /// <summary>孔内点击穿透给真实控件；孔外由遮罩接收（触发 DimClicked）。</summary>
        public bool IsRaycastLocationValid(Vector2 screenPoint, UnityEngine.Camera eventCamera)
        {
            if (!_hasHole)
                return true;

            if (!RectTransformUtility.ScreenPointToLocalPointInRectangle(
                    rectTransform, screenPoint, eventCamera, out Vector2 local))
                return true;

            return !_holeRect.Contains(local);
        }

        public void OnPointerClick(PointerEventData eventData)
        {
            DimClicked?.Invoke();
        }

#if UNITY_EDITOR
        protected override void Reset()
        {
            base.Reset();
            color = new Color(0f, 0f, 0f, 0.6f);
        }
#endif
    }
}
