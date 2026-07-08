# 架构决策记录（ADR）

重大取舍的决策与理由。改变决策时在此追加新条目说明为什么，不删旧条目。

## ADR-001：暂不按模块拆分 Framework.asmdef（2026-07）

**状态**：已决策（暂不拆分）。

**背景**：Framework 运行时是单一 asmdef（200+ 源文件）。拆分为
Framework.Core / Network / Resource / UI / … 多程序集的收益是增量编译提速与
层间依赖编译期强制；诉求在 P2-B 评估中提出。

**决策**：现阶段**不拆**，理由按权重排序：

1. **解耦成本前置且巨大**：`GameEntry` 静态门面被全部模块引用，模块间存在大量
   横向互调（Analytics↔Sdk 渠道维度、ErrorCenter→Tips/Event、RemoteConfig→HotUpdate
   版本比较、Save→Serialization）。拆程序集必须先把这些互调抽成接口 + 组合根注入，
   改动面覆盖几乎每个文件，而框架当前正处于能力快速补齐期——拆分会冻结迭代数周。
2. **收益当下不成立**：单程序集全量编译在当前规模下仍是秒级；框架由单人/小团队
   维护，无并行改不同模块的合并冲突之痛。
3. **连带成本**：InternalsVisibleTo、MSBuild 验证流程、HybridCLR 程序集配置、
   测试 asmdef 引用、UPM 包结构全部要跟着动，且拆错边界的返工代价高于晚拆。

**已有替代手段**（守住拆分想守的东西）：

- 目录 + 命名空间即模块边界（Framework.Network / Framework.Save / …）；
- `.editorconfig` + 深度校验器 + 测试守规范；
- 新抽象层（Http/Serialization/Storage/Pooling）已按"接口 + Shared 注入点"成型，
  未来拆分时这些天然是程序集边界。

**重新评估的触发条件**（满足任一即重开本决策）：

- 脚本改动后的编译等待成为日常痛点（经验阈值：>15 秒且每日多次）；
- 框架由 ≥3 人并行维护，模块间合并冲突高频出现；
- 需要对外发布裁剪版包（例如只要 Network+Resource 不要 UI）；
- 业务项目要求把某模块热更化（进 HybridCLR 热更程序集组）。

**拆分时的建议切法**（留给未来）：先切叶子（Serialization → Http → Storage，
它们已零横向依赖），再切 Network/Save/Analytics，最后处理 GameEntry 门面
（拆成 per-module 注册 + 服务定位）；每切一刀跑全量门禁。

## ADR-002：分层拆分启动——Framework.Foundation 先行（2026-07）

**状态**：已实施（第一、二步完成；第三步挂起）。部分修订 ADR-001：per-module
全拆维持挂起，但**分层拆分**按下述路线启动。依赖链现为 `Runtime → Kernel →
Foundation` 三层单向、编译期强制。

**动机**：架构长期健康——趁规模小把层间依赖固化为编译期约束，防止继续糊；
无 ADR-001 所列外部触发（编译耗时/多人/裁剪/热更均未命中）。

**实测修正 ADR-001 的两个判断**：

1. "改动面覆盖几乎每个文件"不成立——`GameEntry.X` 全部 124 处引用中约半数是
   模块经门面取自己，真正模块互调约 60 处、集中在 9 个服务上；
2. 依赖扫描必须以编译验证为准——`using` 扫描抓不到根命名空间 `Framework`
   下类型的无 using 引用（本次 Pooling 即栽在 `GameLog`/`IPoolable` 上）。

**第一步（本次）**：`Serialization / Http / Storage / Enum` 四个零依赖目录经
**asmref 聚合**进新程序集 `Framework.Foundation`（asmdef 落在 `Foundation/`，
各目录放 `.asmref`，目录结构与命名空间零改动）；`Framework` 引用它。
外部引用仅 UniTask。零代码改动，MSBuild 双程序集编译验证通过。

**本轮未进 Foundation 的候选及原因**：

- **Pooling**：`ObjectPool` 引用根命名空间的 `GameLog`（Utils）与 `IPoolable`
  （Resource 目录）——待解绳结：日志改注入或下沉、`IPoolable` 归属移入 Pooling；
- **Utils**：`PerfHud` 直连 `Core.GameEntry`，且混杂纯工具（StableHash/MD5Util）
  与 Unity 调试件（RuntimeConsole/UIExtensions），需先按性质拆开。

**后续路线**（每步独立提交、全量门禁）：

- 第二步：`Framework.Kernel`（FrameworkComponent / MonoSingleton / Event /
  Timer / AppConfig / Telemetry / ErrorCenter），唯一手术是 ErrorCenter→Tips
  反转为事件订阅；
- 第三步（挂起，沿用 ADR-001 触发条件）：Boot 提取（GameEntry/LaunchFlow/
  LoginFlow 上移为组合根，命名空间不变故业务侧零改动）+ Runtime 按模块拆；
  平时顺手做的准备：门面自引用改直连、NetworkWaitingUI/ReconnectPanel 归属
  理顺、确认 Analytics↔Sdk 实为单向。

### 第二步实施记录：Framework.Kernel（2026-07-08）

**状态**：已实施。新增程序集 `Framework.Kernel`（asmdef 落 `Kernel/`），依赖链
自此为 `Runtime → Kernel → Foundation` 三层单向，编译期强制。

**归入 Kernel 的成员**：`Kernel/`（FrameworkComponent / MonoSingleton / Singleton /
AppConfig / AppConfigAsset / TelemetryErrorCodes / Errors / Telemetry.CrashReporter）
+ `Event/`（asmref）+ `Timer/`（asmref），共 15 个源文件。

**GameLog 一并下沉 Foundation**：`FrameworkComponent`/`ErrorCenter`/`EventManager`/
`TimerManager`/`CrashReporter` 均依赖 `GameLog`，若 GameLog 留在 Framework 则
Kernel 反向依赖 Runtime，闭环。故把 `GameLog.cs` 从 `Utils/` 移到新目录
`Logging/`（asmref → Foundation）——这正是 ADR-001 预判、第一步 Pooling 栽跟头
坐实的那个绳结。GameLog 移动经 `git mv`（.meta GUID 保留，引用不断）。

**唯一手术：ErrorCenter 反转为零上行依赖**（此前 `ErrorCenter` 直连
`GameEntry.Tips`/`GameEntry.Event`/`GameEntry.Analytics`，是 Kernel 化的硬绳结）：

1. 埋点：`ErrorCenter`（Kernel）不再 `GameEntry.Analytics.Track`，改为暴露
   `event Action<ErrorDecision> ErrorReported`（同码 60 秒限流后触发），由组合根
   GameEntry 订阅转发埋点；限流状态仍属 ErrorCenter，职责不外泄。
2. 呈现：`DefaultErrorPresenter`（依赖 Tips/Event/GameEntry）从 ErrorCenter.cs
   **上移**到 Framework 层新文件 `Core/DefaultErrorPresenter.cs`；Kernel 内仅保留
   仅日志的兜底 `LoggingErrorPresenter`。真正 UI 呈现器由 GameEntry 经
   `SetPresenter` 注入。行为等价（Toast/弹窗降级/登出维护广播全保留）。
3. 副产：埋点上报此前在纯单测中因 `GameEntry.Analytics` 为 null 无法断言，反转后
   经 `ErrorReported` 事件可直接验证，限流单测已补上强断言。

**未纳入 Kernel 的近邻及原因**：`GameMessage`/`EventDefine` 含业务语义消息码，
但与 `EventManager` 同目录同程序集不可分割，随 Event 一并入 Kernel（可接受：它们
是纯枚举/常量，无行为、无上行依赖）。

**验证**：编辑器占锁，手工重建 6 个 csproj（Foundation/Kernel/Framework/Editor/
两个 Tests）+ Assembly-CSharp，MSBuild 全绿；Kernel、Foundation **单独**重建
（仅引用下层）通过，反证零上行依赖真实成立。asmdef 引用侧同步更新：Framework /
Editor / 两个 Tests 均加 `Framework.Kernel` 引用（Editor 用 AppConfigAsset、测试用
ErrorCenter/EventManager/TimerManager）。

**第二步后仍在 Framework 层的原 Core 成员**：GameEntry / LaunchFlow / LoginFlow /
Auth / Privacy / VersionDisplayHelper 等——它们是组合根与业务编排，正是第三步
Boot 提取的对象，本步不动。
