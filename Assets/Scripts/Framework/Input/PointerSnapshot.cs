using UnityEngine;

namespace Framework.Input
{
    /// <summary>
    /// 单帧指针快照，统一鼠标与触控语义。
    /// </summary>
    public readonly struct PointerSnapshot
    {
        /// <summary>无效指针快照。</summary>
        public static readonly PointerSnapshot None = new PointerSnapshot(-1, Vector2.zero, Vector2.zero, false, false, false, PointerPhase.None);

        /// <summary>
        /// 创建指针快照。
        /// </summary>
        /// <param name="pointerId">指针 Id；鼠标固定为 0，触控为 fingerId。</param>
        /// <param name="position">屏幕坐标。</param>
        /// <param name="delta">相对上一帧的屏幕位移。</param>
        /// <param name="isPressed">当前是否处于按下态。</param>
        /// <param name="wasPressedThisFrame">本帧是否刚按下。</param>
        /// <param name="wasReleasedThisFrame">本帧是否刚抬起。</param>
        /// <param name="phase">本帧指针相位。</param>
        public PointerSnapshot(
            int pointerId,
            Vector2 position,
            Vector2 delta,
            bool isPressed,
            bool wasPressedThisFrame,
            bool wasReleasedThisFrame,
            PointerPhase phase)
        {
            PointerId = pointerId;
            Position = position;
            Delta = delta;
            IsPressed = isPressed;
            WasPressedThisFrame = wasPressedThisFrame;
            WasReleasedThisFrame = wasReleasedThisFrame;
            Phase = phase;
        }

        /// <summary>指针 Id；无效时为 -1。</summary>
        public int PointerId { get; }

        /// <summary>当前屏幕坐标。</summary>
        public Vector2 Position { get; }

        /// <summary>相对上一帧的屏幕位移。</summary>
        public Vector2 Delta { get; }

        /// <summary>当前是否处于按下态。</summary>
        public bool IsPressed { get; }

        /// <summary>本帧是否刚按下。</summary>
        public bool WasPressedThisFrame { get; }

        /// <summary>本帧是否刚抬起。</summary>
        public bool WasReleasedThisFrame { get; }

        /// <summary>本帧指针相位。</summary>
        public PointerPhase Phase { get; }

        /// <summary>是否存在有效指针。</summary>
        public bool IsValid => PointerId >= 0;
    }
}
