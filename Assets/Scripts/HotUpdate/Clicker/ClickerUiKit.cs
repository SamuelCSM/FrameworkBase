using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace HotUpdate.Clicker
{
    /// <summary>
    /// 运行时灰盒 UGUI 构建工具（纯代码，无预制体）。玩法 UI 全部代码生成，
    /// 100% 在热更程序集内，天然适配「改玩法代码 → 热更下发」（切片 D）。禁美术投入。
    /// </summary>
    internal static class ClickerUiKit
    {
        private static readonly Color TextBright = new Color(0.92f, 0.93f, 0.95f, 1f);
        private static readonly Color Accent     = new Color(0.29f, 0.56f, 0.89f, 1f);
        private static readonly Color PanelDark  = new Color(0.16f, 0.18f, 0.23f, 1f);

        private static int UiLayer => LayerMask.NameToLayer("UI");

        public static void Stretch(GameObject go)
        {
            var rt = go.GetComponent<RectTransform>();
            if (rt == null) rt = go.AddComponent<RectTransform>();
            rt.anchorMin = Vector2.zero;
            rt.anchorMax = Vector2.one;
            rt.sizeDelta = Vector2.zero;
            rt.anchoredPosition = Vector2.zero;
        }

        private static GameObject Child(Transform parent, string name)
        {
            var go = new GameObject(name, typeof(RectTransform));
            go.layer = UiLayer;
            go.transform.SetParent(parent, false);
            return go;
        }

        private static void SetRect(GameObject go, Vector2 anchor, Vector2 pos, Vector2 size, Vector2? pivot = null)
        {
            var rt = go.GetComponent<RectTransform>();
            rt.anchorMin = anchor;
            rt.anchorMax = anchor;
            rt.pivot = pivot ?? new Vector2(0.5f, 0.5f);
            rt.anchoredPosition = pos;
            rt.sizeDelta = size;
        }

        public static Image Image(Transform parent, string name, Color color, bool stretch)
        {
            var go = Child(parent, name);
            if (stretch) Stretch(go);
            var img = go.AddComponent<Image>();
            img.color = color;
            img.raycastTarget = false;
            return img;
        }

        public static TextMeshProUGUI Text(Transform parent, string name, string text, float fontSize,
            TextAlignmentOptions align, Vector2 anchor, Vector2 pos, Vector2 size, Vector2? pivot = null)
        {
            var go = Child(parent, name);
            SetRect(go, anchor, pos, size, pivot);
            var t = go.AddComponent<TextMeshProUGUI>();
            t.text = text;
            t.fontSize = fontSize;
            t.alignment = align;
            t.color = TextBright;
            t.raycastTarget = false;
            return t;
        }

        public static Button Button(Transform parent, string name, string label, float fontSize,
            Vector2 anchor, Vector2 pos, Vector2 size)
        {
            var go = Child(parent, name);
            SetRect(go, anchor, pos, size);
            var img = go.AddComponent<Image>();
            img.color = Accent; // 保留默认 raycastTarget=true，按钮才可点
            var button = go.AddComponent<Button>();
            button.targetGraphic = img;

            var t = Text(go.transform, "Label", label, fontSize, TextAlignmentOptions.Center,
                new Vector2(0.5f, 0.5f), Vector2.zero, Vector2.zero);
            Stretch(t.gameObject);
            t.color = Color.white;
            return button;
        }

        /// <summary>居中面板（弹窗内层容器），返回其 Transform。</summary>
        public static Transform Panel(Transform parent, string name, Vector2 size)
        {
            var go = Child(parent, name);
            SetRect(go, new Vector2(0.5f, 0.5f), Vector2.zero, size);
            var img = go.AddComponent<Image>();
            img.color = PanelDark;
            return go.transform;
        }
    }
}
