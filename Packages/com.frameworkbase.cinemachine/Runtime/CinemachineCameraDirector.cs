using System.Collections.Generic;
using Framework;
using UnityEngine;
#if FRAMEWORKBASE_CINEMACHINE
using Cinemachine;
#endif

namespace Framework.Integrations.Camera
{
    /// <summary>
    /// Cinemachine 版相机调度实现（<b>骨架</b>），对接主干 <see cref="ICameraDirector"/>。
    ///
    /// <para>本包<b>不含</b> Cinemachine 包本身。所有 Cinemachine 调用锁在编译宏
    /// <c>FRAMEWORKBASE_CINEMACHINE</c> 之后——工程装了 <c>com.unity.cinemachine</c> 时经 asmdef
    /// <c>versionDefines</c> 自动开启；未启用时整类退化为无操作，无 Cinemachine 也能编译。</para>
    ///
    /// <para>切换语义：命名镜头由 <see cref="CinemachineDirectorCamera"/> 组件自登记；<see cref="Activate"/>
    /// 把目标虚拟相机优先级抬高、其余压低，真正的过渡（瞬切/blend/时长）由 CinemachineBrain 的 blend 设置决定
    /// ——框架不把 blend 语义泄漏进主干契约。震屏走 Cinemachine Impulse。</para>
    ///
    /// <para>骨架针对 Cinemachine 2.x（程序集 <c>Cinemachine</c>、类型 <c>CinemachineVirtualCamera</c> /
    /// <c>CinemachineImpulseSource</c>）。若工程用 Cinemachine 3.x（程序集 <c>Unity.Cinemachine</c>、
    /// 类型 <c>CinemachineCamera</c>），按 README 调整类型名与 asmdef 引用。</para>
    /// </summary>
    public sealed class CinemachineCameraDirector : ICameraDirector
    {
        // 当前 live 镜头 id；两套编译路径共用（无 Cinemachine 时恒为 null）。
        private string _activeId;

#if FRAMEWORKBASE_CINEMACHINE
        // 命名镜头登记表：id -> 虚拟相机。由 CinemachineDirectorCamera 组件在启用/禁用时自登记/注销。
        private readonly Dictionary<string, CinemachineVirtualCamera> _cameras =
            new Dictionary<string, CinemachineVirtualCamera>();

        // 震屏冲击源（可选）：由场景注入；无则 Shake 无操作。
        private CinemachineImpulseSource _impulseSource;

        // 优先级切换约定：激活者抬到 Active、其余压到 Standby，Brain 据此选出 live 相机并按其 blend 过渡。
        private const int ActivePriority = 100;
        private const int StandbyPriority = 10;
#endif

        /// <inheritdoc/>
        public string ActiveCameraId => _activeId;

        /// <inheritdoc/>
        public bool IsRegistered(string cameraId)
        {
#if FRAMEWORKBASE_CINEMACHINE
            return !string.IsNullOrEmpty(cameraId) && _cameras.ContainsKey(cameraId);
#else
            _ = cameraId;
            return false;
#endif
        }

        /// <inheritdoc/>
        public void Activate(string cameraId)
        {
#if FRAMEWORKBASE_CINEMACHINE
            if (string.IsNullOrEmpty(cameraId) ||
                !_cameras.TryGetValue(cameraId, out CinemachineVirtualCamera target) || target == null)
            {
                Debug.LogWarning($"[CinemachineDirector] 未登记镜头 id={cameraId}，忽略 Activate");
                return;
            }

            // 全部压到 Standby，仅目标抬到 Active；CinemachineBrain 会挑出最高优先级并按其 blend 过渡。
            foreach (KeyValuePair<string, CinemachineVirtualCamera> kv in _cameras)
            {
                if (kv.Value != null)
                    kv.Value.Priority = StandbyPriority;
            }
            target.Priority = ActivePriority;
            _activeId = cameraId;
#else
            _ = cameraId;
#endif
        }

        /// <inheritdoc/>
        public void Shake(float amplitude, float duration)
        {
#if FRAMEWORKBASE_CINEMACHINE
            // TODO(cinemachine): duration 由 ImpulseSource 的 ImpulseDefinition（时长/衰减曲线）承载，
            // 这里只按幅度触发一次冲击；需要精确时长控制时在场景侧配置对应 Impulse Definition。
            if (_impulseSource != null)
                _impulseSource.GenerateImpulseWithForce(amplitude);
            else
                Debug.LogWarning("[CinemachineDirector] 未设置 ImpulseSource，Shake 无操作（SetImpulseSource 注入）");
            _ = duration;
#else
            _ = amplitude;
            _ = duration;
#endif
        }

#if FRAMEWORKBASE_CINEMACHINE
        /// <summary>登记命名镜头（由 <see cref="CinemachineDirectorCamera"/> 调用）。</summary>
        internal void RegisterCamera(string id, CinemachineVirtualCamera vcam)
        {
            if (!string.IsNullOrEmpty(id) && vcam != null)
                _cameras[id] = vcam;
        }

        /// <summary>注销命名镜头（组件禁用时）。</summary>
        internal void UnregisterCamera(string id)
        {
            if (!string.IsNullOrEmpty(id))
            {
                _cameras.Remove(id);
                if (_activeId == id)
                    _activeId = null;
            }
        }

        /// <summary>注入震屏冲击源（场景搭建期）。</summary>
        public void SetImpulseSource(CinemachineImpulseSource source) => _impulseSource = source;
#endif
    }

    /// <summary>
    /// 命名虚拟相机自登记组件（<b>骨架</b>）：挂在每台 Cinemachine 虚拟相机上，
    /// 启用时把自己按 <see cref="cameraId"/> 登记进当前 <see cref="CinemachineCameraDirector"/>，禁用时注销。
    /// 未启用 <c>FRAMEWORKBASE_CINEMACHINE</c> 时为惰性组件（不引用 Cinemachine 类型，仍可编译）。
    /// </summary>
    [AddComponentMenu("Framework/Camera/Cinemachine Director Camera")]
#if FRAMEWORKBASE_CINEMACHINE
    [RequireComponent(typeof(CinemachineVirtualCamera))]
#endif
    public sealed class CinemachineDirectorCamera : MonoBehaviour
    {
        [Header("镜头标识（业务经 Cameras.Director.Activate(id) 切换）")]
        [SerializeField] private string cameraId;

        private void OnEnable()
        {
#if FRAMEWORKBASE_CINEMACHINE
            if (Cameras.Director is CinemachineCameraDirector director)
                director.RegisterCamera(cameraId, GetComponent<CinemachineVirtualCamera>());
#endif
        }

        private void OnDisable()
        {
#if FRAMEWORKBASE_CINEMACHINE
            if (Cameras.Director is CinemachineCameraDirector director)
                director.UnregisterCamera(cameraId);
#endif
        }
    }

    /// <summary>
    /// Cinemachine 相机调度自注册入口。仅在启用 <c>FRAMEWORKBASE_CINEMACHINE</c> 时注册——
    /// 未装 Cinemachine 时不注册，主干保持 <see cref="NullCameraDirector"/> 兜底、零副作用。
    /// </summary>
    public static class CinemachineCameraBootstrap
    {
#if FRAMEWORKBASE_CINEMACHINE
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        private static void AutoRegister()
        {
            // 场景 MonoBehaviour Awake 之前注入，使各 CinemachineDirectorCamera 的 OnEnable 能登记到本实例。
            Cameras.Register(new CinemachineCameraDirector());
        }
#endif
    }
}
