using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace Framework
{
    /// <summary>
    /// 通用 Tab 单项视图：持有自身引用与选中/未选皮肤数据，并负责"我长什么样"的渲染（SetSelected）。
    /// 不持有选中状态、不认识业务含义，由 <see cref="UITabGroup"/> 统一驱动。
    /// 各字段均为可选，按需在 Inspector / 构建工具中配置：分段按钮可用底图换皮，导航项可用高亮节点与染色。
    /// </summary>
    public sealed class TabItemView : MonoBehaviour
    {
        [Header("点击区按钮（必填）")]
        [SerializeField] private Button button;

        [Header("选中态换底图的目标 Image（分段按钮用，可空）")]
        [SerializeField] private Image background;

        [Header("选中态三态底图：常态/按下/禁用（可空）")]
        [SerializeField] private Sprite selectedNormal;
        [SerializeField] private Sprite selectedPressed;
        [SerializeField] private Sprite selectedDisabled;

        [Header("未选态三态底图：常态/按下/禁用（可空）")]
        [SerializeField] private Sprite normalNormal;
        [SerializeField] private Sprite normalPressed;
        [SerializeField] private Sprite normalDisabled;

        [Header("选中时显示、未选时隐藏的高亮节点（导航中心高亮用，可空）")]
        [SerializeField] private GameObject activeObject;

        [Header("图标（用于选中染色，可空）")]
        [SerializeField] private Image icon;

        [Header("文字（用于选中染色，可空）")]
        [SerializeField] private TMP_Text label;

        [Header("选中态图标/文字颜色")]
        [SerializeField] private Color selectedColor = Color.white;

        [Header("未选态图标/文字颜色")]
        [SerializeField] private Color normalColor = Color.white;

        /// <summary>点击区按钮，供 <see cref="UITabGroup"/> 绑定点击。</summary>
        public Button Button => button;

        /// <summary>
        /// 套用选中 / 未选皮肤：换底图三态、切高亮节点、改图标文字颜色（仅处理已配置的字段）。
        /// </summary>
        /// <param name="selected">是否为当前选中项。</param>
        public void SetSelected(bool selected)
        {
            Sprite stateNormal = selected ? selectedNormal : normalNormal;
            if (background != null && stateNormal != null)
            {
                background.sprite = stateNormal;
                if (button != null && button.transition == Selectable.Transition.SpriteSwap)
                {
                    button.spriteState = new SpriteState
                    {
                        highlightedSprite = stateNormal,
                        pressedSprite = selected ? selectedPressed : normalPressed,
                        selectedSprite = stateNormal,
                        disabledSprite = selected ? selectedDisabled : normalDisabled,
                    };
                }
            }

            if (activeObject != null)
            {
                activeObject.SetActive(selected);
            }

            Color contentColor = selected ? selectedColor : normalColor;
            if (icon != null)
            {
                icon.color = contentColor;
            }

            if (label != null)
            {
                label.color = contentColor;
            }
        }
    }
}
