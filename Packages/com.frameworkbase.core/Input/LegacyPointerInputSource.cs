using System;
using System.Collections.Generic;
using UnityEngine;

namespace Framework.Input
{
    /// <summary>
    /// Legacy Input Manager 指针采样实现，统一鼠标与触控。
    /// </summary>
    public sealed class LegacyPointerInputSource : IPointerInputSource
    {
        /// <summary>鼠标指针 Id。</summary>
        private const int MousePointerId = 0;

        /// <summary>上一帧鼠标位置，用于计算 Delta。</summary>
        private Vector2 lastMousePosition;

        /// <summary>当前主指针 Id，触控模式下在按下期间保持稳定。</summary>
        private int primaryPointerId = -1;

        /// <summary>上一帧各触控位置，用于计算 Delta。</summary>
        private readonly Dictionary<int, Vector2> lastTouchPositions = new Dictionary<int, Vector2>(4);

        /// <inheritdoc/>
        public PointerSnapshot PrimaryPointer { get; private set; } = PointerSnapshot.None;

        /// <inheritdoc/>
        public int ActivePointerCount { get; private set; }

        /// <inheritdoc/>
        public bool IsMultiPointerActive => ActivePointerCount >= 2;

        /// <inheritdoc/>
        public void Collect()
        {
            if (ShouldUseTouchInput())
            {
                CollectFromTouch();
                return;
            }

            CollectFromMouse();
        }

        /// <summary>
        /// 判断是否应使用触控采样，避免移动端 Touch 与模拟鼠标重复计数。
        /// </summary>
        /// <returns>应使用触控时返回 true。</returns>
        private static bool ShouldUseTouchInput()
        {
            return UnityEngine.Input.touchSupported && UnityEngine.Input.touchCount > 0;
        }

        /// <summary>
        /// 从鼠标采样主指针。
        /// </summary>
        private void CollectFromMouse()
        {
            ActivePointerCount = UnityEngine.Input.GetMouseButton(0) ? 1 : 0;
            primaryPointerId = MousePointerId;

            Vector2 position = UnityEngine.Input.mousePosition;
            Vector2 delta = position - lastMousePosition;
            lastMousePosition = position;

            bool isPressed = UnityEngine.Input.GetMouseButton(0);
            bool wasPressed = UnityEngine.Input.GetMouseButtonDown(0);
            bool wasReleased = UnityEngine.Input.GetMouseButtonUp(0);
            PointerPhase phase = ResolvePhase(wasPressed, wasReleased, isPressed, delta);

            PrimaryPointer = new PointerSnapshot(
                MousePointerId,
                position,
                delta,
                isPressed,
                wasPressed,
                wasReleased,
                phase);
        }

        /// <summary>
        /// 从触控采样主指针，并维护主指针 Id。
        /// </summary>
        private void CollectFromTouch()
        {
            ActivePointerCount = UnityEngine.Input.touchCount;
            HashSet<int> activeIds = null;

            if (primaryPointerId >= 0)
            {
                bool primaryStillActive = false;
                for (int i = 0; i < UnityEngine.Input.touchCount; i++)
                {
                    if (UnityEngine.Input.GetTouch(i).fingerId == primaryPointerId)
                    {
                        primaryStillActive = true;
                        break;
                    }
                }

                if (!primaryStillActive)
                {
                    primaryPointerId = -1;
                }
            }

            Touch? primaryTouch = null;
            for (int i = 0; i < UnityEngine.Input.touchCount; i++)
            {
                Touch touch = UnityEngine.Input.GetTouch(i);
                if (primaryPointerId < 0 && (touch.phase == TouchPhase.Began || touch.phase == TouchPhase.Stationary || touch.phase == TouchPhase.Moved))
                {
                    primaryPointerId = touch.fingerId;
                }

                if (touch.fingerId == primaryPointerId)
                {
                    primaryTouch = touch;
                }

                activeIds ??= new HashSet<int>();
                activeIds.Add(touch.fingerId);
            }

            PruneTouchCache(activeIds);

            if (!primaryTouch.HasValue)
            {
                PrimaryPointer = PointerSnapshot.None;
                return;
            }

            Touch resolvedTouch = primaryTouch.Value;
            Vector2 position = resolvedTouch.position;
            lastTouchPositions.TryGetValue(resolvedTouch.fingerId, out Vector2 lastPosition);
            Vector2 delta = position - lastPosition;
            lastTouchPositions[resolvedTouch.fingerId] = position;

            bool isPressed = resolvedTouch.phase != TouchPhase.Ended && resolvedTouch.phase != TouchPhase.Canceled;
            bool wasPressed = resolvedTouch.phase == TouchPhase.Began;
            bool wasReleased = resolvedTouch.phase == TouchPhase.Ended || resolvedTouch.phase == TouchPhase.Canceled;
            PointerPhase phase = ResolveTouchPhase(resolvedTouch.phase, delta);

            PrimaryPointer = new PointerSnapshot(
                resolvedTouch.fingerId,
                position,
                delta,
                isPressed,
                wasPressed,
                wasReleased,
                phase);
        }

        /// <summary>
        /// 清理已抬起触控的缓存位置。
        /// </summary>
        /// <param name="activeIds">当前仍活跃的触控 Id 集合。</param>
        private void PruneTouchCache(HashSet<int> activeIds)
        {
            if (activeIds == null || lastTouchPositions.Count == 0)
            {
                return;
            }

            List<int> staleIds = null;
            foreach (KeyValuePair<int, Vector2> pair in lastTouchPositions)
            {
                if (!activeIds.Contains(pair.Key))
                {
                    staleIds ??= new List<int>(2);
                    staleIds.Add(pair.Key);
                }
            }

            if (staleIds == null)
            {
                return;
            }

            for (int i = 0; i < staleIds.Count; i++)
            {
                lastTouchPositions.Remove(staleIds[i]);
            }
        }

        /// <summary>
        /// 根据鼠标状态解析指针相位。
        /// </summary>
        private static PointerPhase ResolvePhase(bool wasPressed, bool wasReleased, bool isPressed, Vector2 delta)
        {
            if (wasPressed)
            {
                return PointerPhase.Began;
            }

            if (wasReleased)
            {
                return PointerPhase.Ended;
            }

            if (isPressed && delta.sqrMagnitude > 0.01f)
            {
                return PointerPhase.Moved;
            }

            return isPressed ? PointerPhase.None : PointerPhase.None;
        }

        /// <summary>
        /// 根据 Unity 触控相位解析统一指针相位。
        /// </summary>
        private static PointerPhase ResolveTouchPhase(TouchPhase touchPhase, Vector2 delta)
        {
            switch (touchPhase)
            {
                case TouchPhase.Began:
                    return PointerPhase.Began;
                case TouchPhase.Ended:
                    return PointerPhase.Ended;
                case TouchPhase.Canceled:
                    return PointerPhase.Canceled;
                case TouchPhase.Moved:
                    return PointerPhase.Moved;
                case TouchPhase.Stationary:
                    return delta.sqrMagnitude > 0.01f ? PointerPhase.Moved : PointerPhase.None;
                default:
                    return PointerPhase.None;
            }
        }
    }
}
