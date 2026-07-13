using Cysharp.Threading.Tasks;
using Framework.Core.Telemetry;

namespace Framework.Telemetry.Bugly
{
    /// <summary>
    /// Bugly 崩溃后端（<b>骨架</b>）：把腾讯 Bugly 的<b>原生</b>崩溃 / ANR / OOM 捕获接到框架的
    /// <see cref="ICrashBackend"/>。这正是默认 <c>LocalFileCrashBackend</c> 补不上的那块——原生
    /// 致命崩溃由 Bugly 原生层信号处理器捕获、经 Bugly 自身管道在下次启动上报，框架不搬字节。
    ///
    /// <para>本后端的职责：① <see cref="Install"/> 尽早启动 Bugly 让原生捕获就位；② 把用户 /
    /// 自定义键 / 面包屑透传给 Bugly 做崩溃归因；③ 把框架捕获的托管异常转发为 Bugly「非致命」记录。
    /// 因原生崩溃走 Bugly 管道，<see cref="TryFlushPendingAsync"/> 对框架而言无事可做（返回 false）。</para>
    ///
    /// <para>装配：由 <see cref="BuglyBootstrap"/> 经 <c>RuntimeInitializeOnLoad(BeforeSceneLoad)</c>
    /// 在 <c>GameEntry.Awake</c> 之前 <c>CrashReporter.Register</c>。真实 SDK 落地步骤见包 README。</para>
    /// </summary>
    public sealed class BuglyCrashBackend : ICrashBackend
    {
        private readonly BuglyOptions _options;

        public BuglyCrashBackend(BuglyOptions options)
        {
            _options = options ?? new BuglyOptions();
        }

        /// <inheritdoc />
        public string Name => "bugly";

        /// <inheritdoc />
        public void Install(in CrashSessionInfo session)
        {
            if (!_options.IsConfigured)
            {
                // 未配置 AppId：不启动原生捕获（骨架默认态）。
                // 级别分环境：Editor / Development Build 属开发常态，用普通日志（不污染"零噪声"启动壳验收）；
                // 正式包维持醒目 Error，避免带着"没有崩溃上报"的包上线而无人察觉。
#if UNITY_EDITOR || DEVELOPMENT_BUILD
                GameLog.Log("[BuglyCrashBackend] 未配置 AppId，Bugly 原生崩溃捕获未启动（开发环境常态，仅托管兜底后端有效）");
#else
                GameLog.Error("[BuglyCrashBackend] 未配置 AppId，Bugly 原生崩溃捕获未启动（仅托管兜底后端有效）");
#endif
                return;
            }

            BuglyNative.Start(_options.AppId, _options.IsDebug, _options.Region);

            // 把早期即知的会话维度作为自定义键透传，Bugly 崩溃报告即可按构建/渠道/设备聚合。
            BuglyNative.SetKeyValue("app_version", session.AppVersion);
            BuglyNative.SetKeyValue("build_type", session.BuildType);
            if (!string.IsNullOrEmpty(_options.Channel))
                BuglyNative.SetKeyValue("channel", _options.Channel);
            if (!string.IsNullOrEmpty(session.DeviceId))
                BuglyNative.SetKeyValue("device_id", session.DeviceId);

            GameLog.Log($"[BuglyCrashBackend] Bugly 已装载（region={_options.Region}, debug={_options.IsDebug}）");
        }

        /// <inheritdoc />
        public void SetUser(string userId) => BuglyNative.SetUser(userId ?? string.Empty);

        /// <inheritdoc />
        public void SetCustomKey(string key, string value)
        {
            if (!string.IsNullOrEmpty(key))
                BuglyNative.SetKeyValue(key, value ?? string.Empty);
        }

        /// <inheritdoc />
        public void LeaveBreadcrumb(string message)
        {
            if (!string.IsNullOrEmpty(message))
                BuglyNative.Log(message);
        }

        /// <inheritdoc />
        public void RecordManagedException(in ManagedExceptionInfo error)
        {
            // 首行作为异常名，其余作为原因；托管栈原样带上。Bugly 记为非致命，可在后台单独看。
            string message = error.Message ?? string.Empty;
            int split = message.IndexOf(':');
            string name = split > 0 ? message.Substring(0, split) : "ManagedException";
            BuglyNative.ReportException(name, message, error.StackTrace ?? string.Empty);
        }

        /// <inheritdoc />
        public UniTask<bool> TryFlushPendingAsync()
        {
            // 原生致命崩溃由 Bugly 在原生层落盘并经自身管道下次启动上报——框架侧无积压可冲刷。
            return UniTask.FromResult(false);
        }
    }
}
