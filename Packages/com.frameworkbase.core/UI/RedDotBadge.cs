using System;
using Framework.Foundation;
using UnityEngine;

namespace Framework
{
    /// <summary>
    /// 红点徽标绑定组件：挂在 UI 节点上，把 <c>GameEntry.RedDots</c> 共享红点树的某个路径
    /// 绑定到徽标显隐（计数 &gt; 0 显示）与可选的计数文本。
    /// <para>
    /// OnEnable 订阅、OnDisable 退订——窗口关闭 / 对象池回收自然解绑，不漏订阅；
    /// 订阅即回调当前值，UI 先激活、业务后写数的时序也能正确显示。
    /// 绑定局部树（业务自建 RedDotTree）时不挂本组件，代码里直接 Subscribe。
    /// </para>
    /// </summary>
    public class RedDotBadge : MonoBehaviour
    {
        [Tooltip("红点树路径，如 Mail/System")]
        [SerializeField] private string _path;

        [Tooltip("徽标根对象（计数 > 0 时激活）。必须是自身之外的子对象——本组件挂常驻节点（如按钮），" +
                 "徽标挂其下：若指向自身，隐藏徽标会连带 OnDisable 退订，计数再变也不会恢复显示")]
        [SerializeField] private GameObject _badgeRoot;

        [Tooltip("可选：计数文本。为空则只做显隐")]
        [SerializeField] private TMPro.TMP_Text _countText;

        [Tooltip("计数显示封顶：超过显示为「上限+」，0 或负值表示不封顶")]
        [SerializeField] private int _maxDisplayCount = 99;

        private IDisposable _subscription;

        private void OnEnable()
        {
            if (string.IsNullOrEmpty(_path))
            {
                Debug.LogError($"[RedDotBadge] {name} 未配置红点路径", this);
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

            _subscription = tree.Subscribe(_path, Apply);
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
                _countText.text = _maxDisplayCount > 0 && count > _maxDisplayCount
                    ? $"{_maxDisplayCount}+"
                    : count.ToString();
            }
        }
    }
}
