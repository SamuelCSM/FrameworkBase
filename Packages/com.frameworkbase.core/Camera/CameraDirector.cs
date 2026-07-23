namespace Framework
{
    /// <summary>
    /// 运行时相机调度缝（框架 L1 契约，<b>不依赖任何具体相机方案</b>）。
    /// <para>
    /// 定位：框架只定义"业务想对镜头下什么指令"（激活命名镜头、震屏），把"用什么实现"
    /// （Cinemachine 虚拟相机 / 项目自研 / 无）留给可选扩展包或项目层——与 PrimeTween(ADR-007)、
    /// 崩溃后端、云存档同一模式：<b>主干接口 + 默认兜底 + 实现进扩展包</b>。框架核心因此零 Cinemachine 依赖。
    /// </para>
    /// <para>
    /// 命名镜头的"登记"（哪个 id 对应哪台虚拟相机）属实现细节，不进本契约——由具体实现（如
    /// Cinemachine 扩展包）用自己的方式在场景搭建期登记；本接口只管"指挥"。
    /// </para>
    /// </summary>
    public interface ICameraDirector
    {
        /// <summary>当前 live 镜头 id；无/未知返回 null。</summary>
        string ActiveCameraId { get; }

        /// <summary>是否已登记该命名镜头（业务可据此在 <see cref="Activate"/> 前自保）。</summary>
        /// <param name="cameraId">镜头标识。</param>
        bool IsRegistered(string cameraId);

        /// <summary>
        /// 激活命名镜头为当前 live 镜头。过渡方式（瞬切 / blend / 时长）由具体实现与其配置决定——
        /// 框架不把 Cinemachine 的 blend 语义泄漏进契约。未登记的 id 由实现决定忽略或告警。
        /// </summary>
        /// <param name="cameraId">目标镜头标识。</param>
        void Activate(string cameraId);

        /// <summary>
        /// 触发一次相机震屏/冲击（打击感、爆炸反馈等）。无震屏能力的实现可空操作。
        /// </summary>
        /// <param name="amplitude">强度（实现自解释量纲，通常正比于位移幅度）。</param>
        /// <param name="duration">持续时长（秒）。</param>
        void Shake(float amplitude, float duration);
    }

    /// <summary>
    /// 空相机调度器：未安装任何实现时的默认兜底，所有指令无操作。
    /// 保证业务经 <see cref="Cameras.Director"/> 调用永不 NullReference，也不强迫项目必须接一套相机方案。
    /// </summary>
    public sealed class NullCameraDirector : ICameraDirector
    {
        /// <summary>共享单例（无状态，可复用）。</summary>
        public static readonly NullCameraDirector Instance = new NullCameraDirector();

        private NullCameraDirector() { }

        /// <inheritdoc/>
        public string ActiveCameraId => null;

        /// <inheritdoc/>
        public bool IsRegistered(string cameraId) => false;

        /// <inheritdoc/>
        public void Activate(string cameraId) { }

        /// <inheritdoc/>
        public void Shake(float amplitude, float duration) { }
    }

    /// <summary>
    /// 相机调度访问点（ADR-008 风格的全局服务缝）。默认 <see cref="NullCameraDirector"/>；
    /// 可选的 Cinemachine 扩展包或项目层在启动期经 <see cref="Register"/> 注入真实实现。
    /// 业务经 <see cref="Director"/> 下达镜头指令，无需感知底层用的是什么相机方案。
    /// </summary>
    public static class Cameras
    {
        /// <summary>当前生效的相机调度器；恒非 null（未注入时为 <see cref="NullCameraDirector"/>）。</summary>
        public static ICameraDirector Director { get; private set; } = NullCameraDirector.Instance;

        /// <summary>
        /// 注入相机调度实现（扩展包/项目层在启动期调用）。传 null 视为撤销、回到空兜底。
        /// </summary>
        /// <param name="director">实现实例；null 回退空兜底。</param>
        public static void Register(ICameraDirector director)
        {
            Director = director ?? NullCameraDirector.Instance;
        }

        /// <summary>撤销当前实现，回到空兜底（进程退出/测试隔离用）。</summary>
        public static void Reset()
        {
            Director = NullCameraDirector.Instance;
        }
    }
}
