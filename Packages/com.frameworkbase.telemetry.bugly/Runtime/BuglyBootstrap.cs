using Framework.Core.Telemetry;
using UnityEngine;

namespace Framework.Telemetry.Bugly
{
    /// <summary>
    /// Bugly 后端自注册入口。
    ///
    /// <para>用 <c>[RuntimeInitializeOnLoadMethod(BeforeSceneLoad)]</c> 在<b>任何场景 MonoBehaviour
    /// 的 Awake 之前</b>运行，从而保证 <c>CrashReporter.Register</c> 早于 <c>GameEntry.Awake →
    /// CrashReporter.Install</c>——原生崩溃捕获必须尽早就位，这是 <c>ICrashBackend</c> 的装配契约。</para>
    ///
    /// <para><b>接管是有条件的</b>：<c>CrashReporter</c> 只保留一个后端，注册本包后端等于顶掉框架默认的
    /// <c>LocalFileCrashBackend</c>。因此只有 AppId 已配置<b>且</b>原生 SDK 真的链接进包时才接管；
    /// 任一不满足就让出位置，托管异常继续由本地落盘后端记录并可经 <c>CrashReportUrl</c> 上报。
    /// 判定见 <see cref="ShouldTakeOver"/>。</para>
    ///
    /// <para>若要自定义 AppId / 渠道 / 区域，改 <see cref="ResolveOptions"/>（骨架里写死占位；
    /// 真实项目建议改成从 Resources / AppConfig 读）。</para>
    /// </summary>
    public static class BuglyBootstrap
    {
        /// <summary>进程启动最早期尝试接管崩溃后端；不满足接管条件时保持框架默认后端。</summary>
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        private static void AutoRegister()
        {
            TryRegisterBackend(ResolveOptions(), BuglyNative.IsLinked);
        }

        /// <summary>
        /// 按配置与原生链接状态决定是否接管崩溃后端，并在让出时说明原因。
        /// 拆成 internal 方法而非全写在 <see cref="AutoRegister"/> 里，是为了让接管判定可被单测覆盖——
        /// <c>RuntimeInitializeOnLoadMethod</c> 在 EditMode 不触发，原生链接状态也无法在 Editor 里造出来。
        /// </summary>
        /// <param name="options">本次装配使用的 Bugly 参数。</param>
        /// <param name="nativeLinked">原生 SDK 是否真的链接进本次构建，正式装配传 <see cref="BuglyNative.IsLinked"/>。</param>
        /// <returns>已接管返回 true；让出给框架默认后端返回 false。</returns>
        internal static bool TryRegisterBackend(BuglyOptions options, bool nativeLinked)
        {
            if (!ShouldTakeOver(options, nativeLinked, out string declineReason))
            {
                LogDeclined(declineReason);
                return false;
            }

            CrashReporter.Register(new BuglyCrashBackend(options));
            return true;
        }

        /// <summary>
        /// 判定本包是否应接管崩溃后端。
        /// <para>
        /// 两个条件缺一不可：AppId 已配置，且原生层真的可用。原生层是空壳（未加
        /// <c>FRAMEWORKBASE_BUGLY_SDK</c> 宏、或运行在 Editor 与非 Android/iOS 平台）时接管的后果最严重——
        /// 原生崩溃抓不到，托管异常又被转发进无操作的原生缝，同时框架的本地落盘兜底已被顶掉，
        /// 结果是崩溃回捞整条链静默失效。
        /// </para>
        /// </summary>
        /// <param name="options">待判定的 Bugly 参数；null 视为未配置。</param>
        /// <param name="nativeLinked">原生 SDK 是否真的链接进本次构建。</param>
        /// <param name="declineReason">不接管时返回原因文本，供日志说明；接管时为 null。</param>
        /// <returns>应当接管返回 true。</returns>
        internal static bool ShouldTakeOver(BuglyOptions options, bool nativeLinked, out string declineReason)
        {
            declineReason = null;

            if (options == null || !options.IsConfigured)
            {
                declineReason = "未配置 AppId";
                return false;
            }

            if (!nativeLinked)
            {
                declineReason = "原生 SDK 未链接（缺 FRAMEWORKBASE_BUGLY_SDK 宏，或运行在 Editor / 非 Android|iOS 平台）";
                return false;
            }

            return true;
        }

        /// <summary>
        /// 记录"本包让出崩溃后端"。级别分环境：Editor 与开发包里原生层本就不可用，属常态，用普通日志；
        /// 正式包里让出意味着这个包白装了、原生崩溃无人捕获，必须醒目告警，避免带着这种状态上线而无人察觉。
        /// </summary>
        /// <param name="reason">让出原因。</param>
        private static void LogDeclined(string reason)
        {
            string message = $"[BuglyBootstrap] 未接管崩溃后端（{reason}），" +
                             "崩溃回捞保持框架默认的本地落盘后端（仅覆盖托管异常，原生崩溃 / ANR / OOM 不被捕获）";
#if UNITY_EDITOR || DEVELOPMENT_BUILD
            GameLog.Log(message);
#else
            GameLog.Error(message);
#endif
        }

        /// <summary>
        /// 解析 Bugly 初始化参数。<b>骨架占位实现</b>——AppId 留空，落地时替换为真实来源。
        /// 推荐改法：<c>Resources.Load&lt;TextAsset&gt;("bugly_options")</c> 解析 JSON，
        /// 或在 <c>AppConfig</c> 增字段（BuglyAppId / BuglyRegion）后从那里读。
        /// </summary>
        private static BuglyOptions ResolveOptions()
        {
            return new BuglyOptions
            {
                // TODO(bugly): 填入 Bugly 后台的 AppId（留空则本包不接管，崩溃回捞走框架本地兜底后端）。
                AppId = string.Empty,
#if DEVELOPMENT_BUILD || UNITY_EDITOR
                IsDebug = true,
#else
                IsDebug = false,
#endif
                Region = BuglyRegion.China,
            };
        }
    }
}
