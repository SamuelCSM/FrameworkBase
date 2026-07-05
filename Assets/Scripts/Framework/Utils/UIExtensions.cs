using System;
using TMPro;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace Framework
{
    /// <summary>
    /// Unity UI 组件扩展方法
    ///
    /// 设计原则：
    ///   1. 所有方法做 null 守护，调用方无需提前判空
    ///   2. 赋值前做 dirty check，避免触发不必要的 UI 重绘
    ///   3. 以 Extension Method 方式挂载在原生 Unity 类型上，IDE 自动补全发现
    ///   4. 事件绑定统一经过 UITracker，业务层零感知地记录操作路径
    ///
    /// 使用示例（在 UIBase 子类的 OnInit 中）：
    ///   View.btnClose.AddClick(() => Close());           // 普通点击（自动埋点）
    ///   View.btnIcon.AddLongPress(() => OnLongPress());  // 长按
    ///   View.panel.AddDrag(onBegin, onDrag, onEnd);      // 拖拽
    ///   View.progressBar.SetProgress(0.5f);
    ///   View.panelError.SetVisible(false);
    /// </summary>
    public static class UIExtensions
    {
        // ── GameObject ─────────────────────────────────────────────────────

        /// <summary>设置 GameObject 显隐（含 null 检查 + dirty check）</summary>
        public static void SetVisible(this GameObject go, bool visible)
        {
            if (go == null || go.activeSelf == visible) return;
            go.SetActive(visible);
        }

        // ── Component（自动取 .gameObject）────────────────────────────────

        /// <summary>设置组件所在 GameObject 的显隐</summary>
        public static void SetVisible(this Component comp, bool visible)
        {
            if (comp == null) return;
            comp.gameObject.SetVisible(visible);
        }

        // ── Button 点击 ────────────────────────────────────────────────────

        /// <summary>
        /// 清除所有已有监听，绑定新的点击回调，并自动向 UITracker 上报。
        /// 这是业务层最常见的按钮绑定模式，避免重复绑定。
        /// </summary>
        public static void AddClick(this Button btn, UnityAction action)
        {
            if (btn == null) return;
            btn.onClick.RemoveAllListeners();
            btn.onClick.AddListener(() =>
            {
                UITracker.Record(btn.gameObject, UIEventType.Click);
                action?.Invoke();
            });
        }

        /// <summary>追加点击监听（不清除已有监听，自动埋点）</summary>
        public static void AppendClick(this Button btn, UnityAction action)
        {
            if (btn == null) return;
            btn.onClick.AddListener(() =>
            {
                UITracker.Record(btn.gameObject, UIEventType.Click);
                action?.Invoke();
            });
        }

        /// <summary>
        /// 为非 Button 组件绑定点击（Component 版，清除旧监听）。
        /// 适用于 Image、RectTransform 等没有 Button 组件的可点击区域。
        /// </summary>
        public static void AddClick(this Component comp, Action action)
        {
            if (comp == null || action == null) return;
            AddClick(comp.gameObject, action);
        }

        /// <summary>
        /// 为非 Button GameObject 绑定点击（GameObject 版，清除旧监听）。
        /// 适用于 panelXxx 等直接持有 GameObject 引用的场景。
        /// </summary>
        public static void AddClick(this GameObject go, Action action)
        {
            if (go == null || action == null) return;
            var listener = UIEventListener.Get(go);
            listener.clickHandler = _ =>
            {
                UITracker.Record(go, UIEventType.Click);
                action.Invoke();
            };
        }

        /// <summary>追加点击监听（Component 版，不清除已有监听）</summary>
        public static void AppendClick(this Component comp, Action action)
        {
            if (comp == null || action == null) return;
            AppendClick(comp.gameObject, action);
        }

        /// <summary>追加点击监听（GameObject 版，不清除已有监听）</summary>
        public static void AppendClick(this GameObject go, Action action)
        {
            if (go == null || action == null) return;
            var listener = UIEventListener.Get(go);
            var existing = listener.clickHandler;
            listener.clickHandler = e =>
            {
                existing?.Invoke(e);
                UITracker.Record(go, UIEventType.Click);
                action.Invoke();
            };
        }

        /// <summary>设置 Selectable 组件是否可交互（含 dirty check）</summary>
        public static void SetInteractable(this Selectable sel, bool interactable)
        {
            if (sel == null || sel.interactable == interactable) return;
            sel.interactable = interactable;
        }

        // ── 长按 ──────────────────────────────────────────────────────────
        // Component 版：适用于 Button、Image 等所有组件（Button IS-A Component）
        // GameObject 版：适用于只有 GameObject 引用的场景（如 panelXxx）

        /// <summary>
        /// 绑定长按回调（触发阈值默认 0.5 秒）。
        /// Component 版，Button / Image / RectTransform 等均可直接调用。
        /// </summary>
        public static void AddLongPress(this Component comp, Action action, float holdTime = 0.5f)
        {
            if (comp == null || action == null) return;
            AddLongPress(comp.gameObject, action, holdTime);
        }

        /// <summary>绑定长按回调（GameObject 版）</summary>
        public static void AddLongPress(this GameObject go, Action action, float holdTime = 0.5f)
        {
            if (go == null || action == null) return;
            var listener = UIEventListener.Get(go);
            listener.LongPressThreshold = holdTime;
            listener.longPressHandler = _ =>
            {
                UITracker.Record(go, UIEventType.LongPress);
                action.Invoke();
            };
        }

        // ── Pointer Down / Up ──────────────────────────────────────────────

        /// <summary>绑定 Pointer 按下回调（Component 版，含自动埋点）</summary>
        public static void AddPointerDown(this Component comp, Action<PointerEventData> action)
        {
            if (comp == null || action == null) return;
            AddPointerDown(comp.gameObject, action);
        }

        /// <summary>绑定 Pointer 按下回调（GameObject 版）</summary>
        public static void AddPointerDown(this GameObject go, Action<PointerEventData> action)
        {
            if (go == null || action == null) return;
            UIEventListener.Get(go).pointerDownHandler = e =>
            {
                UITracker.Record(go, UIEventType.PointerDown);
                action.Invoke(e);
            };
        }

        /// <summary>绑定 Pointer 抬起回调（Component 版，含自动埋点）</summary>
        public static void AddPointerUp(this Component comp, Action<PointerEventData> action)
        {
            if (comp == null || action == null) return;
            AddPointerUp(comp.gameObject, action);
        }

        /// <summary>绑定 Pointer 抬起回调（GameObject 版）</summary>
        public static void AddPointerUp(this GameObject go, Action<PointerEventData> action)
        {
            if (go == null || action == null) return;
            UIEventListener.Get(go).pointerUpHandler = e =>
            {
                UITracker.Record(go, UIEventType.PointerUp);
                action.Invoke(e);
            };
        }

        // ── 拖拽 ──────────────────────────────────────────────────────────

        /// <summary>
        /// 绑定完整拖拽回调（Component 版）。
        /// BeginDrag 和 EndDrag 自动埋点；onDrag 高频回调不埋点以节省性能。
        /// </summary>
        public static void AddDrag(
            this Component comp,
            Action<PointerEventData> onBeginDrag = null,
            Action<PointerEventData> onDrag      = null,
            Action<PointerEventData> onEndDrag   = null)
        {
            if (comp == null) return;
            AddDrag(comp.gameObject, onBeginDrag, onDrag, onEndDrag);
        }

        /// <summary>绑定完整拖拽回调（GameObject 版）</summary>
        public static void AddDrag(
            this GameObject go,
            Action<PointerEventData> onBeginDrag = null,
            Action<PointerEventData> onDrag      = null,
            Action<PointerEventData> onEndDrag   = null)
        {
            if (go == null) return;
            var listener = UIEventListener.Get(go);

            if (onBeginDrag != null)
                listener.beginDragHandler = e =>
                {
                    UITracker.Record(go, UIEventType.BeginDrag);
                    onBeginDrag.Invoke(e);
                };

            if (onDrag != null)
                listener.dragHandler = onDrag;  // 高频，不埋点

            if (onEndDrag != null)
                listener.endDragHandler = e =>
                {
                    UITracker.Record(go, UIEventType.EndDrag);
                    onEndDrag.Invoke(e);
                };
        }

        // ── Pointer Enter / Exit ───────────────────────────────────────────

        /// <summary>绑定鼠标/手指进入区域回调（Component 版）</summary>
        public static void AddPointerEnter(this Component comp, Action<PointerEventData> action)
        {
            if (comp == null || action == null) return;
            UIEventListener.Get(comp.gameObject).pointerEnterHandler = action;
        }

        /// <summary>绑定鼠标/手指进入区域回调（GameObject 版）</summary>
        public static void AddPointerEnter(this GameObject go, Action<PointerEventData> action)
        {
            if (go == null || action == null) return;
            UIEventListener.Get(go).pointerEnterHandler = action;
        }

        /// <summary>绑定鼠标/手指离开区域回调（Component 版）</summary>
        public static void AddPointerExit(this Component comp, Action<PointerEventData> action)
        {
            if (comp == null || action == null) return;
            UIEventListener.Get(comp.gameObject).pointerExitHandler = action;
        }

        /// <summary>绑定鼠标/手指离开区域回调（GameObject 版）</summary>
        public static void AddPointerExit(this GameObject go, Action<PointerEventData> action)
        {
            if (go == null || action == null) return;
            UIEventListener.Get(go).pointerExitHandler = action;
        }

        // ── TMP_Text ───────────────────────────────────────────────────────

        /// <summary>设置 TMP 文字（含 null 检查 + dirty check）</summary>
        public static void SetText(this TMP_Text label, string text)
        {
            if (label == null) return;
            text ??= string.Empty;
            text = Language.ResolveAutoText(text);
            if (label.text == text) return;
            label.text = text;
        }

        /// <summary>设置 TMP 多语言文字。</summary>
        public static void SetLang(this TMP_Text label, string key)
        {
            if (label == null) return;

            if (label is UI.TextMeshProEx ex)
            {
                ex.SetLang(key);
                return;
            }

            label.text = Language.Get(key);
        }

        /// <summary>设置带格式化参数的 TMP 多语言文字。</summary>
        public static void SetLang(this TMP_Text label, string key, params object[] args)
        {
            if (label == null) return;

            if (label is UI.TextMeshProEx ex)
            {
                ex.SetLang(key, args);
                return;
            }

            label.text = Language.Get(key, args);
        }

        /// <summary>设置 TMP 文字（int 重载，避免装箱）</summary>
        public static void SetText(this TMP_Text label, int value)
            => label.SetText(value.ToString());

        /// <summary>设置 TMP 文字（long 重载）</summary>
        public static void SetText(this TMP_Text label, long value)
            => label.SetText(value.ToString());

        /// <summary>设置 TMP 文字（float 重载，可指定格式）</summary>
        public static void SetText(this TMP_Text label, float value, string format = "0.##")
            => label.SetText(value.ToString(format));

        // ── Slider ─────────────────────────────────────────────────────────

        /// <summary>设置 Slider 进度（0~1，含 Clamp + dirty check）</summary>
        public static void SetProgress(this Slider slider, float value)
        {
            if (slider == null) return;
            float v = Mathf.Clamp01(value);
            if (Mathf.Abs(slider.value - v) < 0.001f) return;
            slider.value = v;
        }

        // ── Image ──────────────────────────────────────────────────────────

        /// <summary>设置 Image.fillAmount（0~1，含 dirty check），适用于圆形/扇形进度条</summary>
        public static void SetFillAmount(this Image image, float value)
        {
            if (image == null) return;
            float v = Mathf.Clamp01(value);
            if (Mathf.Abs(image.fillAmount - v) < 0.001f) return;
            image.fillAmount = v;
        }

        // ── Graphic（Image / Text / RawImage 的基类）──────────────────────

        /// <summary>设置颜色（保留原 alpha，含 dirty check）</summary>
        public static void SetColor(this Graphic graphic, Color color)
        {
            if (graphic == null) return;
            color.a = graphic.color.a;
            if (graphic.color == color) return;
            graphic.color = color;
        }

        /// <summary>设置颜色（含 alpha）</summary>
        public static void SetColorFull(this Graphic graphic, Color color)
        {
            if (graphic == null) return;
            if (graphic.color == color) return;
            graphic.color = color;
        }

        /// <summary>设置 alpha（含 dirty check）</summary>
        public static void SetAlpha(this Graphic graphic, float alpha)
        {
            if (graphic == null) return;
            float a = Mathf.Clamp01(alpha);
            if (Mathf.Abs(graphic.color.a - a) < 0.001f) return;
            Color c = graphic.color;
            c.a = a;
            graphic.color = c;
        }

        // ── CanvasGroup ────────────────────────────────────────────────────

        /// <summary>设置 CanvasGroup.alpha（含 Clamp）</summary>
        public static void SetAlpha(this CanvasGroup cg, float alpha)
        {
            if (cg == null) return;
            cg.alpha = Mathf.Clamp01(alpha);
        }

        /// <summary>
        /// 同时设置 CanvasGroup 的可交互性与射线检测。
        /// 常见场景：打开 Loading 时屏蔽所有输入。
        /// </summary>
        public static void SetInteractable(this CanvasGroup cg, bool interactable)
        {
            if (cg == null) return;
            cg.interactable    = interactable;
            cg.blocksRaycasts  = interactable;
        }

        // ── Transform ─────────────────────────────────────────────────────

        /// <summary>等比缩放（含 dirty check）</summary>
        public static void SetScale(this Transform trans, float scale)
        {
            if (trans == null) return;
            Vector3 s = new Vector3(scale, scale, scale);
            if (trans.localScale == s) return;
            trans.localScale = s;
        }

        /// <summary>非等比缩放（z 恒为 1）</summary>
        public static void SetScale(this Transform trans, Vector2 scale)
        {
            if (trans == null) return;
            trans.localScale = new Vector3(scale.x, scale.y, 1f);
        }

        // ── RectTransform ──────────────────────────────────────────────────

        /// <summary>设置 anchoredPosition（含 dirty check，阈值 0.5px）</summary>
        public static void SetAnchoredPosition(this RectTransform rect, Vector2 pos)
        {
            if (rect == null) return;
            if (Mathf.Abs(rect.anchoredPosition.x - pos.x) < 0.5f &&
                Mathf.Abs(rect.anchoredPosition.y - pos.y) < 0.5f) return;
            rect.anchoredPosition = pos;
        }

        /// <summary>设置 sizeDelta</summary>
        public static void SetSizeDelta(this RectTransform rect, Vector2 size)
        {
            if (rect == null) return;
            rect.sizeDelta = size;
        }
    }
}
