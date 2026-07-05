using UnityEngine;
using UnityEngine.UI;

namespace Framework
{
    /// <summary>
    /// 窗口弹出时的背景遮罩行为。
    /// </summary>
    public enum UIBlockerMode
    {
        /// <summary>不创建遮罩（背景 UI 仍可交互）</summary>
        None,

        /// <summary>全屏透明遮罩，只拦截点击</summary>
        Transparent,

        /// <summary>半透明黑色遮罩（默认 alpha 0.6）</summary>
        DimBlack,

        /// <summary>半透明黑色遮罩 + 点击空白区域关闭当前窗口</summary>
        ClickToClose,
    }

    /// <summary>
    /// UI 遮罩工具类。
    /// 在窗口 GameObject 下方（同层级、低 sibling index）创建一个全屏 Image，
    /// 用于拦截下层 UI 的点击事件，并可选提供暗色视觉效果。
    /// </summary>
    public static class UIBlocker
    {
        /// <summary>默认暗色遮罩透明度</summary>
        private const float DefaultDimAlpha = 0.6f;

        /// <summary>
        /// 为指定窗口 GameObject 创建遮罩。
        /// 遮罩会成为窗口 GameObject 的前一个兄弟节点（sibling index 更小），
        /// 确保渲染在窗口之下、同层级其他 UI 之上。
        /// </summary>
        /// <param name="windowGO">窗口根对象。</param>
        /// <param name="mode">遮罩模式。</param>
        /// <param name="onClickBlocker">ClickToClose 模式下点击遮罩的回调。</param>
        /// <returns>创建的遮罩 GameObject，None 模式返回 null。</returns>
        public static GameObject Create(GameObject windowGO, UIBlockerMode mode, System.Action onClickBlocker = null)
        {
            if (mode == UIBlockerMode.None || windowGO == null)
            {
                return null;
            }

            Transform parent = windowGO.transform.parent;
            if (parent == null)
            {
                return null;
            }

            // 创建遮罩 GameObject
            var blockerGO = new GameObject("__UIBlocker__");
            blockerGO.layer = windowGO.layer;
            blockerGO.transform.SetParent(parent, false);

            // 放在窗口的前面（渲染顺序在窗口之下）
            int windowIndex = windowGO.transform.GetSiblingIndex();
            blockerGO.transform.SetSiblingIndex(windowIndex);

            // 设置全屏 RectTransform
            var rt = blockerGO.AddComponent<RectTransform>();
            rt.anchorMin = Vector2.zero;
            rt.anchorMax = Vector2.one;
            rt.sizeDelta = Vector2.zero;
            rt.anchoredPosition = Vector2.zero;

            // 添加 Image 用于拦截 Raycast
            var image = blockerGO.AddComponent<Image>();
            image.raycastTarget = true;

            switch (mode)
            {
                case UIBlockerMode.Transparent:
                    image.color = Color.clear;
                    break;

                case UIBlockerMode.DimBlack:
                    image.color = new Color(0f, 0f, 0f, DefaultDimAlpha);
                    break;

                case UIBlockerMode.ClickToClose:
                    image.color = new Color(0f, 0f, 0f, DefaultDimAlpha);
                    if (onClickBlocker != null)
                    {
                        var btn = blockerGO.AddComponent<Button>();
                        btn.transition = Selectable.Transition.None;
                        btn.onClick.AddListener(() => onClickBlocker.Invoke());
                    }
                    break;
            }

            return blockerGO;
        }

        /// <summary>
        /// 销毁遮罩 GameObject。
        /// </summary>
        /// <param name="blockerGO">遮罩对象。</param>
        public static void Destroy(GameObject blockerGO)
        {
            if (blockerGO != null)
            {
                Object.Destroy(blockerGO);
            }
        }
    }
}
