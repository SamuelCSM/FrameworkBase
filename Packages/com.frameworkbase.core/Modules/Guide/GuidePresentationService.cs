using System;
using UnityEngine;

namespace Framework
{
    /// <summary>通用引导表现服务：按 TargetId 创建/更新顶层挖孔遮罩。</summary>
    public sealed class GuidePresentationService : IDisposable
    {
        private readonly UIManager _ui;
        private readonly UITargetRegistry _targets;
        private GameObject _overlayObject;
        private GuideMaskOverlay _overlay;

        public GuidePresentationService(UIManager ui, UITargetRegistry targets)
        {
            _ui = ui ?? throw new ArgumentNullException(nameof(ui));
            _targets = targets ?? throw new ArgumentNullException(nameof(targets));
        }

        public bool IsVisible => _overlayObject != null;

        /// <summary>孔外压暗区被点击时转发（供业务叠加「请点击高亮处」抖动等反馈，表现不进框架）。</summary>
        public event Action DimClicked;

        public bool TryFocus(int targetId, object scope, float padding, float dimAlpha)
        {
            if (!_targets.TryResolve(targetId, scope, out UITarget target)) return false;
            EnsureOverlay();
            _overlay.color = new Color(0f, 0f, 0f, Mathf.Clamp01(dimAlpha));
            _overlay.Focus(target.RectTransform, Mathf.Max(0f, padding));
            _overlayObject.transform.SetAsLastSibling();
            return true;
        }

        public void Clear()
        {
            if (_overlay != null) _overlay.DimClicked -= RaiseDimClicked;
            if (_overlayObject != null) UnityEngine.Object.Destroy(_overlayObject);
            _overlayObject = null;
            _overlay = null;
        }

        /// <summary>转发当前遮罩的孔外点击；遮罩销毁重建后订阅会重新挂到新实例上。</summary>
        private void RaiseDimClicked() => DimClicked?.Invoke();

        public void Dispose() => Clear();

        private void EnsureOverlay()
        {
            if (_overlayObject != null) return;
            Transform parent = _ui.GetLayerRoot(UILayer.Top)
                ?? throw new InvalidOperationException("UILayer.Top 尚未初始化。");
            _overlayObject = new GameObject(
                "GuideMaskOverlay",
                typeof(RectTransform),
                typeof(CanvasRenderer),
                typeof(GuideMaskOverlay));
            _overlayObject.layer = LayerMask.NameToLayer("UI");
            var rect = (RectTransform)_overlayObject.transform;
            rect.SetParent(parent, false);
            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.one;
            rect.offsetMin = Vector2.zero;
            rect.offsetMax = Vector2.zero;
            _overlay = _overlayObject.GetComponent<GuideMaskOverlay>();
            _overlay.raycastTarget = true;
            // 把遮罩的孔外点击转发到服务层，否则该事件无人可订阅、OnPointerClick 形同死链。
            _overlay.DimClicked += RaiseDimClicked;
        }
    }
}
