using System;
using Cysharp.Threading.Tasks;
using UnityEngine;

namespace Framework.Core.Telemetry
{
    /// <summary>
    /// 崩溃回捞编排器（static，框架最早期挂接）。
    ///
    /// 职责：挂托管异常监听、维护会话归因上下文、把事件路由到注入的 <see cref="ICrashBackend"/>。
    /// 具体「捕获什么、怎么上报」由后端决定——默认 <see cref="LocalFileCrashBackend"/> 只覆盖
    /// 托管异常并本地落盘；<b>原生致命崩溃（SIGSEGV / OOM / ANR）需在扩展包实现
    /// <see cref="ICrashBackend"/> 接厂商 SDK（Crashlytics / Sentry / Bugly）并 <see cref="Register"/>。</b>
    /// </summary>
    /// <remarks>
    /// <para>装配时序：厂商扩展包用 <c>[RuntimeInitializeOnLoadMethod(BeforeSceneLoad)]</c> 自注册，
    /// 早于 <c>GameEntry.Awake</c> → <see cref="Install"/>；未注册时 Install 落默认本地后端。
    /// 原生捕获必须尽早就位，故 <see cref="Register"/> 必须先于 <see cref="Install"/>。</para>
    /// <para>线程安全：<see cref="Install"/> 挂的 <c>logMessageReceivedThreaded</c> 可能在任意线程触发，
    /// 后端 <see cref="ICrashBackend.RecordManagedException"/> 须自行保证线程安全。</para>
    /// </remarks>
    public static class CrashReporter
    {
        /// <summary>当前后端；未 Install 前可经 <see cref="Register"/> 预置。</summary>
        private static ICrashBackend _backend;

        /// <summary>是否已挂接（幂等保护）。</summary>
        private static bool _installed;

        /// <summary>当前后端名（未装配时为 none）。</summary>
        public static string BackendName => _backend?.Name ?? "none";

        /// <summary>
        /// 注册崩溃后端。<b>必须在 <see cref="Install"/> 之前</b>调用（原生捕获要尽早就位）。
        /// 通常由厂商扩展包 <c>[RuntimeInitializeOnLoadMethod(BeforeSceneLoad)]</c> 自注册。
        /// Install 后调用被拒绝（记 Error）。重复注册以最后一次为准（记 Warning）。
        /// </summary>
        public static void Register(ICrashBackend backend)
        {
            if (backend == null)
            {
                GameLog.Error("[CrashReporter] Register 传入 null，忽略");
                return;
            }

            if (_installed)
            {
                GameLog.Error($"[CrashReporter] 已 Install，拒绝注册后端 {backend.Name}" +
                              "（须在 Install 前注册，通常经 RuntimeInitializeOnLoad(BeforeSceneLoad) 自注册）");
                return;
            }

            if (_backend != null)
                GameLog.Warning($"[CrashReporter] 崩溃后端被覆盖注册：{_backend.Name} → {backend.Name}");

            _backend = backend;
        }

        /// <summary>
        /// 挂接崩溃捕获（幂等）。应在框架初始化最早期调用。
        /// 未经 <see cref="Register"/> 预置后端时落默认 <see cref="LocalFileCrashBackend"/>
        /// （仅覆盖托管异常，非危险兜底，故只 info 日志、不告警）。
        /// </summary>
        public static void Install()
        {
            if (_installed) return;
            _installed = true;

            if (_backend == null)
            {
                _backend = new LocalFileCrashBackend();
                GameLog.Log("[CrashReporter] 未注册崩溃后端，使用默认本地落盘后端" +
                            "（仅覆盖托管异常；原生崩溃 / ANR / OOM 需接入厂商扩展包）");
            }

            // persistentDataPath / version / deviceId 只能在主线程访问，Install 在主线程取好交给后端。
            var session = new CrashSessionInfo(
                Application.version,
                BuildTypeString(),
                Application.persistentDataPath,
                SystemInfo.deviceUniqueIdentifier);

            try
            {
                _backend.Install(session);
            }
            catch (Exception ex)
            {
                GameLog.Error($"[CrashReporter] 后端 {_backend.Name} Install 异常（崩溃捕获可能未就位）：{ex.Message}");
            }

            Application.logMessageReceivedThreaded += OnLogMessage;
        }

        /// <summary>设置归因用户（登录成功 / 切号后调用）。未 Install 时静默忽略。</summary>
        public static void SetUser(string userId) => SafeForward(b => b.SetUser(userId));

        /// <summary>设置自定义归因键（渠道 / 环境 / 当前关卡等）。未 Install 时静默忽略。</summary>
        public static void SetCustomKey(string key, string value) => SafeForward(b => b.SetCustomKey(key, value));

        /// <summary>留一条面包屑（关键操作路径）。未 Install 时静默忽略。</summary>
        public static void LeaveBreadcrumb(string message) => SafeForward(b => b.LeaveBreadcrumb(message));

        /// <summary>
        /// 尝试上报本地积压（默认后端读 <c>AppConfig.CrashReportUrl</c>；原生后端走自身管道）。
        /// 在框架就绪后调用；未装配后端时返回 false。
        /// </summary>
        public static async UniTask<bool> TryUploadPendingAsync()
        {
            if (_backend == null) return false;

            try
            {
                return await _backend.TryFlushPendingAsync();
            }
            catch (Exception ex)
            {
                GameLog.Warning($"[CrashReporter] 后端 {_backend.Name} flush 异常：{ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// 卸载监听并复位（与 <see cref="Install"/> 对称）。应用退出 / 测试隔离时调用；
        /// 复位后可重新 <see cref="Register"/> + <see cref="Install"/>。
        /// </summary>
        public static void Shutdown()
        {
            if (!_installed) return;
            Application.logMessageReceivedThreaded -= OnLogMessage;
            _installed = false;
            _backend = null;
        }

        /// <summary>
        /// 日志回调：仅处理 Exception 级（Error 级噪声大且通常已有业务日志），转后端记为非致命。
        /// 崩溃记录本身绝不能再抛异常影响主流程。
        /// </summary>
        private static void OnLogMessage(string condition, string stackTrace, LogType type)
        {
            if (type != LogType.Exception) return;

            var info = new ManagedExceptionInfo(
                DateTimeOffset.UtcNow.ToUnixTimeSeconds(), condition, stackTrace, type);

            try
            {
                _backend?.RecordManagedException(info);
            }
            catch
            {
                // 后端记录失败不得反噬崩溃回捞本身。
            }
        }

        /// <summary>把归因调用转给后端并吞掉异常（归因失败不得影响业务）。</summary>
        private static void SafeForward(Action<ICrashBackend> action)
        {
            ICrashBackend backend = _backend;
            if (backend == null) return;

            try
            {
                action(backend);
            }
            catch (Exception ex)
            {
                GameLog.Warning($"[CrashReporter] 归因上下文转发异常：{ex.Message}");
            }
        }

        /// <summary>当前构建类型字符串（归因维度）。</summary>
        private static string BuildTypeString()
        {
#if UNITY_EDITOR
            return "editor";
#elif DEVELOPMENT_BUILD
            return "development";
#else
            return "release";
#endif
        }
    }
}
