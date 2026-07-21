using System;
using UnityEngine;
using UnityEngine.Serialization;

namespace Framework
{
    /// <summary>
    /// 红点徽标绑定组件：挂在 UI 节点上，把 <c>GameEntry.RedDots</c> 共享红点 DAG 的稳定 ID
    /// 绑定到徽标显隐（计数 &gt; 0 显示）与可选的计数文本。
    /// <para>
    /// OnEnable 订阅、OnDisable 退订——窗口关闭 / 对象池回收自然解绑，不漏订阅；
    /// 订阅即回调当前值，UI 先激活、业务后写数的时序也能正确显示。
    /// 绑定局部树（业务自建 RedDotTree）时不挂本组件，代码里直接 Subscribe。
    /// </para>
    /// </summary>
    public class RedDotBadge : MonoBehaviour
    {
        public enum DisplayMode
        {
            DotOnly,
            Number,
        }

        [Tooltip("稳定红点 ID；可直接粘贴，Editor 会回显 Key/描述并提供搜索。0 表示未配置")]
        [SerializeField] private int _redDotId;

        // 兼容旧 Prefab 的 _path 序列化数据，Editor 可按 Key 一键迁移；运行时不再使用路径寻址。
        [FormerlySerializedAs("_path")]
        [SerializeField, HideInInspector] private string _legacyPath;

        [Tooltip("徽标根对象（计数 > 0 时激活）。必须是自身之外的子对象——本组件挂常驻节点（如按钮），" +
                 "徽标挂其下：若指向自身，隐藏徽标会连带 OnDisable 退订，计数再变也不会恢复显示")]
        [SerializeField] private GameObject _badgeRoot;

        [Tooltip("可选：计数文本。为空则只做显隐")]
        [SerializeField] private TMPro.TMP_Text _countText;

        [Tooltip("只显示红点，或显示节点最终计数。展示方式属于 UI，不进入红点逻辑配置")]
        [SerializeField] private DisplayMode _displayMode = DisplayMode.DotOnly;

        [Tooltip("计数显示封顶：超过显示为「上限+」，0 或负值表示不封顶")]
        [SerializeField] private int _maxDisplayCount = 99;

        private IDisposable _subscription;

        public int RedDotId => _redDotId;
        public string LegacyPath => _legacyPath;

        private void OnEnable()
        {
            if (_redDotId <= 0)
            {
                Debug.LogError($"[RedDotBadge] {name} 未配置有效红点 ID", this);
                return;
            }
            if (_badgeRoot == null || _badgeRoot == gameObject)
            {
                Debug.LogError($"[RedDotBadge] {name} 徽标根未配置或指向自身：本组件须挂常驻节点、" +
                               "徽标为其子对象，否则隐藏徽标会连带退订", this);
                return;
            }

            var tree = Core.GameEntry.RedDots;
            if (tree == null)
            {
                // 框架未初始化（如预制体单测场景）：保持隐藏，不订阅
                Apply(0);
                return;
            }

            try
            {
                // RedDotService 允许目录初始化前订阅，初始化完成后仍会保持绑定。
                _subscription = tree.Subscribe(_redDotId, Apply);
            }
            catch (Exception ex)
            {
                Apply(0);
                Debug.LogError($"[RedDotBadge] {name} 绑定红点 ID {_redDotId} 失败：{ex.Message}", this);
            }
        }

        private void OnDisable()
        {
            _subscription?.Dispose();
            _subscription = null;
        }

        /// <summary>按聚合计数刷新显隐与文本。</summary>
        private void Apply(int count)
        {
            bool visible = count > 0;
            if (_badgeRoot.activeSelf != visible)
                _badgeRoot.SetActive(visible);

            if (_countText != null && visible)
            {
                _countText.text = _displayMode == DisplayMode.DotOnly
                    ? string.Empty
                    : _maxDisplayCount > 0 && count > _maxDisplayCount
                        ? $"{_maxDisplayCount}+"
                        : count.ToString();
            }
        }

        /// <summary>代码构建 UI 时配置绑定；激活状态下会立即重订阅。</summary>
        public void Configure(
            int redDotId,
            GameObject badgeRoot,
            TMPro.TMP_Text countText = null,
            DisplayMode displayMode = DisplayMode.DotOnly,
            int maxDisplayCount = 99)
        {
            _subscription?.Dispose();
            _subscription = null;
            _redDotId = redDotId;
            _badgeRoot = badgeRoot;
            _countText = countText;
            _displayMode = displayMode;
            _maxDisplayCount = maxDisplayCount;
            if (isActiveAndEnabled) OnEnable();
        }
    }
}
