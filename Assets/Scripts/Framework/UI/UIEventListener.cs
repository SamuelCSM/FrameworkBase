using System;
using Framework.Core;
using UnityEngine;
using UnityEngine.EventSystems;

namespace Framework
{
    /// <summary>
    /// UI 事件拦截层
    ///
    /// 职责：
    ///   为任意 GameObject 提供完整的 Pointer 事件支持，包括：
    ///   Click（含防连击）、LongPress（长按）、PointerDown/Up、
    ///   BeginDrag/Drag/EndDrag、PointerEnter/Exit。
    ///
    /// 使用方式：
    ///   不要手动 AddComponent，统一通过 UIExtensions 扩展方法调用：
    ///     comp.AddLongPress(() => OnLongPress());
    ///     comp.AddPointerDown(e => OnDown(e));
    ///     comp.AddDrag(onBegin, onDrag, onEnd);
    ///
    ///   Button 组件的普通点击仍通过 btn.AddClick() 绑定（走 Button.onClick），
    ///   本类负责 Button 不支持的事件类型（长按、拖拽、指针进出等）。
    ///
    /// 设计说明：
    ///   所有回调字段由 UIExtensions 在框架内部赋值，业务层不直接操作本类。
    ///   UITracker.Record 在 UIExtensions 层统一调用，本类只做事件分发。
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class UIEventListener : MonoBehaviour,
        IPointerClickHandler,
        IPointerDownHandler,
        IPointerUpHandler,
        IBeginDragHandler,
        IDragHandler,
        IEndDragHandler,
        IPointerEnterHandler,
        IPointerExitHandler
    {
        // ── 配置 ──────────────────────────────────────────────────────────
        /// <summary>防连击冷却时间（秒），仅对非 Button 的点击生效</summary>
        public float ClickCooldown      = 0.3f;

        /// <summary>长按触发阈值（秒）</summary>
        public float LongPressThreshold = 0.5f;

        // ── 回调（由 UIExtensions 赋值，不对外暴露）──────────────────────
        internal Action<PointerEventData> clickHandler;
        internal Action<PointerEventData> longPressHandler;
        internal Action<PointerEventData> pointerDownHandler;
        internal Action<PointerEventData> pointerUpHandler;
        internal Action<PointerEventData> beginDragHandler;
        internal Action<PointerEventData> dragHandler;
        internal Action<PointerEventData> endDragHandler;
        internal Action<PointerEventData> pointerEnterHandler;
        internal Action<PointerEventData> pointerExitHandler;

        // ── 状态 ──────────────────────────────────────────────────────────
        private float      _lastClickTime = -999f;
        private bool       _isPointerDown;
        private bool       _isDragging;

        /// <summary>长按定时器ID（-1 表示无）。由 TimerManager 统一调度，替代旧协程实现。</summary>
        private int               _longPressTimerId = -1;

        /// <summary>长按触发时回传的指针事件数据，按下时记录。</summary>
        private PointerEventData  _longPressEventData;

        // ── 静态工厂 ──────────────────────────────────────────────────────
        /// <summary>获取或创建目标 GameObject 上的 UIEventListener（按需挂载）</summary>
        public static UIEventListener Get(GameObject go)
        {
            if (go == null) return null;
            var listener = go.GetComponent<UIEventListener>();
            return listener != null ? listener : go.AddComponent<UIEventListener>();
        }

        // ── IPointerClickHandler ──────────────────────────────────────────
        public void OnPointerClick(PointerEventData eventData)
        {
            // Button 组件的点击由 Button.onClick 处理（见 UIExtensions.AddClick）
            // 此处只处理非 Button GameObject 的点击
            if (GetComponent<UnityEngine.UI.Button>() != null) return;
            if (_isDragging) return;

            float now = Time.unscaledTime;
            if (now - _lastClickTime < ClickCooldown) return;
            _lastClickTime = now;

            clickHandler?.Invoke(eventData);
        }

        // ── IPointerDownHandler ───────────────────────────────────────────
        public void OnPointerDown(PointerEventData eventData)
        {
            _isPointerDown = true;
            _isDragging    = false;

            pointerDownHandler?.Invoke(eventData);

            if (longPressHandler != null)
            {
                CancelLongPress(); // 防御：清理可能残留的上一个长按定时器
                _longPressEventData = eventData;
                _longPressTimerId = GameEntry.Timer?.AddTimer(OnLongPressElapsed, LongPressThreshold, useRealTime: true) ?? -1;
            }
        }

        // ── IPointerUpHandler ─────────────────────────────────────────────
        public void OnPointerUp(PointerEventData eventData)
        {
            _isPointerDown = false;
            CancelLongPress();
            pointerUpHandler?.Invoke(eventData);
        }

        // ── IBeginDragHandler ─────────────────────────────────────────────
        public void OnBeginDrag(PointerEventData eventData)
        {
            _isDragging = true;
            CancelLongPress();
            beginDragHandler?.Invoke(eventData);
        }

        // ── IDragHandler ──────────────────────────────────────────────────
        public void OnDrag(PointerEventData eventData)
            => dragHandler?.Invoke(eventData);

        // ── IEndDragHandler ───────────────────────────────────────────────
        public void OnEndDrag(PointerEventData eventData)
        {
            _isDragging = false;
            endDragHandler?.Invoke(eventData);
        }

        // ── IPointerEnterHandler / IPointerExitHandler ────────────────────
        public void OnPointerEnter(PointerEventData eventData)
            => pointerEnterHandler?.Invoke(eventData);

        public void OnPointerExit(PointerEventData eventData)
            => pointerExitHandler?.Invoke(eventData);

        // ── 长按触发 ──────────────────────────────────────────────────────
        /// <summary>长按定时器到点回调：仍处于按下且未拖拽时触发长按。</summary>
        private void OnLongPressElapsed()
        {
            _longPressTimerId = -1; // 一次性定时器已自行结束
            if (_isPointerDown && !_isDragging)
                longPressHandler?.Invoke(_longPressEventData);
        }

        // ── 生命周期清理 ──────────────────────────────────────────────────
        private void OnDisable()
        {
            _isPointerDown = false;
            _isDragging    = false;
            CancelLongPress();
        }

        private void CancelLongPress()
        {
            if (_longPressTimerId == -1) return;
            GameEntry.Timer?.CancelTimer(_longPressTimerId);
            _longPressTimerId = -1;
        }
    }
}
