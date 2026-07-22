using System;
using Framework.Core;
using UnityEngine;
using UnityEngine.UI;

namespace Framework
{
    /// <summary>
    /// Prefab 上的语义 UI 目标锚点。启用时注册、禁用时注销；默认以最近的 UIView 根 GameObject 为实例 Scope。
    /// </summary>
    public sealed class UITargetAnchor : MonoBehaviour
    {
        [Tooltip("稳定 UI Target ID；由配置生成常量并通过 Editor 搜索器写入")]
        [SerializeField] private int _targetId;

        [Tooltip("引导高亮/定位使用的矩形；留空取自身 RectTransform")]
        [SerializeField] private RectTransform _target;

        [Tooltip("可点击目标；留空时仍可定位，但不能用于 UI.TargetClicked Trigger")]
        [SerializeField] private Button _button;

        [Tooltip("以最近 UIView 的根 GameObject 作为 Scope，支持同一窗口多实例")]
        [SerializeField] private bool _scopeToWindow = true;

        private IDisposable _registration;

        public int TargetId => _targetId;

        private void OnEnable() => TryRegister();
        private void Start() => TryRegister();

        private void OnDisable()
        {
            _registration?.Dispose();
            _registration = null;
        }

        /// <summary>代码构建 UI 使用；激活状态下立即重新注册。</summary>
        public void Configure(int targetId, RectTransform target = null, Button button = null)
        {
            _registration?.Dispose();
            _registration = null;
            _targetId = targetId;
            _target = target;
            _button = button;
            if (isActiveAndEnabled) TryRegister();
        }

        private void TryRegister()
        {
            if (_registration != null || _targetId <= 0 || GameEntry.UiTargets == null) return;
            RectTransform target = _target != null ? _target : transform as RectTransform;
            if (target == null)
            {
                GameLog.Error($"[UITargetAnchor] {_targetId} 缺少 RectTransform：{name}");
                return;
            }

            object scope = null;
            if (_scopeToWindow)
            {
                UIView view = GetComponentInParent<UIView>(includeInactive: true);
                if (view != null) scope = view.gameObject;
            }
            _registration = GameEntry.UiTargets.Register(_targetId, target, _button, scope);
        }
    }
}
