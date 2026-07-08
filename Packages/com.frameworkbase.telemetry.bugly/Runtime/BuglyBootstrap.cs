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
    /// <para>业务零接线：装了本包即自动接管崩溃后端。若要自定义 AppId / 渠道 / 区域，改
    /// <see cref="ResolveOptions"/>（骨架里写死占位；真实项目建议改成从 Resources / AppConfig 读）。</para>
    /// </summary>
    public static class BuglyBootstrap
    {
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        private static void AutoRegister()
        {
            CrashReporter.Register(new BuglyCrashBackend(ResolveOptions()));
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
                // TODO(bugly): 填入 Bugly 后台的 AppId（留空则不启动原生捕获，仅告警）。
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
