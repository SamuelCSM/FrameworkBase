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
}
