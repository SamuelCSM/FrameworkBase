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
            if (_overlayObject != null) UnityEngine.Object.Destroy(_overlayObject);
            _overlayObject = null;
            _overlay = null;
        }

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
        }
    }
}
