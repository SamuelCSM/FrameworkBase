# Changelog

本包遵循 [语义化版本](https://semver.org/lang/zh-CN/)。`0.x` 为孵化期。

## [未发布]

### 修复

- **接管崩溃后端改为有条件**：此前无论 AppId 是否配置、原生 SDK 是否链接，`BuglyBootstrap`
  都会注册本包后端。由于 `CrashReporter` 只保留一个后端，这会顶掉主干的
  `LocalFileCrashBackend`——在骨架默认态（AppId 留空、未加 `FRAMEWORKBASE_BUGLY_SDK` 宏）下，
  托管异常被转发进无操作的原生缝，既不上报也不落盘，崩溃回捞整条链静默失效。
  现在只有 AppId 已配置**且** `BuglyNative.IsLinked` 为真时才接管；否则让位并说明原因
  （正式包 Error、Editor 与开发包普通日志）。
- 新增 `BuglyNative.IsLinked`：与各原生方法执行真实调用的条件完全一致，供装配方判断
  原生层是不是空壳。

### 新增

- 本包 EditMode 测试程序集 `Framework.Telemetry.Bugly.Tests.EditMode`，覆盖接管判定的
  四种组合与"让位后回落主干本地落盘后端"。

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
