using System;
#if FRAMEWORKBASE_BUGLY_SDK && UNITY_IOS && !UNITY_EDITOR
using System.Runtime.InteropServices;
#endif
#if FRAMEWORKBASE_BUGLY_SDK && UNITY_ANDROID && !UNITY_EDITOR
using UnityEngine;
#endif

namespace Framework.Telemetry.Bugly
{
    /// <summary>
    /// Bugly 原生 SDK 互操作缝（<b>骨架</b>）。
    ///
    /// <para>本包<b>不含</b> Bugly 原生二进制（Android <c>.aar</c> / iOS <c>.framework</c>）。
    /// 所有原生调用都锁在编译宏 <c>FRAMEWORKBASE_BUGLY_SDK</c> 之后——未启用时整类退化为无操作，
    /// 使骨架在没有 SDK 时也能编译通过。落地真实 SDK 后（见包 README）在
    /// Player Settings → Scripting Define Symbols 加上该宏即可启用。</para>
    ///
    /// <para>各方法与 Bugly 官方 API 的对应关系写在各自注释里，替换为真实调用时按图索骥。
    /// 契约：所有方法<b>不得抛异常</b>（异常在此吞掉并转 GameLog），崩溃回捞不能反噬业务。</para>
    /// </summary>
    internal static class BuglyNative
    {
#if FRAMEWORKBASE_BUGLY_SDK && UNITY_IOS && !UNITY_EDITOR
        // iOS：对应工程内 Objective-C 桥接（自行在 Plugins/iOS 提供 fb_bugly_* 的 C 包装，内部转调 Bugly SDK）。
        [DllImport("__Internal")] private static extern void fb_bugly_start(string appId, bool debug, int region);
        [DllImport("__Internal")] private static extern void fb_bugly_set_user(string userId);
        [DllImport("__Internal")] private static extern void fb_bugly_set_key(string key, string value);
        [DllImport("__Internal")] private static extern void fb_bugly_log(string message);
        [DllImport("__Internal")] private static extern void fb_bugly_report_exception(string name, string reason, string stack);
#endif

        /// <summary>装载并启动 Bugly（对应 Android <c>CrashReport.initCrashReport</c> / iOS <c>[Bugly startWithAppId:]</c>）。</summary>
        internal static void Start(string appId, bool debug, BuglyRegion region)
        {
#if FRAMEWORKBASE_BUGLY_SDK && UNITY_ANDROID && !UNITY_EDITOR
            Safe(() =>
            {
                using var player = new AndroidJavaClass("com.unity3d.player.UnityPlayer");
                using var activity = player.GetStatic<AndroidJavaObject>("currentActivity");
                using var crashReport = new AndroidJavaClass("com.tencent.bugly.crashreport.CrashReport");
                // TODO(bugly): 如需国际版走不同 init 重载，按 region 分支。
                crashReport.CallStatic("initCrashReport", activity, appId, debug);
            });
#elif FRAMEWORKBASE_BUGLY_SDK && UNITY_IOS && !UNITY_EDITOR
            Safe(() => fb_bugly_start(appId, debug, (int)region));
#else
            // 骨架无操作：未启用 FRAMEWORKBASE_BUGLY_SDK 或在 Editor/其它平台。
            _ = appId; _ = debug; _ = region;
#endif
        }

        /// <summary>设置归因用户（对应 <c>CrashReport.setUserId</c> / <c>[Bugly setUserIdentifier:]</c>）。</summary>
        internal static void SetUser(string userId)
        {
#if FRAMEWORKBASE_BUGLY_SDK && UNITY_ANDROID && !UNITY_EDITOR
            Safe(() =>
            {
                using var crashReport = new AndroidJavaClass("com.tencent.bugly.crashreport.CrashReport");
                crashReport.CallStatic("setUserId", userId);
            });
#elif FRAMEWORKBASE_BUGLY_SDK && UNITY_IOS && !UNITY_EDITOR
            Safe(() => fb_bugly_set_user(userId));
#else
            _ = userId;
#endif
        }

        /// <summary>设置自定义键值（对应 <c>CrashReport.putUserData</c> / <c>[Bugly setUserValue:forKey:]</c>）。</summary>
        internal static void SetKeyValue(string key, string value)
        {
#if FRAMEWORKBASE_BUGLY_SDK && UNITY_ANDROID && !UNITY_EDITOR
            Safe(() =>
            {
                using var player = new AndroidJavaClass("com.unity3d.player.UnityPlayer");
                using var activity = player.GetStatic<AndroidJavaObject>("currentActivity");
                using var crashReport = new AndroidJavaClass("com.tencent.bugly.crashreport.CrashReport");
                crashReport.CallStatic("putUserData", activity, key, value);
            });
#elif FRAMEWORKBASE_BUGLY_SDK && UNITY_IOS && !UNITY_EDITOR
            Safe(() => fb_bugly_set_key(key, value));
#else
            _ = key; _ = value;
#endif
        }

        /// <summary>留面包屑（对应 <c>BuglyLog.d</c> / iOS 日志接口），随下次崩溃回带。</summary>
        internal static void Log(string message)
        {
#if FRAMEWORKBASE_BUGLY_SDK && UNITY_ANDROID && !UNITY_EDITOR
            Safe(() =>
            {
                using var buglyLog = new AndroidJavaClass("com.tencent.bugly.crashreport.BuglyLog");
                buglyLog.CallStatic("d", "FrameworkBase", message);
            });
#elif FRAMEWORKBASE_BUGLY_SDK && UNITY_IOS && !UNITY_EDITOR
            Safe(() => fb_bugly_log(message));
#else
            _ = message;
#endif
        }

        /// <summary>
        /// 上报一条托管异常为非致命（对应 Android <c>CrashReport.postException(4, name, reason, stack, null)</c>，
        /// category 4 = C#/Unity；iOS <c>[Bugly reportExceptionWithCategory:...]</c>）。
        /// </summary>
        internal static void ReportException(string name, string reason, string stack)
        {
#if FRAMEWORKBASE_BUGLY_SDK && UNITY_ANDROID && !UNITY_EDITOR
            Safe(() =>
            {
                using var crashReport = new AndroidJavaClass("com.tencent.bugly.crashreport.CrashReport");
                // 4 = Unity/C# 类别；末位 extraInfo 传 null。
                crashReport.CallStatic("postException", 4, name, reason, stack, null);
            });
#elif FRAMEWORKBASE_BUGLY_SDK && UNITY_IOS && !UNITY_EDITOR
            Safe(() => fb_bugly_report_exception(name, reason, stack));
#else
            _ = name; _ = reason; _ = stack;
#endif
        }

#if FRAMEWORKBASE_BUGLY_SDK && !UNITY_EDITOR
        /// <summary>吞掉原生调用异常（AndroidJNI / P/Invoke 失败不得反噬业务），转 GameLog。</summary>
        private static void Safe(Action action)
        {
            try { action(); }
            catch (Exception ex) { GameLog.Warning($"[BuglyNative] 原生调用异常：{ex.Message}"); }
        }
#endif
    }
}
