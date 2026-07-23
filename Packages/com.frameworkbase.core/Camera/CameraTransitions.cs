using System;
using UnityEngine;

namespace Framework
{
    /// <summary>
    /// 相机过渡缓动曲线。均为定义域/值域都在 [0,1] 的归一化曲线，
    /// 由 <see cref="CameraEase.Evaluate"/> 求值，供各过渡通道把线性时间进度重整为观感更自然的运动。
    /// </summary>
    public enum CameraEasing
    {
        /// <summary>匀速。</summary>
        Linear = 0,

        /// <summary>3t²−2t³：两端速度为 0，最常用的平滑起停。</summary>
        SmoothStep = 1,

        /// <summary>6t⁵−15t⁴+10t³：两端一阶二阶导都为 0，比 SmoothStep 更顺滑。</summary>
        SmootherStep = 2,

        /// <summary>t²：起步慢、越走越快（进场推进感）。</summary>
        EaseIn = 3,

        /// <summary>t(2−t)：起步快、收尾慢（贴合停靠）。</summary>
        EaseOut = 4,
    }

    /// <summary>
    /// 缓动曲线求值（纯数学，不依赖 UnityEngine，可脱离运行时单测）。
    /// 借鉴 ALCameraController 每参数独立过渡的分解思路，但以归一化缓动取代其逐帧加速度 fade，
    /// 无状态、可复用、易验证。
    /// </summary>
    public static class CameraEase
    {
        /// <summary>
        /// 把线性进度 <paramref name="t"/> 重整为缓动后进度。
        /// 越界一律钳到 [0,1]——过渡通道的外推没有语义且会导致过冲。
        /// </summary>
        /// <param name="easing">曲线类型。</param>
        /// <param name="t">线性进度（期望 [0,1]，越界自动钳制）。</param>
        /// <returns>缓动后进度，恒在 [0,1]。</returns>
        public static float Evaluate(CameraEasing easing, float t)
        {
            if (t <= 0f) return 0f;
            if (t >= 1f) return 1f;

            switch (easing)
            {
                case CameraEasing.SmoothStep:
                    return t * t * (3f - 2f * t);
                case CameraEasing.SmootherStep:
                    return t * t * t * (t * (t * 6f - 15f) + 10f);
                case CameraEasing.EaseIn:
                    return t * t;
                case CameraEasing.EaseOut:
                    return t * (2f - t);
                default:
                    return t; // Linear
            }
        }
    }

    /// <summary>
    /// 归一化过渡时钟（纯逻辑，不依赖 UnityEngine，可 EditMode 注入 dt 单测）。
    /// 只负责"从 0 到 1 的缓动进度推进"，不持有具体被过渡的值——位置/正交尺寸/视野/朝向
    /// 各通道共用同一时钟类型，把端点插值留给调用方，避免每种值类型各写一份时序逻辑。
    /// <para>
    /// dt 由外部注入而非内部读 <c>Time</c>：既让核心可脱离 PlayerLoop 测（见 <c>frameworkbase-workflow</c>
    /// 纯逻辑与 Unity API 分离的惯例），也让驱动层自由选择缩放/非缩放时间。
    /// </para>
    /// </summary>
    public sealed class CameraTransitionClock
    {
        private readonly float _duration;
        private readonly CameraEasing _easing;
        private float _elapsed;

        /// <summary>
        /// 构造一个时钟。<paramref name="duration"/> ≤0 视为瞬时（构造即完成、进度恒 1）。
        /// </summary>
        /// <param name="duration">总时长（秒）。</param>
        /// <param name="easing">缓动曲线。</param>
        public CameraTransitionClock(float duration, CameraEasing easing)
        {
            _duration = duration;
            _easing = easing;
            _elapsed = 0f;
        }

        /// <summary>是否已完成（瞬时时钟恒为 true）。</summary>
        public bool IsComplete => _duration <= 0f || _elapsed >= _duration;

        /// <summary>缓动后的归一化进度 [0,1]；完成时恒为 1，供调用方插值端点。</summary>
        public float Progress
        {
            get
            {
                if (_duration <= 0f) return 1f;
                float t = _elapsed / _duration;
                return CameraEase.Evaluate(_easing, t);
            }
        }

        /// <summary>
        /// 推进 <paramref name="dt"/> 秒（负值忽略），累计不越过 duration。
        /// </summary>
        /// <param name="dt">帧时间增量（秒）。</param>
        /// <returns>推进后是否已完成。</returns>
        public bool Advance(float dt)
        {
            if (dt > 0f) _elapsed += dt;
            if (_elapsed > _duration) _elapsed = _duration;
            return IsComplete;
        }
    }

    /// <summary>
    /// 运行时相机过渡驱动器（挂在相机上）。补齐 FrameworkBase 相机层此前只有声明式静态取景
    /// （<see cref="SceneCameraRigBase"/>）、缺运行时运镜的空档。
    /// <para>
    /// 四个互不影响、可并行的通道：位置 / 正交尺寸 / 透视视野角 / 朝向。每通道"最新过渡覆盖旧过渡"
    /// （不做 AL 那样的优先级栈——优先级是业务策略，框架只保证单通道语义清晰）。
    /// duration ≤0 即瞬时到位。运镜默认走非缩放时间，与游戏暂停解耦。
    /// </para>
    /// </summary>
    [AddComponentMenu("Framework/Camera/Camera Transition Driver")]
    public sealed class CameraTransitionDriver : MonoBehaviour
    {
        [Header("目标相机（留空则取本物体上的 Camera）")]
        [SerializeField] private Camera targetCamera;

        [Header("使用非缩放时间（运镜通常不随 timeScale 暂停）")]
        [SerializeField] private bool useUnscaledTime = true;

        private readonly Vec3Channel _move = new Vec3Channel();
        private readonly FloatChannel _ortho = new FloatChannel();
        private readonly FloatChannel _fov = new FloatChannel();
        private readonly RotationChannel _rotation = new RotationChannel();

        /// <summary>目标相机；未显式指定时回退到本物体上的 Camera 并缓存。</summary>
        public Camera TargetCamera => targetCamera != null ? targetCamera : (targetCamera = GetComponent<Camera>());

        /// <summary>是否有任一通道正在过渡。</summary>
        public bool IsMoving => _move.Active || _ortho.Active || _fov.Active || _rotation.Active;

        /// <summary>
        /// 平滑移动相机世界位置。
        /// </summary>
        /// <param name="worldPosition">目标世界坐标。</param>
        /// <param name="duration">时长（秒）；≤0 瞬时到位。</param>
        /// <param name="easing">缓动曲线。</param>
        /// <param name="onComplete">到位后回调（被新过渡打断则不触发）。</param>
        public void MoveTo(Vector3 worldPosition, float duration,
            CameraEasing easing = CameraEasing.SmoothStep, Action onComplete = null)
        {
            _move.Begin(transform.position, worldPosition, duration, easing, onComplete);
            ApplyIfInstant();
        }

        /// <summary>平滑改变正交相机尺寸（对透视相机无意义，调用方自负）。</summary>
        public void ZoomOrthographicTo(float orthographicSize, float duration,
            CameraEasing easing = CameraEasing.SmoothStep, Action onComplete = null)
        {
            _ortho.Begin(TargetCamera.orthographicSize, orthographicSize, duration, easing, onComplete);
            ApplyIfInstant();
        }

        /// <summary>平滑改变透视相机视野角（对正交相机无意义，调用方自负）。</summary>
        public void FieldOfViewTo(float fieldOfView, float duration,
            CameraEasing easing = CameraEasing.SmoothStep, Action onComplete = null)
        {
            _fov.Begin(TargetCamera.fieldOfView, fieldOfView, duration, easing, onComplete);
            ApplyIfInstant();
        }

        /// <summary>平滑旋转相机到目标朝向。</summary>
        public void RotateTo(Quaternion rotation, float duration,
            CameraEasing easing = CameraEasing.SmoothStep, Action onComplete = null)
        {
            _rotation.Begin(transform.rotation, rotation, duration, easing, onComplete);
            ApplyIfInstant();
        }

        /// <summary>
        /// 由当前位置朝向某世界点（借鉴 AL 的 Focus 控制器）。
        /// 目标点与相机重合时朝向无定义，直接忽略本次调用。
        /// </summary>
        public void FocusOn(Vector3 worldPoint, float duration,
            CameraEasing easing = CameraEasing.SmoothStep, Action onComplete = null)
        {
            Vector3 dir = worldPoint - transform.position;
            if (dir.sqrMagnitude <= Mathf.Epsilon)
                return; // 焦点与相机重合，朝向无解

            RotateTo(Quaternion.LookRotation(dir.normalized, Vector3.up), duration, easing, onComplete);
        }

        /// <summary>取消全部通道（保留当前值，不回调）。</summary>
        public void CancelAll()
        {
            _move.Cancel();
            _ortho.Cancel();
            _fov.Cancel();
            _rotation.Cancel();
        }

        /// <summary>
        /// 瞬时过渡（duration ≤0）在发起当帧立即落值，不必等下一个 LateUpdate，
        /// 避免"发起后同帧读相机仍是旧值"的时序错觉。
        /// </summary>
        private void ApplyIfInstant()
        {
            if (!IsMoving)
                TickAll(0f);
        }

        /// <summary>相机运镜放在 LateUpdate：晚于业务逻辑改位置，避免同帧被覆盖后又抖回。</summary>
        private void LateUpdate()
        {
            if (!IsMoving)
                return;

            float dt = useUnscaledTime ? Time.unscaledDeltaTime : Time.deltaTime;
            TickAll(dt);
        }

        /// <summary>推进并应用四通道；dt=0 时只落瞬时值不推进时钟。</summary>
        private void TickAll(float dt)
        {
            if (_move.Tick(dt, out Vector3 pos))
                transform.position = pos;
            if (_rotation.Tick(dt, out Quaternion rot))
                transform.rotation = rot;
            if (_ortho.Tick(dt, out float size))
                TargetCamera.orthographicSize = size;
            if (_fov.Tick(dt, out float fov))
                TargetCamera.fieldOfView = fov;
        }

        // ── 各通道：端点 + 共用时钟 + 完成回调；语义完全一致，仅插值类型不同 ──────────

        /// <summary>Vector3 通道（位置）。</summary>
        private sealed class Vec3Channel
        {
            private Vector3 _from;
            private Vector3 _to;
            private CameraTransitionClock _clock;
            private Action _onComplete;

            public bool Active { get; private set; }

            public void Begin(Vector3 from, Vector3 to, float duration, CameraEasing easing, Action onComplete)
            {
                _from = from;
                _to = to;
                _clock = new CameraTransitionClock(duration, easing);
                _onComplete = onComplete;
                Active = true;
            }

            public void Cancel()
            {
                Active = false;
                _onComplete = null;
            }

            /// <summary>推进并输出当前值；返回是否需要应用（未激活返回 false）。</summary>
            public bool Tick(float dt, out Vector3 value)
            {
                if (!Active)
                {
                    value = _to;
                    return false;
                }

                bool done = _clock.Advance(dt);
                value = Vector3.LerpUnclamped(_from, _to, _clock.Progress);
                if (done)
                    Complete();
                return true;
            }

            private void Complete()
            {
                Active = false;
                Action cb = _onComplete;
                _onComplete = null;
                cb?.Invoke(); // 回调放在清状态之后，允许回调里发起下一段过渡
            }
        }

        /// <summary>float 通道（正交尺寸 / 视野角）。</summary>
        private sealed class FloatChannel
        {
            private float _from;
            private float _to;
            private CameraTransitionClock _clock;
            private Action _onComplete;

            public bool Active { get; private set; }

            public void Begin(float from, float to, float duration, CameraEasing easing, Action onComplete)
            {
                _from = from;
                _to = to;
                _clock = new CameraTransitionClock(duration, easing);
                _onComplete = onComplete;
                Active = true;
            }

            public void Cancel()
            {
                Active = false;
                _onComplete = null;
            }

            public bool Tick(float dt, out float value)
            {
                if (!Active)
                {
                    value = _to;
                    return false;
                }

                bool done = _clock.Advance(dt);
                value = Mathf.LerpUnclamped(_from, _to, _clock.Progress);
                if (done)
                    Complete();
                return true;
            }

            private void Complete()
            {
                Active = false;
                Action cb = _onComplete;
                _onComplete = null;
                cb?.Invoke();
            }
        }

        /// <summary>Quaternion 通道（朝向）。</summary>
        private sealed class RotationChannel
        {
            private Quaternion _from;
            private Quaternion _to;
            private CameraTransitionClock _clock;
            private Action _onComplete;

            public bool Active { get; private set; }

            public void Begin(Quaternion from, Quaternion to, float duration, CameraEasing easing, Action onComplete)
            {
                _from = from;
                _to = to;
                _clock = new CameraTransitionClock(duration, easing);
                _onComplete = onComplete;
                Active = true;
            }

            public void Cancel()
            {
                Active = false;
                _onComplete = null;
            }

            public bool Tick(float dt, out Quaternion value)
            {
                if (!Active)
                {
                    value = _to;
                    return false;
                }

                bool done = _clock.Advance(dt);
                value = Quaternion.SlerpUnclamped(_from, _to, _clock.Progress);
                if (done)
                    Complete();
                return true;
            }

            private void Complete()
            {
                Active = false;
                Action cb = _onComplete;
                _onComplete = null;
                cb?.Invoke();
            }
        }
    }
}
