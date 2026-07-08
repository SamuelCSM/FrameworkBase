# FrameworkBase Telemetry — Bugly（参考骨架）

把腾讯 [Bugly](https://bugly.qq.com/) 的**原生**崩溃 / ANR / OOM 捕获接到主干的
`Framework.Core.Telemetry.ICrashBackend`。这是主干默认 `LocalFileCrashBackend`
补不上的那块——托管兜底后端只能抓 `LogType.Exception`，抓不住真正杀死移动端会话的
原生致命崩溃；那些只有 Bugly 的原生信号处理器 / NDK / ANR watchdog 能捕获。

> **这是骨架**：不含 Bugly 原生 SDK 二进制。所有原生调用锁在编译宏
> `FRAMEWORKBASE_BUGLY_SDK` 之后，未启用时整包退化为无操作（可编译、可安装、不报错，
> 但不产生任何原生捕获）。按下方步骤落地真实 SDK 后启用宏即可。

## 结构

| 文件 | 职责 |
|---|---|
| `Runtime/BuglyCrashBackend.cs` | `ICrashBackend` 实现：装载 / 归因透传 / 托管异常转发 |
| `Runtime/BuglyBootstrap.cs` | `[RuntimeInitializeOnLoadMethod(BeforeSceneLoad)]` 自注册，早于 `GameEntry.Awake` |
| `Runtime/BuglyNative.cs` | 原生 SDK 互操作缝（Android `AndroidJavaClass` / iOS `DllImport("__Internal")`），锁在宏后 |
| `Runtime/BuglyOptions.cs` | AppId / 渠道 / 区域参数 |

## 装配时序（为什么能抓住启动崩溃）

```
[RuntimeInitializeOnLoadMethod(BeforeSceneLoad)]  ← BuglyBootstrap.AutoRegister
        └─ CrashReporter.Register(new BuglyCrashBackend(...))
GameEntry.Awake
        └─ CrashReporter.Install()  ← 此时后端已就位，调 Bugly 启动，原生捕获上线
```

`Register` 必须先于 `Install`；`RuntimeInitializeOnLoad(BeforeSceneLoad)` 保证它早于任何
场景 MonoBehaviour 的 `Awake`，故早于 `GameEntry.Awake`。业务零接线：装了本包即自动接管。

## 落地真实 Bugly SDK

1. **导入原生 SDK**：把 Bugly Android `.aar` 放 `Assets/Plugins/Android/`，
   iOS 用官方 `.framework` 或 CocoaPods；iOS 侧另需在 `Plugins/iOS/` 提供
   `fb_bugly_*` 的 Objective-C C 包装（`BuglyNative.cs` 里已声明这些 `extern` 签名，
   桥接内部转调 `Bugly` SDK）。
2. **填 AppId**：改 `BuglyBootstrap.ResolveOptions()`（骨架留空）。推荐改成从
   `Resources.Load<TextAsset>("bugly_options")` 或 `AppConfig` 新增字段读取，别写死在代码里。
3. **启用宏**：Player Settings → Scripting Define Symbols 加 `FRAMEWORKBASE_BUGLY_SDK`
   （建议只在 Android / iOS 平台加）。启用后 `BuglyNative` 的原生分支才编入。
4. **验证**：真机跑一次，在 Bugly 后台确认托管异常（非致命）与主动触发的原生崩溃都上报到位。

## 契约要点（改真实实现时守住）

- **不得抛异常**：`BuglyNative` 的原生调用一律 try/catch 吞掉转 `GameLog`——崩溃回捞不能反噬业务。
- **`TryFlushPendingAsync` 返回 false**：原生崩溃走 Bugly 自身管道下次启动上报，框架侧无积压可冲刷。
- **线程**：`RecordManagedException` 可能在任意线程被调；`AndroidJavaClass` 调用需 JVM 附着线程，
  真实实现按需 `AndroidJNI.AttachCurrentThread` 或编组到主线程（骨架直接转发并已在此注明）。

## 与主干的关系

主干 `com.frameworkbase.core` 只定义 `ICrashBackend` 契约与 `CrashReporter` 编排器，**不含**
任何厂商代码。本包是其一个参考实现；换 Sentry / Crashlytics 照此模式另起一个扩展包即可。
