# Changelog

本包遵循 [语义化版本](https://semver.org/lang/zh-CN/)。`0.x` 为孵化期。

## [0.1.0] - 2026-07-08

### 新增

- Bugly 崩溃后端**参考骨架**：`BuglyCrashBackend` 实现主干 `ICrashBackend`，
  经 `BuglyBootstrap` 的 `[RuntimeInitializeOnLoadMethod(BeforeSceneLoad)]` 自注册，
  早于 `GameEntry.Awake → CrashReporter.Install`。
- `BuglyNative` 原生互操作缝：Android `AndroidJavaClass` / iOS `DllImport("__Internal")`
  调用，全部锁在编译宏 `FRAMEWORKBASE_BUGLY_SDK` 之后——未启用时退化为无操作，
  保证骨架在无原生 SDK 时可编译。
- `BuglyOptions`（AppId / 渠道 / 区域）。落地真实 SDK 步骤见 README。

### 已知限制

- 不含 Bugly 原生二进制；未启用 `FRAMEWORKBASE_BUGLY_SDK` 时不产生任何原生捕获。
- 尚无真机上报验证（骨架阶段）；真实 SDK 接入后需在 Bugly 后台确认托管非致命与原生崩溃均到位。
