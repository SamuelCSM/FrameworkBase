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

**状态**：已实施（第一、二步完成；第三步仅做 3a 去环，3b/3c 挂起）。部分修订
ADR-001：per-module 全拆维持挂起，但**分层拆分**按下述路线启动。依赖链现为
`Runtime → Kernel → Foundation` 三层单向、编译期强制；运行时 18 模块经 3a 去环后
folder 依赖图为无环 DAG。

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

### 第三步依赖分析与 3a 实施记录：去环（2026-07-08）

**决策**：第三步范围收敛为 **3a（去环 + 门面自引用核查）**，不新增程序集；
3b（Framework.Boot 提取）、3c（18 路 per-module 拆分）维持挂起，仅在 ADR-001
触发条件出现时重启。动机仍为长期健康、无外部触发；结论：3a 拿走绝大部分结构
健康收益，3b/3c 在当前规模 ROI 存疑，边界留到真有需求时沿 DAG 一刀切。

**依赖分析结论**：拿掉 Foundation/Kernel 两底层后，18 个运行时模块**本身是
无环 DAG**，仅被 4 个"放错模块的跨层文件"打破成 4 个环——每个环的成因都相同：
一个视图/编排文件被塞进了本该在其下层的服务模块里。

| 环 | 唯一肇事文件 | 原模块 |
|---|---|---|
| Network→UI | `NetworkWaitingUI.cs` | Network |
| UI→Network、UI→Auth | `ReconnectPanel.cs` | UI |
| Auth→UI | `LoginAuthPopupPresenter.cs` | Core/Auth |
| Privacy→Analytics/RemoteConfig | `PrivacyCompliance.cs`（RTBF 编排器）| Core/Privacy |

**3a-去环（已实施）**：4 个肇事文件经 `git mv` 上移到 `Core/Composition/`
并按职责细分：`UIAdapters/` 承接 Network/Auth 与 UI 的跨模块适配，
`Privacy/` 承接 RTBF 本地抹除编排（同属 Framework 程序集、命名空间不变、prefab
按 .meta GUID 绑定不断），故零代码改动、零引用破坏。此后各服务模块目录不再含上行依赖文件，folder 依赖图成真 DAG
——这是未来沿 DAG 切 asmdef 的强制前置。

**规划期两处误判，经读码/编译更正**：

1. `PrivacyConsent → Analytics` 不存在——那些 `GameEntry.Analytics.CollectionEnabled`
   仅在 XML 文档注释的 `<code>` 示例里；实际代码只广播 `PrivacyConsentChanged`
   事件，Analytics 侧已正确订阅（`AnalyticsManager.RegisterPrivacyConsentListener`）。
   故 consent 层本就无环、无需事件反转；Privacy 的环纯由 `PrivacyCompliance`
   编排器与低层 `PrivacyConsent` 同目录造成，移走编排器即解。
2. "门面自引用清理"实为空操作——排查确认**没有任何 Manager 在自己的文件里调
   `GameEntry.<自己>`**（真自引用为零）。残留的 `GameEntry.X` 调用只有三类：
   ① 文档注释示例（业务的合法公共 API 用法，应保留）；② 真实跨模块边界
   （即 DAG 的边，保留至 3b DI）；③ 模块内兄弟类取本模块 Manager（如 Resource
   的 `GameObjectPool`/`AddressableGameObjectProvider`、Stage 的 Navigation→Manager、
   `InputBlockScope`）——它们走门面只因 Manager 无 `.Instance` 访问器，消除需引入
   实例访问器/构造注入，属 3b 范畴，本步不强行改。

**3a 后的门面定性**：`GameEntry.X` 是**对外公共 API**（业务/热更程序集 ~13 处
合法使用），不删；能拆程序集的前提是把"框架模块内部"的跨模块门面互调换成注入，
那是 3b 的核心工作量（ADR-001 所称"几周冻结"的真实来源）。

**验证**：编辑器占锁，MSBuild 重建 Framework + EditMode 测试全绿。

## ADR-003：单例访问命名规约 `.Instance` vs `.Shared`（2026-07）

**状态**：已决策。规约成文，后续新增照此判定；不改存量。

**背景**：代码里两种静态单例访问名并存，一度被疑为不一致。排查确认是**两种刻意区分
的模式**，非随意混用：

| | `.Instance` | `.Shared` |
|---|---|---|
| 基座 | `Singleton<T>` / `MonoSingleton<T>` 基类 | `static` 持有类（HttpClients / FileStorages / …） |
| 返回 | 具体类型 | **接口**（IHttpClient / IFileStorage / ISecureStorage / …） |
| 可替换 | 否——自造、自管生命周期的硬单例 | **是——带 setter / SetBackend 注入缝**，测试/宿主可换实现 |
| 存量 | `GameEntry`、`SaveManager` | Http / Storage / Serialization / SecureStorage / ErrorCenter / ErrorCodeRegistry / AnalyticsSchema |

**决策**：**保留区分，写成规约，不统一命名**。

- **`.Shared`** = 某**抽象/接口的可替换共享默认**，<b>必须</b>配注入点（属性 setter 或
  `SetBackend`）。对齐行业惯例：.NET `ArrayPool<T>.Shared` / `MemoryPool<T>.Shared`、
  Apple `URLSession.shared`。看到 `.Shared` 即知"此处可注入 mock"。
- **`.Instance`** = 具体**硬单例**（不可替换、拥有生命周期）。看到 `.Instance` 即知"别想换"。

**为何不统一成 `.Instance`**（曾被提议）：把 `.Shared` 改名 `.Instance` 是**降级**——
抹掉"可注入"信号、对接口的可替换默认叫 Instance 语义即错、破坏大厂惯例、94 处调用 +
公共 API 大改换负价值。真正的问题不是命名而是"规约没成文"，本 ADR 即补此。

**判定规则（新增代码照此）**：
- 该类型是**接口/抽象的可替换默认**、需要测试注入 → `X.Shared` + 注入 setter。
- 该类型是**具体硬单例**（一份、不可换、有生命周期）→ `X.Instance`。
- 框架 Manager 经组合根 `GameEntry.X` 暴露为**对外公共 API**，此为第三种、不受本 ADR 改动。

**连带决策（供 3b 门面解耦用）**：给框架 Manager 增 `.Instance` 访问器时用 `.Instance`
（它们是 GameEntry 拥有的具体单例、不可替换，正落 `.Instance` 语义），经
`FrameworkComponent<T>` 基类在构造时登记。用途：让"模块内兄弟类取本模块 Manager"从
`GameEntry.<自己模块>`（依赖 Core 门面）改为 `<Manager>.Instance`（同模块内），
消除 ADR-002 3a 点名的伪耦合——这是未来沿 DAG 切 asmdef 的强制前置。见该实施记录。

### ADR-003 实施记录：Manager `.Instance` 访问器 + 同模块自引用去门面（2026-07-09）

**状态**：已实施（低成本前置切片；跨模块门面互调的全量 DI 化仍属挂起的 3b）。

**机制**：Kernel 新增 CRTP 基类 `FrameworkComponent<T> : FrameworkComponent`
（`where T : FrameworkComponent<T>`），构造时登记 `public static T Instance`。组合根
`GameEntry` 经 `new T()` 造 Manager 时即登记，早于 `OnInit`；`GameEntry.X` 门面属性与
`X.Instance` 指向同一对象。

**转换范围（仅同模块自引用——兄弟类只为取本模块 Manager 而绕 Core.GameEntry）**：
4 个 Manager 改继承 `FrameworkComponent<T>`（Resource / Stage / Input / Scene），
其模块内 6 处调用改直取：

| 文件 | 原 | 现 |
|---|---|---|
| `Resource/GameObjectPool` | `GameEntry.Resource` | `ResourceManager.Instance` |
| `Resource/AddressableGameObjectProvider` | `GameEntry.Resource` | `ResourceManager.Instance` |
| `Stage/GameStageNavigationManager` | `GameEntry.StageManager` | `GameStageManager.Instance` |
| `Input/InputBlockScope` | `GameEntry.Input` | `InputManager.Instance` |
| `Scene/SceneBase`（2 处代码，doc 示例保留） | `GameEntry.Scene` | `SceneManager.Instance` |

**收益**：这四个模块的目录内代码不再为「取自己的 Manager」依赖 `Core.GameEntry`——拆 asmdef
时这正是会成环的那类边（模块→Core→模块）。保留的 `GameEntry.X` 只剩两类：对外公共 API、
真实跨模块边（如 `GameStageManager`→`GameEntry.Scene`，属 DAG 的边，留到 3b）。

**未动**：跨模块门面互调（Tips→Network、Audio/UI→Resource 等）——它们是真依赖，
换 `.Instance` 只是把「经 Core 取」变「直取」，属 3b 全量 DI 的工作量，本切片不含。

**验证**：Kernel/Framework/Tests dotnet build 全绿；自跑 run-ci EditMode + 资源门禁通过。

**补充（全量统一，2026-07-09）**：上一切片只转了有同模块自引用的 4 个 Manager，留下
「部分 Manager 有 `.Instance`、部分没有」的不一致 API 面（违反最小惊讶）。经复议：`.Instance`
是**访问约定**而非投机功能，约定应统一——遂把全部 **17 个** `FrameworkComponent` 派生 Manager
一律改继承 `FrameworkComponent<T>`（每个仅基类声明改一行、CRTP 零行为改动）。此后
「Manager 是否有 `.Instance`」可预测（都有）；`GameEntry.X` 继续作为对外业务门面，`.Instance`
为框架内部访问——边界靠 ADR-003 约定（`internal` 跨 Kernel→Framework 程序集不可行，故保持
`public`）。业务侧访问路径不变。（此处「internal 不可行」仅就 Kernel→Framework 跨程序集的
`.Instance` 访问而言，勿据此推断「internal 收口整体不可行」——边界硬化的完整讨论见下方
「ADR-003 补遗」。）

### ADR-003 补遗：边界硬化的正确杠杆是「包边改可见性」而非「拆 asmdef」（2026-07-09）

**背景与纠错**：先前记录（本 ADR 与 ADR-002）把「阻止消费方绕过门面直取
`NetworkManager.Instance`」的收口手段表述为「留到 3b 独立 asmdef 启用 `internal`」。
**这个成本估算是错的，特此纠正**：对一个以 UPM 包分发的框架，要守的边界**本就已经是程序集
边界**（`com.frameworkbase.*` 各编译成独立程序集）。因此收口**无需拆每个 Manager 成独立
asmdef**，只需在包这一层决定哪些类型 `public`。

**要防的耦合腐烂分两类，别混为一谈**：

1. **跨层反向依赖**（Foundation→Framework）——已由三层 asmdef DAG 编译期挡死（ADR-002）。最重要的一条，已到位。
2. **消费方绕过门面直取具体单例**——现仅靠「门面 + 约定」，编译器不管。本补遗针对这条。

**正确的收口路径（成本远低于拆 asmdef）**：

- `public` = 对所有消费方的**长期契约**（要管版本）；把 `GameEntry` 门面 + `INetworkService`
  等接口留 `public`，把 `NetworkManager` 这类具体实现改 `internal`——一刀对**包外**（游戏/热更
  程序集）消失，对**包内**（CRTP 的 `.Instance`、同门面）无损。仅在**同一** asmdef 内改可见性**即可**，非拆模块。
- `GameEntry.X return NetworkManager.Instance` 成立的前提是二者同程序集——它们本就同在 `core`
  包，故天然成立，无需引 DI；真正被挡住的只有**包外**代码。
- 测试程序集用 `[assembly: InternalsVisibleTo("Framework.Tests")]` 开定向后门，不影响测试。

**为何值得做（服务「公开面小而稳」这一健壮性支撑）**：大厂框架的健壮性核心不是某个关键字，
而是「对外只暴露接口/门面，具体实现自由重构而不破坏消费方」。把 Manager 从 `public` 收成
`internal` 是这条路的自然延伸，给未来重构 Manager 内部的自由。这与 .NET 生态（Roslyn /
ASP.NET Core / .NET runtime 及 Unity 引擎自身 C# 层）普遍的 `internal` +
`InternalsVisibleTo` 实践一致。

**为何分两步、别今天全改**：改 `internal` 是一次 **breaking change**——任何已直取具体
`Manager` 的消费方代码会编译失败。故：
- **新模块即刻遵循**：实现 `internal` / 只暴露接口 + 门面，不欠新债。
- **存量 Manager 收口**排进一次**带主版本号**的边界整理，给消费方迁移窗口，勿贸然打断当前 P1。

**更稳的过渡选项**：若想要 CI 约束又暂不动可见性，加一条**架构单元测试**（反射扫程序集引用，
断言「业务命名空间不得引用 `*Manager` 具体类型」，违反即 CI 挂红）。零 breaking、可逆；局限是
只对**同仓能编到**的代码有效，独立分发出去的消费工程测不到——那一种仍需 `internal`。

**结论修订**：删去「必须等 3b 拆 asmdef 才能收口边界」的旧表述。收口 = **包内改可见性 +
分两步迁移 + 架构测试兜底**，与是否拆 asmdef（3b/3c）无关，另有其自身 ROI 权衡，见
ADR-001/002。

## ADR-004：网络协议契约下沉 `Framework.Protocol.Abstractions`（2026-07-12）

**状态**：已实施。新增程序集 `Framework.Protocol.Abstractions`（asmdef 落 `Protocol/`）。

**背景**：`INetMessage / IResponse / IRequest<T>` 三个网络消息契约接口原在 `Framework`
运行时程序集内（`Network/INetMessage.cs`）。热更协议程序集 `GameProtocol` 仅为实现这三个
marker 接口，却引用了**整个** `Framework`——而 `Framework` 拖着 `Unity.Addressables /
HybridCLR.Runtime / Unity.TextMeshPro` 等重型依赖。`GameProtocol` 本身在
`HybridCLRSettings.hotUpdateAssemblies` 中（热更程序集），等于把重型 AOT 依赖拽进了热更
协议拓扑，且服务端无法复用纯协议程序集。

**决策**：把三个契约接口从 `Framework` 抽到独立薄程序集 `Framework.Protocol.Abstractions`：
- 仅依赖 `Google.Protobuf`（`isExplicitlyReferenced: 0`，自动引用）；`noEngineReferences: true`
  ——**引擎无关**，服务端可原样复用协议契约。
- **命名空间保持 `Framework.Network` 不变**（命名空间 ≠ 程序集名），故 `GameProtocol` 与
  `Framework` 内 `Network/*` 消费方**零源码改动**，只改 asmdef 引用。

**依赖重连**：
- `Framework` → 新增引用 `Framework.Protocol.Abstractions`（NetworkManager 等仍在 `Framework`，
  运行时逻辑不动）。
- `GameProtocol` 引用由 `["Framework"]` 收敛为 `["Framework.Protocol.Abstractions"]`，
  彻底脱离重型 Framework。
- `HotUpdate`（入口约定参考实现，将经 `GameProtocol` 消息发网络请求）补引契约程序集，
  使 `NetworkManager.RequestAsync<T>(IRequest<T>)` 可见。

新依赖图仍为无环 DAG（`Protocol.Abstractions` 为 sink）：
`Framework/GameProtocol/HotUpdate → Protocol.Abstractions`；`Framework.Network` 运行时
`→ Protocol.Abstractions`。

**入口名/入口类型可配置（已落地）**：此前入口程序集名 `HotUpdate` 与入口类型
`HotUpdate.Entry.HotfixEntry` 硬编码在 `VersionManager` / `HotUpdateManager.StartHotfix`，而热更
**清单**已可经 `AppConfig.HotUpdateAssemblyFiles` 配置——两者不一致。现补齐：新增
`AppConfig.HotUpdateEntryAssembly` / `HotUpdateEntryTypeFullName`，`VersionManager.EntryHotUpdateAssemblyName`
由 `static readonly` 改为配置优先属性，并新增 `HotUpdateEntryTypeFullName` 属性；`StartHotfix` 反射改读该属性。
**默认值不变**（回退 `HotUpdate` / `HotUpdate.Entry.HotfixEntry`），既有项目零迁移。一致性双保险：门禁 R4
静态校验「有效入口 ∈ 热更清单」（配置优先，回退单行解析 `DefaultCodePatchFileName`）；`VersionManagerTests`
用编译真相锁死「入口 ∈ 清单 + 入口类型为含命名空间全名 + 默认值即历史约定」。

**已落地的 asmdef 依赖门禁**：`Tools/ci/check-asmdef-deps.ps1`（纯静态、不需 Unity/License，
秒级）把本 ADR 与 ADR-001/002 的约束焊成 CI 强约束——R1 无环、R2 分层单向且核心层不引测试/热更/
业务、R3 被依赖的热更协议程序集不得引重型 `Framework`、R4 热更清单三方一致、R5 测试程序集
`autoReferenced=false`。已接入 `ci.yml`（`asmdef-gate` job，`tests` 前置）与本机 `run-ci.ps1`。

## ADR-005：可信多 CDN 回退的安全边界——Host 不是内容身份（2026-07-14）

**状态**：已实施。新增 `HotUpdate/TrustedCdn.cs`（`TrustedContentIdentity` / `TrustedCdnRouteSet` /
`CdnHealthTracker` / `TrustedCdnDownloadClient`），`AppConfig.UpdateCdnEndpoints` 配置项，运行时/构建
门禁共用校验 `UpdateSecurity.ValidateCdnEndpointConfiguration`；`TrustedCdnTests` 253 行锁定不变量。
配套的同批运行时安全硬化（磁盘失败关闭、缓存治理、网络生命周期恢复）不变量一并记于文末，避免只活在
代码注释里。

**背景**：单一 CDN 一旦局部故障（某边缘节点挂、某地区被墙、证书临时失效），热更整链路即中断，玩家卡在
更新页。直觉解法是「配一串备用域名，轮着下」——但热更下载的是**将被加载执行的远程代码**，把「多域名
回退」做成朴素的 URL 列表会打开两个致命缺口：

1. **Host 混入信任判定**：若某个 Host 被劫持或错配，它返回的字节只要「能下下来」就被拼进安装流程。
2. **跨 Host 断点续传**：A 下一半、B 续另一半，拼出来的根本不是任何一个已签名对象——完整性校验形同虚设。

而备用 Host 若来自 RemoteConfig 等**未签名**下发通道，等于给攻击者一个「不改包、只改配置」就能把代码
下载重定向到任意站点的开关。

**决策**：回退只作用于**传输层**，信任判定完全独立于 Host。

- **内容身份四元组**（`TrustedContentIdentity`）= `ManifestId(Guid)` + 相对路径 + `Size` + `SHA-256`。
  **Host 不属于身份**：不同 CDN 只有在四个字段完全一致时才被允许承载同一个对象；校验发生在下载完成
  **之后**，任一 Host 的产物都要过同一把签名/哈希尺子。
- **端点随包体构建，禁止远程下发**：`UpdateCdnEndpoints`（`UpdateCdnEndpointDefinition[]`）是 `AppConfig`
  字段，Host 变更必须走客户端构建 → 安全门禁 → 应用发布。注释与 Tooltip 均显式声明「不得由未签名
  RemoteConfig 覆盖 Host」。
- **构建期即拒绝不合规拓扑**（`TrustedCdnRouteSet.TryCreate`，运行时与 `HotUpdateSecurityBuildCheck`
  共用）：端点总数 ≤ 8；名称唯一且合法；`AppEnv` 必须与主环境**逐字符一致**（禁止 prod/qa 交叉回退）；
  每个端点必须是**独立传输 Origin**（scheme + IdnHost + port 三元组去重，否则不构成独立故障域）；渠道根
  必须含**独立环境路径段**、HTTP(S)、禁止 UserInfo/Query/Fragment/非规范化转义。
- **相对路径是回退的唯一输入**（`IsSafeRelativePath`）：调用方运行时不能传入新 Host；路径拒绝 `..`、
  绝对路径、反斜杠、`? # %`、越界字符集与超长段——杜绝目录穿越与 Host 走私。
- **跨 Host 不复用部分文件**：每次换 Host 前删除 `savePath` 与 `.download`，`forceRefresh:true` 全量重下；
  在没有 ETag/长度不可变性证明时绝不拼接不同对象。
- **按失败类型分级熔断**（`CdnHealthTracker`，进程内、按 Origin）：传输失败累计阈值 2 → 隔离 30s；
  **完整性失败隔离更久**（5min），因为它更可能是投毒/错配而非抖动；命中安全类失败（本地信任根缺失等）
  与 Host 无关，**立即 break 失败关闭**，继续回退也不可能恢复。

**同批运行时安全硬化不变量**（各自有实现+测试，此处只沉淀「为什么」）：

- **磁盘失败关闭**（`Storage/StorageCapacity.cs`）：预算 = Payload + 固定开销(4MiB) + max(最低保留 64MiB,
  Payload×10%)；卷空间查询失败显式返回 `Unknown`，`StoragePreflight.CanProceed` **仅** `Sufficient` 为真——
  查询失败**绝不**当作空间充足。在 `HotUpdateManager` 中于 `PrepareStagingSlot` **之前**门禁。
- **缓存治理保护事务槽**（`Storage/CacheCleanup.cs`）：Active/Pending/LKG/提交中槽 `IsProtected`，参与容量
  统计但**永不进入删除计划**；高(0.90)/低(0.70)水位与磁盘缺口**双触发**；删除顺序确定（Kind →
  最旧写入 → 路径）；`StorageAdmission.Ensure` 在 `Unknown` 时不做破坏性清理，清理后**以真实卷重查**
  为准入事实，绝不相信「计划释放量」。
- **网络生命周期 Epoch 模型**（`Network/NetworkLifecycle.cs`）：单调时间记后台窗口，后台暂停心跳/计时/
  退避；短后台（≤`NetworkBackgroundGraceSeconds` 10s）回前台先探活（`NetworkForegroundProbeTimeoutSeconds`
  5s 超时视为半开 TCP），长后台或网络代际变化（LAN↔Carrier / Fingerprint 变化）**废弃旧 Epoch** 后串行
  重连+重鉴权；`SessionExpired` 为永久失败，停止拿过期令牌空转；离线队列仅接受显式 ReadOnly/服务端去重请求。

**运维接线见 `HotUpdate/HOTUPDATE_RELEASE_GUIDE.md`**（CDN 配置规则、磁盘/缓存错误码、排障表）。

## ADR-006：配表分片——以一致性域为原子单元的多库模型与三级文案取值（2026-07-16）

**状态**：已实施（首批两片：`main` = config.db + `language` = language.db；`ConfigShardCatalog`
声明片目录，`ConfigManager` 多片路由，导出管线按片落库，LaunchFlow 首个文案前提前就绪 language 片）。

**背景**：配表此前是单一 `config.db`（首包 `StreamingAssets/RefData/`、热更经 Addressables
`RefData/config.db`、运行时活动副本在 `persistentDataPath`）。两个结构性问题：

1. **膨胀**：所有表挤一个文件——改一行文案要下发整库；表规模增长后下载 churn、导出耗时、
   出厂基线体积同步恶化，且无法按 locale/模块做差量。
2. **language 表需要得最早、更新得最频繁，却搭最晚的车**：库在 LaunchFlow Step 6b 才安装
   （Android 的 StreamingAssets 是 `jar://`，SQLite 无法直开，必须先提取），首装 Step 1~5 的
   Loading 文案只能落到 `Language.GetOrDefault` 的源语言默认值（三级兜底的第三级成了常态路径）。

**讨论中否决的方案——运行时行级双库叠取**（取 key 时先查热更库、miss 再查随包库）：

- **版本倒挂陷阱**：整包更新后随包基线比热更库新，盲目 hot-first 会用旧热更文案盖新随包文案；
  行级判定无法承载"谁更新"的语义（行没有版本，只有库有）。
- **关系型混版**：技能表引效果表时行级叠取会造出"新技能行 + 旧效果行"这种无法审计的组合。
- **破坏既有事务边界**：内容发行事务（LKG 快照、`.bak`、统一启动确认点、出厂回退）全部是
  **文件粒度**；行级叠取使"当前生效内容"不再对应任何单一文件，回滚/审计失去锚点。
- 运行时双句柄、双查询路径的常驻复杂度。

**决策**：**片（shard）是版本、下载、回滚、覆盖的统一原子单元**。三级取值语义
（热更 → 随包 → 兜底）在**安装期**消解为"活动片文件"，运行时每片只开一个文件：

1. **片 = 一致性域**：互相引用的表必须同片；片内整体替换，片间独立版本。首批按"加载时机 +
   更新频率"切两片：`language`（启动最早需要、文案改动最频繁）与 `main`（其余全部，默认片）。
2. **片目录 `ConfigShardCatalog`**（ConfigData/，纯静态声明）：表名 → 片文件名的唯一映射源，
   运行时路由与导出管线共用，杜绝两侧漂移。片文件缺失时表路由**回退 main 片并告警**——
   老项目单库零迁移，导出配置错误可见不致崩。
3. **版本门控与完整性不新建机制**：每片沿用既有 per-db 门控（schema 兼容检查 + 随包基线更新
   检测 + resourceVersion 防倒挂），热更片的完整性/版本身份复用 Addressables catalog + 签名发布
   清单（`ResourcesOut/RefData/{片}.bytes` 经目录约定自动收编为地址 `RefData/{片}`）——
   **不引入第二套签名清单**。
4. **事务按片实例化、确认点统一**：每片一个 `ConfigDatabaseInstaller`（`.tmp/.bak` 状态机不变），
   `Confirm/Restore/HasUnconfirmedBackup/FactoryReset` 按片迭代；一轮热更中任一片安装失败即
   整轮失败并回滚本轮已装片——绝不出现"新 language + 旧 main"的跨片错配。
5. **language 片提前就绪**：LaunchFlow 在第一条 Loading 文案之前 `EnsureShardReadyAsync(language)`
   （小文件同步级提取，后续启动为存在性检查）。首装提取窗口从"整库安装完成"缩到近零；
   `GetOrDefault` 三级兜底从常态路径退化为异常保险（提取失败/库损坏），**保留不删**。
6. **导出侧按片路由**：`ExcelExporter` 逐 sheet 查片目录决定落库文件；孤儿表清理按片执行
   （表迁片后自动从旧片删除）；`.bytes` 热更产物按片同步。language 等框架 Bootstrap 表
   （生成类已预置于 `ConfigData/Bootstrap/`）跳过代码生成、只导数据，避免热更侧长出重复类。

**扩片前置条件（记录，未实施）**：新增业务片之前必须先落**跨片外键校验门禁**（DataValidator
禁止跨一致性域引用，或仅允许引用稳定片）；language 片后续可按 locale 再拆（首包只带默认+当前
语言）；`main` 拆 core/activity 的触发阈值为单片 >8~16MB 或热更 churn 集中于少数表；下载体积
若仍是痛点走 hdiff 二进制差分，**不做行级 delta 表**。

**后果**：首包多一个数据库文件与一次小文件提取；ensure/confirm/restore 由单路径变按片迭代。
换来：改文案只下发 language 片不动大库；首装第一条 Loading 文案即可本地化；后续扩片是改
片目录声明而非动架构。

## ADR-007：通用补间接入 PrimeTween——直接暴露而非包门面，作硬依赖不加 define（2026-07-16）

**状态**：已实施。新增 `Tween/`（`TweenBootstrap` 配置引导 + `TweenAsyncExtensions` UniTask/取消桥 +
`TWEEN_GUIDE.md`）；`Framework.asmdef` 加 `PrimeTween.Runtime` 程序集引用（PrimeTween 的运行时 asmdef 名为
`PrimeTween.Runtime`，命名空间才是 `PrimeTween`；无 `versionDefines`）；`UIAnimator` 重写为
直接基于 PrimeTween 的 Tween/Sequence；`GameEntry.Awake` 早于 Manager 初始化引导 `TweenBootstrap.Initialize()`。
PrimeTween 由工程 `manifest.json` 提供（npm scoped registry `com.kyrylokuzyk`），与 UniTask/HybridCLR 同
「壳工程供依赖」模型、同为**硬依赖**。

**背景**：主干缺通用补间——`UIAnimation`/`UIAnimator` 只有窗口开关的手写 `Time.deltaTime` 逐帧插值，
场景/相机/数值动画无统一方案。审计（对标 DOTween/PrimeTween/QFramework）将其列为 P1 体验缺口。选型定
PrimeTween：零运行期分配、结构体句柄、AOT/IL2CPP 安全、WebGL 可 await、销毁目标即安全终止。

**决策一：不把 `Tween`/`Sequence` 再包一层框架门面。** 与 Http/Serialization/Storage 那类「接口 + `.Shared`
可注入默认」（ADR-003）**刻意不同**——补间库的价值就是其零分配流式 API：包门面要么重造整个动画表面
（数百方法）、要么照样泄漏 `PrimeTween.*` 类型，还会因委托/装箱引入分配，净负价值。业界惯例即直接用。
故业务/热更程序集 `using PrimeTween;` 直调；框架只补三块它该管的：① 启动期容量+默认缓动配置
（`TweenBootstrap`，杜绝运行期扩容 GC）；② UniTask+`CancellationToken` 桥（`TweenAsyncExtensions`：取消
= `Stop` 停在当前值、不抛、await 正常返回，对齐框架既有动画语义）；③ UI 过渡预设复用（`UIAnimator`）。

**命名护栏**：框架根命名空间 `Framework` 内**禁止**出现名为 `Tween` 的类型——否则 `using Framework;` +
`using PrimeTween;` 触发 CS0104 二义（与 README 记的 `Logger` 同类坑）。新增类型一律 `Tween` 前缀
（`TweenBootstrap`/`TweenAsyncExtensions`），`Tween` 这个名字只属 `PrimeTween.Tween`。

**决策二：作硬依赖，不加 `FRAMEWORKBASE_PRIMETWEEN` 之类 define + 回退。** 补间既已被架构决策选定为
框架标准、且 UI 依赖它，它就是**核心标准**而非可选厂商件——应与 UniTask/HybridCLR 同等按硬依赖处理，
不留 `#if` 平行分支。曾试过用 `versionDefines` 把它做成「缺包即回退手写 lerp」的可选依赖，评估后**撤销**：

- **过度设计**：平白多出一条要长期维护 + 双测的代码路径，收益只是「引用框架却没配 scoped registry 的工程
  不报红」——而这类工程本就无法编译（UniTask/HybridCLR 同样缺），可选补间并不能救它。
- **与自身惯例不一致**：框架对 UniTask（全异步基座）就是无 `#if`、无回退的硬依赖；补间同属被选定的
  底座能力，没有理由特殊对待。
- **define 的正当场景是「可选厂商集成」**：本仓库 `FRAMEWORKBASE_BUGLY_SDK`（Bugly 崩溃后端，有则接、
  无则用本地后端）才是 define 的正确用法——那是真可选的厂商件；补间不是。

**为何不进 Foundation/Kernel 底层**：补间消费方（UI/Scene/Camera）全在 `Framework` 运行时层，且 PrimeTween
是 Unity 重依赖，下沉无收益；`Tween/` 落 `Framework` 程序集即可，无需新 asmdef（延续 ADR-001「不轻易拆」）。

**已知可调项**：UI 过渡默认跟随 `Time.timeScale`（`UIAnimator.UseUnscaledTime=false`，与历史逐字一致）；
游戏若在 `timeScale==0` 弹窗需置 true 防时停卡住。补间容量默认 200，按真机 `Max alive tweens` 调
`TweenBootstrap.Initialize(capacity)`。

**门禁影响**：asmdef 依赖门禁只校验工程自有程序集（外部包引用被 `Resolve-ProjectRefs` 过滤），`PrimeTween`
引用不触发 R1..R5。

## ADR-008：三层启动模型——框架能力 / 中间层自带业务 / 热更业务入口（2026-07-22）

**状态**：已决策，待实施（本条为方案定稿，迁移分步执行，见文末「实施路线」）。

**背景**：配置驱动引导框架落地后暴露了分层渗漏——具体表现为两处「放错层」，且组合根
`GameEntry` 从「能力宿主」退化成「什么都往里塞的服务定位器」：

1. **引导引擎（`Guide/`）住在 `Framework` 主程序集**，与 UI/网络等底层能力同层，而它其实是搭在
   编排原语之上的一个**具体应用**；
2. **红点系统被劈成四处**：核心逻辑（`RedDotService/Tree/Provider/Catalog`）在最底层的
   `Framework.Foundation` 程序集，账号同步/导航在 `Framework`，徽标 UI 在 `Framework/UI`，编译器在
   `Framework.Editor`；
3. `GameEntry.Awake` 亲手 `new GuideRunner(...)`、`new GuidePresentationService(...)`、注册引导内置
   编排、订阅 `GuideCompleted/…` 清遮罩，并持有 `RedDots/Guides/GuidePresentation` 静态字段——
   这与它自己代码注释写的「框架只创建空服务，不持有业务拓扑」自相矛盾。

根因是缺一个明确的**中间层**：一批「框架自带、开箱即用、但属于业务而非底层能力」的小系统
（红点、引导，未来可能有签到/公告等）无处安放，只能挤进底层或组合根。

**决策**：确立三层启动模型，层间依赖单向、由 asmdef 编译期强制。

```
L3  热更业务入口 (HotUpdate / Clicker 示例)
        │  维护模块清单：host.Use(new RedDotModule()).Use(new GuideModule())
        ▼
L2  中间层「框架自带业务模块」  Framework.Modules（单一 asmdef）
        │  RedDot/ + Guide/，各模块实现 IFrameworkModule
        ▼
L1  框架能力  Framework（主）→ Framework.Foundation（编排原语等）→ Framework.Kernel
        │  提供 UI / UiTargets / 编排服务 / 通用遮罩件 / 模块宿主 + IFrameworkModule 契约
        └─ L1 不引用 Framework.Modules（编译期锁死铁律）
```

**要点与理由（按权重）**：

1. **依赖分层由 asmdef 边界强制；模块无需为此独立成 UPM 包**（延续 ADR-001/002/004）。asmdef 与 UPM 包
   是正交的两个维度——asmdef 管编译期依赖方向，UPM 包管分发单元，一个包内可容纳多个 asmdef，二者不是
   替代关系。红点/引导是随框架同节奏演进的自家小系统，不是 Bugly 那种需独立版本/发布的第三方件，为它们
   单独开 UPM 包（`package.json`/版本/依赖声明/发布流程）是过度设计。故在 `core` 包内新增 asmdef 承载
   中间层，用 asmdef 引用方向做编译期强制，分发仍随 `core` 包一体。

2. **中间层用单一 asmdef `Framework.Modules`**（Editor 侧对称建 `Framework.Modules.Editor`，把现在混在
   `Framework.Editor` 的红点/引导编译器归位）。刻意**不**给每个模块一个 asmdef——对当前仅两个自家小
   系统而言，per-module asmdef 与独立包同属过度工程。放弃的两点如实记录：
   - **模块间编译期隔离**：红点与引导本应正交，单 asmdef 下「引导不引用红点」只剩自觉，编译器不拦；
   - **asmdef 级物理可选**：不能只裁红点留引导（要裁一起裁）；但**运行时可选**（L3 不 `Use` 即不启动）
     仍逐模块可控，实际影响小。
   低成本补偿：目录 + 命名空间即边界（`Modules/RedDot/`、`Modules/Guide/`，命名空间保持
   `Framework.RedDot` / `Framework.Guide`）；红线——中间层内**模块之间默认不互相直接类引用**，
   真需协作走 L1 的编排/事件做中介；可选加一条架构单元测试断言模块命名空间互不引用。

3. **红点迁出 Foundation**（回答「红点放 Foundation 是否合适」：不合适）。分层判据是**概念职责**，
   不是「能不能编译进去」。红点核心逻辑碰巧无 Unity 依赖所以**能**塞进 Foundation，但 Foundation 的
   定位是「任何项目、任何系统都可能复用的中立基础设施」（序列化/HTTP/存储/**编排原语**/纯数据结构）；
   红点是有明确业务语义的系统（DAG 聚合 + 账号已看版本 + 服务端同步 + 徽标 UI），是「搭在基础设施上的
   通用业务」，属 L2。其「红点树」只服务红点、无第二消费者，所谓通用数据结构是伪通用，随模块一并迁出、
   不为其单独保留 Foundation 席位。**编排原语（`Foundation/Orchestration`）相反，留 Foundation/L1**——
   它是真中立原语（All/Any/Not 组合、触发绑定、动作执行），无业务概念，符合 Foundation 定位。

4. **引导表现能力从 L1 下沉 L2**（一处必须顺手修正的错放）：`UIOrchestrationBuiltins` 里的
   `GuideFocusTarget` / `GuideClearFocus` 两个 Action 及 `GuidePresentationService` 只为引导存在，却注册在
   L1 的 UI 编排内置里。它们归 `GuideModule` 在注册阶段自行注册。L1 的 UI 编排内置**只留中立能力**：
   窗口开关/生命周期、Target 点击、Delay、开关窗口。挖孔遮罩组件 `GuideMaskOverlay` 作为通用 UI 表现件
   可留 L1，建议改名去掉引导语义（如 `MaskHoleOverlay`），Focus/Clear 的编排封装随引导模块走。

5. **编排统一在 L1，两阶段启动**。厘清两种 catalog：**编排 catalog（Rule/Trigger/Action）= 全局共享
   配置**，由 L1 编排服务持有、被引导/业务共同消费；**GuideCatalog = 引导模块私有**。宿主两阶段：
   - **Phase 1 RegisterCapabilities**：各模块把 Evaluator/Binder/Executor 注册进 L1 编排服务（引导在此
     注册 GuideFocus/Clear；业务注册领域规则）——此阶段不得依赖别的模块已就绪；
   - **Freeze**：宿主冻结并 `Initialize` 编排 catalog，此后只读。编排 catalog 的**数据构建**仍由热更侧
     构建器提供（它认识 config 表），L1 只管「何时冻结、冻结后只读」；
   - **Phase 2 StartAsync**：各模块 new 运行器、`Initialize` 自己的私有 catalog、接线、开始监听。
   现有 `GuideBootstrap.Install` 里「一次性 Register + Initialize」即此两阶段的雏形，改造是把散装
   Install 收敛成宿主驱动的两阶段。

6. **`IFrameworkModule` 契约在 L1，实现在 L2，L3 组装**（依赖倒置，方向不破）。契约保持最小：
   `RegisterCapabilities()` / `StartAsync()` / `Dispose()`（+ 可选 `OnLowMemory()`）。宿主生命周期由
   `GameEntry`(L1) 驱动（持有 `List<IFrameworkModule>`、跑两阶段、逆序 Dispose、广播低内存）。模块清单由
   **L3 维护**（Clicker 的 `RuntimeCatalogBootstrap` 作示例），可选性 = 列不列进清单。

7. **启动锚点**：L1 能力在 `GameEntry.Awake` 就绪（含空的编排服务）；**L2 模块两阶段跑在「配置库就绪
   之后」**（现 `OnBeforeBusinessEntry` / `RuntimeCatalogBootstrap.Install` 时机），因为编排/引导 catalog
   内容来自热更 `config.db`。此时序与现状吻合。

8. **账号级生命周期不进模块契约**。红点已看版本（登录加载 / 登出回推）由 `RedDotModule` 在 `StartAsync`
   里自行订阅框架账号进入/退出事件，把现在散在 `GameEntry`/`AppFlow` 的红点账号接线搬进模块自身，
   保持契约不被账号语义污染。

9. **访问点归模块自身**。模块自带静态入口（`Guide.Runner` / `RedDot.Service`，由各 Module 启动时赋值）；
   `GameEntry` 保留**能力**入口（`UI/Rules/Triggers/Actions/UiTargets`），移除**模块**字段
   （`Guides/RedDots/GuidePresentation`）。

**重拆触发条件（本决策的已知演进点）**：**出现第一例「中间层内模块间直接依赖」争议时**，重开讨论是否
按模块拆分 `Framework.Modules` 为独立 asmdef。在此之前维持单一中间层 asmdef，不预先拆。

**后果与迁移注意**：

- **移除 `GameEntry.RedDots/Guides/GuidePresentation` 是 breaking change**（调用点改走
  `RedDot.Service` / `Guide.Runner`）。因框架处于 `0.x` 孵化期（无外部消费方、`CHANGELOG` 已明示 API 可能
  调整），**本次迁移直接断掉旧入口、一次到位、不留过渡兼容层**：同批改完所有调用点，`CHANGELOG` 标注
  breaking。顺势把具体 Manager/Runner 实现收 `internal`、对外只留门面 + 接口（ADR-003 补遗方向），
  同属这次边界整理。
- **红点从 `Framework.Foundation` 迁出是本次改动面最大处**——牵动一批 `using`/命名空间与 asmdef 引用；
  迁出后 Foundation 回归「纯基础设施」（编排原语/存储/序列化…），层次更干净。
- **MonoBehaviour 换程序集务必连 `.cs.meta` 一起 `git mv`**：Unity 靠 meta 的 GUID 定位 MonoScript，
  保住 meta 则挂了 `RedDotBadge`/`UITargetAnchor` 的 Prefab/场景引用不断；漏带 meta 会整批掉引用。
- **asmdef 依赖门禁扩规则**（`Tools/ci/check-asmdef-deps.ps1`，见 ADR-004）：新增「L1 不得引用
  `Framework.Modules`」的 R6，把本 ADR 的铁律焊进 CI。

**实施路线（待执行，每步独立提交 + 全量门禁，可回退）**：

1. L1 落**模块宿主 + `IFrameworkModule` + 两阶段**骨架（先不迁任何模块，空跑通过）；
2. 建 `Framework.Modules` asmdef，**迁引导**进 `Modules/Guide/`：`GuideRunner/Catalog/ProgressStore` +
   下沉 `GuidePresentation` 与 `GuideFocus/Clear` Action，`GuideModule` 实现契约、暴露 `Guide.Runner`；
3. **迁红点**进 `Modules/RedDot/`：核心逻辑从 Foundation 迁出，账号接线搬进 `RedDotModule`，暴露
   `RedDot.Service`；
4. **清 `GameEntry`**：移除模块字段与接线，只留能力入口 + 模块宿主驱动；L3 `RuntimeCatalogBootstrap`
   改为维护模块清单；
5. Editor 侧归位（`Framework.Modules.Editor`）+ asmdef 门禁加 R6 + 可选架构单元测试（模块命名空间互不
   引用）。
