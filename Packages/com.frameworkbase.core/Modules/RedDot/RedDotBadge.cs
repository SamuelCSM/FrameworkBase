using System;
using UnityEngine;
using UnityEngine.Serialization;

namespace Framework
{
    /// <summary>
    /// 红点徽标绑定组件：挂在 UI 节点上，把 <c>RedDots.Service</c> 共享红点 DAG 的稳定 ID
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
            // 序号保持稳定：DotOnly=0、Number=1 为历史值，新增样式追加在后，兼容既有 Prefab 序列化。
            DotOnly = 0,
            Number = 1,
            New = 2,
            Exclamation = 3,
        }

        /// <summary>一个样式对应的美术根变体：当前样式匹配且徽标可见时激活，其余关闭。</summary>
        [Serializable]
        public struct StyleVariant
        {
            [Tooltip("此美术根对应的展示样式")]
            public DisplayMode style;

            [Tooltip("该样式下激活的美术根（图标变体）；应为 Badge Root 下的子对象")]
            public GameObject root;
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

        [Tooltip("展示样式：DotOnly 只显隐、Number 显示计数、New 显示 NEW、Exclamation 显示感叹号。" +
                 "展示方式属于 UI，不进入红点逻辑配置——同一红点 ID 可被不同入口按不同样式呈现")]
        [SerializeField] private DisplayMode _displayMode = DisplayMode.DotOnly;

        [Tooltip("计数显示封顶：超过显示为「上限+」，0 或负值表示不封顶")]
        [SerializeField] private int _maxDisplayCount = 99;

        [Tooltip("可选：按样式切换的美术根变体（图标变体）。配置后可见时只激活与当前样式匹配的根、其余关闭；" +
                 "留空则仅用 Badge Root + 文本表现。这些根应是 Badge Root 下的子对象")]
        [SerializeField] private StyleVariant[] _styleVariants;

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

            // 红点服务由中间层 RedDotModule 持有并发布（ADR-008）；模块未安装/未创建时为 null。
            var tree = RedDots.Service;
            if (tree == null)
            {
                // 红点模块尚未安装（如预制体单测场景、登录期尚未进入业务）：保持隐藏，不订阅。
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

        /// <summary>按聚合计数刷新显隐与文本；展示逻辑收口在纯函数 <see cref="RedDotBadgePresentation"/>。</summary>
        private void Apply(int count)
        {
            RedDotBadgeDisplay display = RedDotBadgePresentation.Resolve(count, _displayMode, _maxDisplayCount);
            if (_badgeRoot.activeSelf != display.Visible)
                _badgeRoot.SetActive(display.Visible);

            ApplyStyleVariants(display.Visible);

            if (_countText != null && display.Visible)
                _countText.text = display.Text;
        }

        /// <summary>可见时只激活与当前样式匹配的美术根变体，其余关闭；未配置变体则跳过。</summary>
        private void ApplyStyleVariants(bool visible)
        {
            if (_styleVariants == null) return;
            for (int i = 0; i < _styleVariants.Length; i++)
            {
                GameObject root = _styleVariants[i].root;
                if (root == null) continue;
                bool active = RedDotBadgePresentation.ShouldShowVariant(visible, _styleVariants[i].style, _displayMode);
                if (root.activeSelf != active) root.SetActive(active);
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
