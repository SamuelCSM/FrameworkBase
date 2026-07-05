using UnityEngine;

namespace Framework.Input
{
    /// <summary>
    /// Legacy Input Manager 双指缩放/平移手势实现。
    /// </summary>
    /// <remarks>
    /// 移动端：双指捏合缩放、双指中点平移。
    /// PC：滚轮缩放、中键拖拽平移。
    /// </remarks>
    public sealed class LegacyPinchPanGestureSource : IPinchPanGestureSource
    {
        /// <summary>PC 滚轮缩放灵敏度。</summary>
        private readonly float scrollWheelZoomSensitivity;

        /// <summary>PC 中键平移像素倍率。</summary>
        private readonly float middleMousePanSensitivity;

        /// <summary>
        /// 创建 Legacy 手势采样源。
        /// </summary>
        /// <param name="scrollWheelZoomSensitivity">滚轮缩放灵敏度。</param>
        /// <param name="middleMousePanSensitivity">中键平移灵敏度。</param>
        public LegacyPinchPanGestureSource(float scrollWheelZoomSensitivity = 0.15f, float middleMousePanSensitivity = 1f)
        {
            this.scrollWheelZoomSensitivity = scrollWheelZoomSensitivity;
            this.middleMousePanSensitivity = middleMousePanSensitivity;
        }

        /// <inheritdoc/>
        public PinchPanFrame CurrentFrame { get; private set; } = PinchPanFrame.None;

        /// <inheritdoc/>
        public void Collect()
        {
            if (UnityEngine.Input.touchSupported && UnityEngine.Input.touchCount >= 2)
            {
                CurrentFrame = CollectFromTouch();
                return;
            }

            CurrentFrame = CollectFromMouse();
        }

        /// <summary>
        /// 从双指触控采样缩放与平移。
        /// </summary>
        private static PinchPanFrame CollectFromTouch()
        {
            Touch touch0 = UnityEngine.Input.GetTouch(0);
            Touch touch1 = UnityEngine.Input.GetTouch(1);

            Vector2 prevPos0 = touch0.position - touch0.deltaPosition;
            Vector2 prevPos1 = touch1.position - touch1.deltaPosition;
            float previousDistance = Vector2.Distance(prevPos0, prevPos1);
            float currentDistance = Vector2.Distance(touch0.position, touch1.position);

            float zoomFactor = 1f;
            if (previousDistance > 1f)
            {
                zoomFactor = currentDistance / previousDistance;
            }

            Vector2 panDelta = (touch0.deltaPosition + touch1.deltaPosition) * 0.5f;
            return new PinchPanFrame(true, zoomFactor, panDelta);
        }

        /// <summary>
        /// 从滚轮与中键采样 PC 等效手势。
        /// </summary>
        private PinchPanFrame CollectFromMouse()
        {
            float zoomFactor = 1f;
            Vector2 panDelta = Vector2.zero;
            bool isActive = false;

            float scroll = UnityEngine.Input.GetAxis("Mouse ScrollWheel");
            if (Mathf.Abs(scroll) > 0.0001f)
            {
                zoomFactor = 1f + scroll * scrollWheelZoomSensitivity;
                isActive = true;
            }

            if (UnityEngine.Input.GetMouseButton(2))
            {
                panDelta = new Vector2(UnityEngine.Input.GetAxis("Mouse X"), UnityEngine.Input.GetAxis("Mouse Y")) * middleMousePanSensitivity;
                isActive = true;
            }

            return isActive ? new PinchPanFrame(true, zoomFactor, panDelta) : PinchPanFrame.None;
        }
    }
}
