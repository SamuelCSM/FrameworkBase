namespace Framework.Core
{
    /// <summary>
    /// 框架组件抽象基类
    /// 所有Manager继承此类，定义统一的生命周期接口
    /// </summary>
    public abstract class FrameworkComponent
    {
        /// <summary>
        /// 初始化（在GameEntry.Awake中调用）
        /// </summary>
        public virtual void OnInit()
        {
        }

        /// <summary>
        /// 每帧更新（在GameEntry.Update中调用）
        /// </summary>
        /// <param name="deltaTime">帧间隔时间</param>
        public virtual void OnUpdate(float deltaTime)
        {
        }

        /// <summary>
        /// 延迟更新（在GameEntry.LateUpdate中调用）
        /// </summary>
        /// <param name="deltaTime">帧间隔时间</param>
        public virtual void OnLateUpdate(float deltaTime)
        {
        }

        /// <summary>
        /// 固定更新（在GameEntry.FixedUpdate中调用）
        /// </summary>
        /// <param name="fixedDeltaTime">固定时间间隔</param>
        public virtual void OnFixedUpdate(float fixedDeltaTime)
        {
        }

        /// <summary>
        /// 关闭清理（在GameEntry.OnApplicationQuit中调用）
        /// </summary>
        public virtual void OnShutdown()
        {
        }

        /// <summary>
        /// 应用程序暂停/恢复（在GameEntry.OnApplicationPause中调用）
        /// 场景：切后台、来电话、息屏
        /// 典型用途：暂停/恢复音频、断开/重连网络、暂停定时器
        /// </summary>
        /// <param name="isPaused">true=进入后台，false=回到前台</param>
        public virtual void OnApplicationPause(bool isPaused)
        {
        }

        /// <summary>
        /// 应用程序焦点变化（在GameEntry.OnApplicationFocus中调用）
        /// 场景：弹出系统弹窗、切回游戏
        /// 典型用途：暂停游戏逻辑、刷新UI状态
        /// </summary>
        /// <param name="hasFocus">true=获得焦点，false=失去焦点</param>
        public virtual void OnApplicationFocus(bool hasFocus)
        {
        }

        /// <summary>
        /// 系统低内存警告（在GameEntry挂接的 Application.lowMemory 中调用）
        /// 场景：移动端内存吃紧，系统即将开始杀进程
        /// 典型用途：清空对象池、丢弃可重建缓存（图集/配置缓存等）
        /// </summary>
        public virtual void OnLowMemory()
        {
        }
    }

    /// <summary>
    /// 带 <see cref="Instance"/> 访问器的框架组件基类（CRTP）。
    ///
    /// 用途：让「模块内兄弟类取本模块 Manager」从 <c>GameEntry.&lt;自己模块&gt;</c>
    /// （依赖 Core 门面）改为 <c>&lt;Manager&gt;.Instance</c>（同模块内直取），消除
    /// ADR-002 3a 点名的伪耦合——这是未来沿 DAG 切 asmdef 的强制前置（见 ADR-003）。
    ///
    /// <para>命名遵循 ADR-003：Manager 是 GameEntry 拥有的<b>具体硬单例</b>（不可替换），
    /// 故用 <c>.Instance</c> 而非 <c>.Shared</c>（后者专指带注入缝的可替换接口默认）。</para>
    ///
    /// <para>登记时机：<see cref="GameEntry"/> 经 <c>new T()</c> 构造 Manager 时即登记，
    /// 早于任何 <c>OnInit</c>。重复构造以最后一次为准（沿用框架单 GameEntry 假设）。</para>
    /// </summary>
    /// <typeparam name="T">具体 Manager 类型（自指约束 CRTP）。</typeparam>
    public abstract class FrameworkComponent<T> : FrameworkComponent where T : FrameworkComponent<T>
    {
        /// <summary>本 Manager 单例（由组合根构造时登记）。</summary>
        public static T Instance { get; private set; }

        /// <summary>构造即登记单例，使同模块兄弟类无需经 GameEntry 门面即可取本模块 Manager。</summary>
        protected FrameworkComponent()
        {
            Instance = (T)this;
        }
    }
}
