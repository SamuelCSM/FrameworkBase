using Cysharp.Threading.Tasks;
using UnityEngine;

namespace Framework.Core.Telemetry
{
    /// <summary>
    /// 崩溃后端抽象：主干只定义契约，具体厂商实现（Crashlytics / Sentry / Bugly 等）
    /// 放各自扩展包，经 <see cref="CrashReporter.Register"/> 注入。主干不含厂商 SDK。
    ///
    /// <para>为什么不是 <c>Analytics</c> 式的 <c>SendAsync(batch)</c>：
    /// 原生致命崩溃（SIGSEGV / IL2CPP C++ 层空引用 / OOM 被系统杀进程 / ANR）只有厂商 SDK 的
    /// 原生信号处理器 / NDK / ANR watchdog 能捕获，且由厂商自身管道在下次启动上报——框架搬不动
    /// 这些字节。框架能做的是：① 尽早 <see cref="Install"/> 让原生捕获就位；② 透传归因上下文
    /// （用户 / 自定义键 / 面包屑）让崩溃报告可定位；③ 把托管异常转发为「非致命」记录。
    /// 默认 <see cref="LocalFileCrashBackend"/> 只是没接厂商时的兜底，仅覆盖托管异常。</para>
    /// </summary>
    public interface ICrashBackend
    {
        /// <summary>后端标识（日志用）。</summary>
        string Name { get; }

        /// <summary>
        /// 装载崩溃捕获。由 <see cref="CrashReporter.Install"/> 在框架最早期调用（Manager 就绪前，
        /// 越早越能兜住启动崩溃）。契约：幂等；<b>不得抛异常</b>；不得阻塞主线程。厂商实现在此
        /// init 原生 SDK / 装信号处理器。
        /// </summary>
        void Install(in CrashSessionInfo session);

        /// <summary>设置归因用户（登录成功 / 切号后调用）；后端把它附到后续崩溃报告。</summary>
        void SetUser(string userId);

        /// <summary>设置自定义归因键（渠道 / 环境 / 当前关卡等），后端随崩溃报告一并上报。</summary>
        void SetCustomKey(string key, string value);

        /// <summary>留一条面包屑（关键操作路径），崩溃时随报告回带，辅助复现。</summary>
        void LeaveBreadcrumb(string message);

        /// <summary>
        /// 记录一条已捕获的托管异常为「非致命」事件。原生致命崩溃由后端自身在原生层捕获，不经此方法。
        /// 契约：可能在任意线程调用；<b>不得抛异常</b>。
        /// </summary>
        void RecordManagedException(in ManagedExceptionInfo error);

        /// <summary>
        /// 尝试上报本地积压。仅默认落盘后端有实际动作；原生后端走自身管道，空实现返回 false 即可。
        /// 返回是否完成了一次成功上报。
        /// </summary>
        UniTask<bool> TryFlushPendingAsync();
    }

    /// <summary>崩溃后端 <see cref="ICrashBackend.Install"/> 时的会话信息（仅含框架最早期即可取到的字段）。</summary>
    public readonly struct CrashSessionInfo
    {
        /// <summary>应用版本（<c>Application.version</c>）。</summary>
        public readonly string AppVersion;

        /// <summary>构建类型："release" / "development" / "editor"。</summary>
        public readonly string BuildType;

        /// <summary>持久化目录（厂商 SDK 落盘 / 框架本地缓存用）。</summary>
        public readonly string PersistentDataPath;

        /// <summary>设备标识（隐私合规下可能为空串）。</summary>
        public readonly string DeviceId;

        public CrashSessionInfo(string appVersion, string buildType, string persistentDataPath, string deviceId)
        {
            AppVersion = appVersion;
            BuildType = buildType;
            PersistentDataPath = persistentDataPath;
            DeviceId = deviceId;
        }
    }

    /// <summary>一条托管异常的取证信息。</summary>
    public readonly struct ManagedExceptionInfo
    {
        /// <summary>发生时间（Unix 秒，UTC）。</summary>
        public readonly long TimestampUnixSeconds;

        /// <summary>异常消息（首行）。</summary>
        public readonly string Message;

        /// <summary>堆栈（IL2CPP 下为托管映射栈）。</summary>
        public readonly string StackTrace;

        /// <summary>日志级别（Exception / Error）。</summary>
        public readonly LogType LogType;

        public ManagedExceptionInfo(long timestampUnixSeconds, string message, string stackTrace, LogType logType)
        {
            TimestampUnixSeconds = timestampUnixSeconds;
            Message = message;
            StackTrace = stackTrace;
            LogType = logType;
        }
    }
}
