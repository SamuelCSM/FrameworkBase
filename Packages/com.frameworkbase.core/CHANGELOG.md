# Changelog

本包遵循 [语义化版本](https://semver.org/lang/zh-CN/)。版本策略：
`0.x` 为孵化期（API 可能调整）；首个商业项目立项时冻结为 `1.0.0`，此后破坏性变更必须升主版本。

## [Unreleased]

### 新增

- **弹窗/流程序列队列 L2 模块 `PopupQueueModule`**（沿用 ADR-008 既有模块扩展机制，不改分层/依赖方向/
  公共契约，无需新 ADR）：借鉴 ALQueue 的序列消费思路，多来源弹窗请求不并发弹出，而是入队后一次只
  展示一个、关闭后再放下一个，按优先级（同级 FIFO）决定顺序。纯核 `PopupQueue`（不依赖 UnityEngine、
  可单测：一次一个/优先级降序/同级 FIFO/Key 去重择优/正在展示同 Key 丢弃）+ 业务实现的
  `IPopupPresenter`（"展示至关闭才返回"的 async 契约）+ 模块以"泵"串行消费（单弹窗展示异常隔离不卡队列、
  会话取消停止泵、账号退出清待展示队列）。访问点 `Popups.Service` 于 Phase 1 发布，`popup` 调试命令查队列
  状态。刻意不做抢占（打断需保存/恢复现场，属业务策略）。纯核 EditMode 11 例。

- **Addressables 远端 bundle 布局审计（规则 11~13）**：`AddressablesValidationRules` 在既有重复隐式依赖
  （规则 9）、组更新粒度（规则 8）之上，补三条远端组 bundle 布局咨询规则——① 命名不含内容哈希
  （NoHash/FileNameHash）→ 内容热更后 CDN 可能按 URL 供旧字节（热更特有隐患，关联 ADR-005/009）；
  ② 未压缩 → 下载体积翻数倍；③ PackTogether 且条目超阈值 → 改一个资源重下整包、补丁粒度过粗
  （与按体积的组超限互补）。模型加与 Addressables API 解耦的 `BundlePackingKind/BundleNamingKind/
  BundleCompressionKind`（采集层从 `BundledAssetGroupSchema` 映射，规则层不碰 Addressables）。均为
  Warning、不阻断构建，随既有 `AddressablesValidator` 门禁在 CiGate 里输出。EditMode 4 例。

- **预加载按设备档位限流并行 `ResourceManager.PreloadAssetsAsync`**：此前批量预加载是串行
  `foreach await`（低端不安全地无上限也谈不上、高端白白丢吞吐）。现按 `DeviceTierService.Tier`
  经纯映射 `DeviceTierResourceTuning.PreloadConcurrency`（Low=1 串行保内存 / Mid=3 / High=6）决定
  同时在途的 Addressables 加载数：低端窄路削内存/IO 峰值、高端多路提吞吐；worker 共享游标领取地址，
  同地址并发由既有句柄缓存去重兜底。新增 `PreloadConcurrencyOverride` 供压测或项目自有档位判断时钉死。
  并发度纯映射 EditMode 覆盖（低端必串行、随档位单调不减）。

- **资源热更闭环——远程 Catalog 纳入已验签发布身份（ADR-009）**：此前资源热更链断裂——
  `BuildRemoteCatalog=0` 且无代码打开，远程 catalog 从不产出（客户端 `CheckForCatalogUpdates`
  永远查不到更新）；即便产出，其完整性也只由 Addressables 无签名 `.hash` 守，不在 RSA 签名覆盖内。
  现补齐四段：① `UpdateInfo` 增签名覆盖的 `ResourceCatalog` 内容身份（FileName/Size/SHA-256），
  `ValidateManifest` 在 `ResourceVersion` 增长时门控要求它、缺失即失败关闭；② 客户端应用远端
  Catalog 前经 `CatalogUpdateFlow` 下载字节按已验签 Size/SHA-256 校验，不符即新终态
  `IntegrityFailed`、绝不激活未签名的资源目录；③ 发布管线 `BuildAddressables` 临时开启
  `BuildRemoteCatalog`（finally 恢复，工程资产保持 0）、定位产出的 `catalog_*.json` 回填内容身份；
  ④ `GenerateManifest` 构建期门禁：含资源更新时清单必须携带该身份。纯代码更新与老项目零迁移
  （`ResourceVersion` 未增长时该字段可空）。**注意**：客户端验签门的 Catalog 取字节路径与发布管线
  的远程 catalog 产出需一次真机发布演练验证。

- **配置驱动全局引导框架**：新增稳定整数 `GuideId / StepId / WindowId / TargetId`、通用
  `RuleService / TriggerService / ActionService`、事件驱动 `GuideRunner`、强类型断点存储以及 Target 挖孔表现。
  `UIWindow.xlsx + Guide.xlsx` 沿用标准 `xxxRef / xxxTable / config.db` 管线，关系表使用 `ConfigListBase`；
  专用编译器校验跨表引用、号段与退休 ID，并确定性生成 `UIWindowIds.g.cs / GuideIds.g.cs`。Clicker 商店提供
  “窗口 Ready → 检查目标 → 挖孔 → 真实按钮点击完成 → 清遮罩”的完整配置样例。复杂业务可按 TypeId 注册
  强类型 Payload Factory 与自定义 Evaluator/Binder/Executor。见 `Guide/CONFIG_DRIVEN_GUIDE.md`。
  `GuideRunner.TryStartAsync` 返回 `GuideStartResult`（`Started/Rejected/Queued`），区分“拒绝启动 / 已排队 /
  已启动”；步骤在进入时被自定义触发器同步完成时可正确链式推进而不会误判失败或吞掉信号；引导在完成/取消/
  失败的任意路径都会兜底清理挖孔遮罩，避免遗留全屏遮罩卡死操作。

- **红点亮起路径导航器 `RedDotNavigator`**（把 `GetActivePath` 接成"点击入口红点→逐级跳转到点亮来源"）：
  按节点注册跳转处理器（打开页面/切页签），`Navigate(entryId)` 沿亮起路径从入口到来源依次触发命中的
  处理器；入口未点亮返回 0，单个处理器异常隔离上报不阻断其余跳转。纯逻辑无 Unity 依赖，EditMode 用例 5 个。
- **红点徽标图标变体 `RedDotBadge.StyleVariant`**（按样式切换美术根）：可配置一组"样式→美术根"，徽标可见时
  只激活与当前 `DisplayMode` 匹配的根、其余关闭，支持小红点 / NEW 角标 / 感叹号图标等美术变体切换。
  切换判定收口到纯函数 `RedDotBadgePresentation.ShouldShowVariant`；未配置变体则保持原有 Badge Root + 文本
  表现。Inspector 已暴露该字段。
- **ServerAccount 已看同步 `IRedDotSeenSyncBackend` + `RedDotServerSeenSync`**（多端红点一致性的框架级闭环）：
  抽象出业务实现的拉取/上报后端，框架负责"取 max 版本"冲突合并与拉取/回推编排——登录拉取并合并、
  本地领先时回推、会话内 `Acknowledge` 触发去抖上报、登出前捕获快照最终回推。协议、鉴权、重试仍由
  后端实现。`RedDotService` 新增非破坏性 `MergeSeen`（逐 Signal 取 max）与 `ServerSeenChanged` 事件；
  `RedDotAccountSession.ConfigureServerSync` 注册后端后，每次账号会话自动完成同步。EditMode 用例 5 个。
- **红点徽标展示样式扩展（New / Exclamation）**：`RedDotBadge.DisplayMode` 在 DotOnly/Number 之外新增
  `New`（显示 NEW）与 `Exclamation`（显示感叹号），展示解析收口到纯函数 `RedDotBadgePresentation` 便于
  单测。样式属于 UI 表现、不进入逻辑配置——同一红点 ID 可被不同入口按不同样式呈现。枚举序号保持稳定，
  兼容既有 Prefab 序列化。EditMode 用例 5 个。
- **红点亮起路径导航 `RedDotService.GetActivePath(id)`**（点击红点跳转到具体来源的数据支撑）：从入口
  节点沿"有值"的子边逐层深入，返回一条到最深亮起 Signal 的确定性路径（每层在多个亮起子节点中按
  FinalCount 降序、ID 升序择一，同状态下可复现）。入口未点亮返回空列表。UI 可据此把玩家一路带到点亮
  红点的叶子功能。诊断命令新增 `reddot path <ID|Key>` 直观打印路径。

### 优化

- **红点增量结算/通知零 GC 分配**：`FlushDirty` 改用节点上的 `Affected` 标记 + 复用缓冲（受影响集合、
  传播队列、变化集合、通知快照、拓扑序比较委托全部复用 `Clear()`），去掉旧实现每次 Flush 新建的
  `HashSet`/`Queue`/`List` 与 LINQ `OrderBy`；`Notify` 用复用缓冲替代 `list.ToArray()`。登录批量写入
  和高频事件驱动下不再产生临时分配。行为语义不变。
- **红点帧末合并（可选）`SetFrameCoalescing` / `FlushPending`**：启用后批处理之外的写入只标脏，由帧驱动
  （GameEntry 的 LateUpdate 自动接管）在帧末统一结算一次，避免一帧内多来源写同一子树造成的重复聚合与
  多次 UI 刷新；读接口（`GetCount`/`Snapshot`/`GetActivePath` 等）按需先行结算，保证"读到自己的写入"。
  未启用时行为与历史完全一致（逐笔即时刷新），单测默认走即时模式。

- **banned-API 静态门禁 `Tools/ci/check-banned-apis.ps1`**（运行时代码的 API 红线焊进 CI）：纯文本
  扫描（无需 Unity，Windows/ubuntu 双平台），只管跑在玩家设备上的代码（Editor/Tests 不扫）。三条规则：
  `local-time`（DateTime.Now 可被玩家改表、跨设备不可比→用 UtcNow/ServerTime）、`thread-sleep`
  （阻塞卡帧）、`gc-collect`（全量 GC 集中卡顿，只允许加载屏/调试命令等遮蔽时机）。豁免须行内
  `banned-api-allow: <规则id> <理由>`（当前行或上一行），随代码进评审——存量 9 处合法使用已逐处
  写明理由。接入 run-ci（静态先行，先于 Unity）与 ci.yml 静态门禁 job；有牙验证：注入违规 exit 1。
- **本地通知排程 `Notifications/`**（体力满/签到/活动提醒的策略收口）：`LocalNotificationPlanner`
  （纯逻辑）注册表与结算分离——业务游玩期间随时 `Register`（同 id 覆盖）/`Unregister`/`Clear`，
  切后台时结算成系统排程清单：过期过滤、免打扰时段平移（落在窗口内推迟到窗口结束，支持跨午夜
  22→8，宁可晚提醒不可吵醒）、升序裁剪到条数上限（iOS 64 上限留余量默认 50，保留最近的——
  越近对拉回越有效）。生命周期框架接管（`LocalNotificationRelay` 由 GameEntry 挂载）：切后台/
  退出先清旧排程再按最新注册表重排，回前台全部取消（玩家在游戏里通知栏还挂"回来玩"是低级错误），
  后端异常隔离。`ILocalNotificationBackend` 平台抽象走 ICrashBackend 同款注入模式，主干日志兜底，
  原生实现（Unity Mobile Notifications/厂商通道）进扩展包；与 `ISdkPushService`（远程推送权限/token）
  分工明确。EditMode 测试 10 例。见 `Notifications/NOTIFICATIONS_GUIDE.md`。
- **纹理审计门禁 `Editor/TextureAudit`**（资源门禁第四项，问题纹理带病入包当场拦）：与 Addressables
  校验器同款「采集（Unity API）/ 规则（纯逻辑可单测）」分层。四条规则：Read/Write Enabled 判 Error
  （CPU 侧常驻拷贝内存翻倍，确需 CPU 采样走 `ReadWriteAllowlistPrefixes` 显式豁免、随代码进评审）；
  导入尺寸单边 >4096 Error / >2048 Warning（以实际入包尺寸计，被 maxTextureSize 压下来的不算）；
  未压缩 Warning（RGBA32 是压缩格式 4~8 倍）；Sprite 开 Mipmap Warning（UI 用不到 mip 链白吃 1/3
  显存）。只扫 Assets/（Packages 不可变不归本工程管）。接入 `CiGate` 资源门禁与菜单
  Framework → Audit Textures。EditMode 测试 8 例。
- **反作弊值类型 `Security/AntiCheatValue`**（对抗内存搜索改值的基线防护）：`AntiCheatInt` /
  `AntiCheatLong` / `AntiCheatFloat` 把真值异或实例密钥存储（内存搜不到明文），另存双输入校验和
  （值+密钥），直改混淆字段或校验和均在下次读取时失配并触发 `AntiCheat.TamperDetected`（业务在此
  埋点/处置，无订阅静默）。与原生类型隐式互转、算术照写；实例密钥无锁生成，同值不同实例混淆结果
  不同；`default` 态读 0 不误报。边界明确：提高门槛非不可破（注入级 hook 挡不住，强对抗接厂商
  SDK），权威数据以服务端为准，持久化取 `Value` 明文走 AES 存档、混淆态不落盘。
  EditMode 测试 8 例。见 `Security/ANTICHEAT_GUIDE.md`。
- **设备分级 / 画质自适应 `Performance/DeviceTier*`**（低端机保命、高端机吃满）：`DeviceTierClassifier`
  （纯逻辑）按内存/显存/核数把设备粗分低/中/高三档，规则刻意保守——任一已知维度踩低端线即判低端
  （误判低只是画质保守，误判高是 OOM 与卡顿投诉，代价不对称）；判高端要求内存已知且全部已知维度达标，
  内存未知封顶中端；阈值经 `DeviceTierThresholds` 按目标市场调整。`DeviceTierService` 启动早期
  （任何重资产加载前）分级并映射 Quality Level（低→0 / 中→中间档 / 高→最高档，Inspector 可关映射只留分级），
  玩家手动选档 `SetOverride` 持久化、传 null 回自动档，`AutoTier` 供设置界面展示推荐档。
  `perf_window` 事件新增 `tier` 字段——大盘按档位分组看实测卡顿率，用数据回调分级阈值。
  EditMode 测试 8 例。见 `Performance/DEVICE_TIER_GUIDE.md`。
- **线上性能采样（APM）`Performance/`**（正式包运行时性能从零采集到有大盘口径）：`PerfWindowAggregator`
  （纯逻辑）按分钟级窗口聚合平均 FPS / 最差帧 / 卡顿帧数（≥100ms）/ 严重卡顿帧数（≥500ms）/
  托管与 Native 内存峰值——阈值取绝对值而非相对目标帧率，大盘口径跨设备可比；`PerfSampler`
  组件接线（GameEntry 自动挂载，Inspector 可关），每窗口经 `AnalyticsManager` 上报一条 `perf_window`
  事件（约 1 条/分钟/玩家，复用既有批量与隐私合规管道），附 GC 增量与活动场景。切后台的半截窗口
  丢弃、恢复首帧巨额 deltaTime 跳过，不污染卡顿计数；`PerfSampler.Enabled` 静态开关供业务按
  RemoteConfig 灰度采样人群。与 `PerfHud`（开发期屏显）分工：HUD 看瞬时值，本模块喂线上大盘。
  EditMode 测试 7 例。见 `Performance/PERFORMANCE_GUIDE.md`。
- **薄引导框架 `Guide/`**（步骤流 / 断点 / 遮罩挖孔 / 触发接线四原语，纯逻辑与表现分离）：`GuideScript`
  构造即校验、构造后不可变的有序步骤序列；`GuideFlow` 驱动步骤推进——业务在 `StepEntered` 回调按步骤 id
  编排表现、步骤达成调 `CompleteStep(id)` 推进，乱序 / 迟到完成属接线错误直接抛（fail-loud）。**每步推进即写档
  存步骤 id**（非序号）：崩溃 / 杀进程重进 `Start` 从断点续，且线上剧本插入 / 重排步骤时按 id 重新定位，
  玩家不会因序号漂移续到错位的步骤上；断点步骤被删 / 改名（id 找不到）则从头重播不卡死。`IGuideProgressStore`
  抽象断点存储，默认 `PrefsGuideProgressStore` 落 PlayerPrefs（设备级），需按账号走的项目自行实现注入。
  `GuideMaskOverlay`（`MaskableGraphic`）全屏压暗 + 目标控件矩形挖孔，孔内点击穿透给真实控件、孔外触发
  `DimClicked` 做提示，挖孔每帧跟随目标。订阅者异常经 `ObserverErrorSink` 隔离。EditMode 测试 10 例。
- **本地化资源回退**（`Localization/`：地址@语言约定 + 候选链探测 + 切语言自动重载）：变体地址按
  `{基础地址}@{语言代码}` 约定命名（如 `UI/banner@en_us`），`LocalizedAssetResolver`（纯逻辑）按
  「当前语言 → 自定义回退链 → 默认语言 → 原始地址」生成有序候选，`LocalizedAssets` 逐个探测存在性取首个命中并缓存；
  释放须用返回的实际地址。**Catalog 热更后自动失效解析缓存**（`ResourceManager.CheckAndUpdateCatalogsAsync`
  在 Catalog 实际更新后调 `LocalizedAssets.ClearCache`，无需业务手动介入）。`LocalizedImage` 组件按候选链加载
  Sprite 并在切语言时自动重载（await 期禁用 / 销毁即归还引用计数，不悬挂），与 `TextMeshProEx` 文案刷新同款体验。
  `IResourceService.ExistsAsync` 补齐「只查 Catalog 不加载」的存在性探测。EditMode 测试 8 例。
- **红点 / Badge 树系统 `Foundation/RedDotTree`**（路径寻址计数聚合）：计数只写叶子（`SetCount`/`AddCount`），
  父节点值 = 子树叶子之和，增量聚合 O(深度) 更新；对非叶子写计数、对持数叶子挂子节点均属结构性错误直接抛
  （fail-loud，杜绝双重计数歧义）。叶子变化沿祖先链传播，路径上值变化的节点通知订阅者（值未变不通知）；订阅默认立即
  回调当前值，UI 绑定无需关心「先订阅还是先写数」。`ClearSubtree` 一键已读，`Snapshot` 稳定序调试快照。纯 C# 零
  Unity 依赖可自由实例化（独立玩法建局部树）。该旧树不再占用全局入口；`GameEntry.RedDots` 已迁移为下述配置驱动服务。
- **配置驱动全局红点 DAG `Foundation/RedDotService`**：稳定整数 ID、模块号段、Node/Edge 分表、多父节点、
  `Any`/`SumChildren`/`MaxChildren`/`SumUniqueSignals` 聚合、Provider 完整快照与增量刷新、Session/账号本地已看版本；
  `RedDotBadge` 改为 ID 绑定并带搜索/回显/旧路径迁移；五张源表进入标准客户端 ConfigData，运行时从
  `config.db.bytes` 组装目录，专用编译器只负责跨表校验和确定性 ID 常量。节点只填写模块内 `CodeName`，完整名称自动派生；
  工作簿枚举列使用真实枚举类型。`red_dot_edge_ref` 使用新增的无主键 `ConfigListBase<T>` 关系表能力，
  无需人工 EdgeKey 且允许多父节点。Provider 同时支持登录/重连完整快照与可选的响应式领域事件监听；
  `IRedDotWriter` 把增量写入限制在 Provider 声明的 Signal 内，多条监听由 Coordinator 在账号会话退出时统一释放。
  Prefab/Scene/CI 共用配置门禁，`reddot explain` 可追踪最终来源；旧 `RedDotTree` 保留。
- **日志回捞最短路径 `Diagnostics/LogDump`**（冲刷 → 打包 → 可选上报）：`LogArchiver` 把日志目录压成单个 zip 落独立
  产物目录（共享读打开正被写线程持有的当前日志文件，产物目录自带保留上限最旧先删，纯文件系统逻辑 EditMode 可测）；
  `LogDump.DumpAsync` 编排冲刷 / 打包 / 上报，上报通道由业务注入 `UploadHandler`（未注入只留存本地，回捞包本身即具备
  「玩家反馈时导出」价值），全部失败路径返回失败结果不抛，动作留崩溃面包屑。EditMode 测试若干例。
- **调试命令总线 + 真机调试面板**（`Diagnostics/`）：`CommandRegistry` 显式注册 + fail-closed 权限门禁
  （正式包默认 None，白名单授权由业务鉴权路径驱动；重名注册直接抛属装配错误）；`RuntimeConsole` 屏幕日志面板带命令
  输入行（非 Editor 的 Development Build 自动挂载）。`BuiltinCommands` 内置安全无副作用命令集：help / version /
  loglevel / perfhud / fps / timescale / gc / net / logdump / reddot / **lang（查看 / 切换当前语言）** /
  **guide（引导断点 status / reset / skip 调试）** / sysinfo；有业务后果的命令由业务自行按风险注册。
- **三大 Manager 补瘦接口**（`INetworkService` / `IResourceService` / `ISaveService`）：Network / Resource / Save
  对外暴露最小契约面，刻意不求全（Catalog 热更、下载预估等启动运维面只在具体类上），便于测试替身与依赖倒置。
- **`GameEntry` Manager 初始化注册表化 + 依赖拓扑 EditMode 门禁**：Manager 初始化改为显式注册表 + 依赖声明，
  拓扑顺序由依赖关系推导并在 EditMode 门禁校验（漏声明 / 环 / 顺序错在测试期即炸出），替代此前手写初始化顺序。
- 通用补间接入 **PrimeTween**（零 GC、AOT/IL2CPP 安全、WebGL 可 await）：新增 `Tween/`——`TweenBootstrap`
  启动期一次性配置补间容量与默认缓动（杜绝运行期扩容 GC）、`TweenAsyncExtensions` 提供 `Tween`/`Sequence`
  的 `ToUniTask(ct)` 桥（取消即停在当前值、不抛、await 正常返回，对齐框架既有动画语义）、`TWEEN_GUIDE.md`
  指南。框架**不再包一层补间门面**，业务/热更 `using PrimeTween;` 直调即可（理由见 ADR-007）。`UIAnimator`
  改由 PrimeTween 驱动（公共签名不变，`UIBase` 零改动；新增 `UseUnscaledTime` 开关，默认跟随 timeScale）。
  PrimeTween 与 UniTask/HybridCLR 同为**硬依赖**（选定的通用补间标准，非可选厂商件，故不加 define 门控），
  由工程 `manifest.json` 提供（npm scoped registry `com.kyrylokuzyk`，壳工程已内置）。`ARCHITECTURE_DECISIONS.md`
  新增 **ADR-007**。
- `DevAuthTools.HasPersistedSession()`（Framework.Editor 公开入口）：供验收驱动断言「登出/互踢后持久化凭据已清」，游戏侧编辑器程序集不再需要触碰 `AuthSessionStore` internal。配套：`GameEntry` 登出请求日志从 Warning 降为普通日志（登出属正常业务流，Warning 会污染 Play 验收「零告警」门禁）。
- `AppFlow` 应用主状态机（Login ⇄ InGame，骑在通用 `AsyncStateMachine` 上）：登录活动 → 身份贯通 → 业务入口（InGame.Enter）→ 挂起等登出 → 拆卸（InGame.Exit：业务退出→鉴权登出→清身份）→ 自动回登录页（此前登出只拆卸、无回登录路径）。纯逻辑全注入，EditMode 脱离 Play 单测 7 例；三个登出源（服务端互踢/玩家主动/渠道会话失效）统一改调 `RequestLogout(reason)`——只记原因+唤醒主循环，拆卸不再发生在事件回调栈深处，同会话多信号合并首个原因生效，登录态收到登出为 no-op，业务入口 await 期间的登出后置合并（入口完成后立即拆卸）；全部钩子异常隔离上报，主循环含 Faulted 防御恢复。`OnBusinessEntryAsync` 语义更新为每登录会话调用一次（业务须支持重入，Clicker 样例已天然满足）。
- `AsyncStateMachine<TState,TTrigger>` 强类型串行异步状态机（Framework.Foundation）：Builder 一次性构建并做拓扑校验（目标未声明/规则不可达/非法超时构建期即拒绝），运行期拓扑不可变；事务化提交（Exit+Enter 全成功才切状态），失败走显式 `OnRollback` 补偿且 fail-closed（缺补偿或补偿失败进 Faulted，须显式 `RecoverAsync`）；处理器内重入触发入队串行执行（链式超限判死循环）；同状态 Ignore/Reject/Reenter 策略先于守卫求值；支持同触发器多守卫规则选路与内部转换；有界审计历史 + 观察者异常隔离到诊断出口。EditMode 测试 19 例。
- 网络生命周期恢复：单调时间记录后台窗口，后台暂停心跳/请求计时/重连退避；短后台主动探活，长后台或 Wi-Fi↔蜂窝/网络代际变化废弃旧 Epoch 后串行重连与重鉴权；Token 过期停止空转重试，离线队列仅接受显式 ReadOnly/服务端去重请求。
- 可信多 CDN 回退：包内 Host 允许列表、环境/路径隔离、ManifestId+相对路径+Size+SHA-256 内容身份、每 Host 重试与熔断；current/清单/伴生签名/DLL 同策略回退，哈希异常立即隔离，跨 Host 无 ETag 证明时强制全量重下。
- 缓存治理策略：高/低水位与磁盘缺口双触发，Temporary→Orphan Staging→Obsolete Release 确定性清理；Active/Pending/LKG/提交中事务硬保护，清理后以真实卷空间重检结果决定准入。
- 热更安装磁盘空间失败关闭门禁：按 Payload、固定事务开销和动态/最低安全余量计算峰值预算；Android StatFs / 桌面卷查询不可用时显式返回 Unknown，禁止把查询失败当作空间充足。
- `AssetLease<T>` 显式资源所有权：幂等释放、同地址共享加载下的独立逻辑引用，以及取消等待后的迟到引用自动归还。
- 登录身份贯通后调用的 `GameEntry.OnBusinessEntryAsync`，供热更业务安全读取账号存档并进入主界面。
- 身份清空前同步调用的 `GameEntry.OnBusinessExit`，统一覆盖主动登出、服务端互踢、SDK 会话失效和应用退出；
  业务可在旧账号身份仍有效时保存数据、取消定时器并关闭 UI。
- `ConfigPipeline` 一键完成 Excel → 热更代码生成 → 首包/热更 config.db 导出与校验，支持菜单和 batchmode。
- 参考热更入口 `HotfixEntry.Start()` 接线业务装配（切片 D），热更/离线两种加载方式下业务会话钩子都就位。
- 文档：`ARCHITECTURE_DECISIONS.md` 新增 **ADR-005**（可信多 CDN 回退的安全边界——Host 不属于内容身份；同批磁盘失败关闭/缓存治理/网络生命周期不变量一并沉淀）；新增运维向 `HotUpdate/HOTUPDATE_RELEASE_GUIDE.md`（CDN 配置规则、磁盘/缓存错误码 `STORAGE_E_*`、前后台恢复参数与排障速查）。

### 修复

- ConfigManager 首装路径去噪：初始化时持久化数据库尚不存在属正常序列（LaunchFlow 随后从首包安装），该提示从 Warning 降为普通日志；「安装后仍无可用库」保持 Warning/Error。此前该噪声被验收器清库路径不对（未删真实的 `{persistentDataPath}/config.db`）掩盖，真实首装设备每次都会打出一条告警。
- AudioManager 改用 `AssetLease<AudioClip>` 记录每次播放所有权；自然结束、Stop/StopAll、Shutdown、加载取消/失败均幂等归还，且代际安全 Handle 阻止池化 AudioSource 被旧引用误操作。
- `.gitignore` 补齐 Addressables 内容构建生成的 `Assets/AddressableAssetsData/Windows.meta`（目录已忽略、其文件夹 meta 此前遗漏，资源发布后残留未跟踪文件）。
- **ResourceManager 并发/同步失败句柄泄漏**：命中共享句柄的等待者在加载失败时只减引用、不释放底层
  Addressables 句柄，与创建者的 `RollbackLoad` 回收路径不一致；并发加载同一地址且失败、由等待者最后把
  引用减到 0 时（缓存已被创建者移除），句柄无人释放。新增 `RollbackSharedReference` 统一"谁减到 0 谁
  释放"。同步 `LoadAsset` 两处一并修：命中在途句柄改阻塞完成再取值且仅成功才增引用（原实现在途读到 null
  却已增引用，引用超计）；新句柄加载失败改就地释放（原实现失败句柄既不入缓存也不释放）。
- **启动下载阶段句柄释放兜底 + 取消令牌贯穿**：`DownloadDependenciesAsync` /
  `DownloadAllRemoteDependenciesAsync` 的句柄改在 `finally` 统一释放（原 `catch` 路径漏释放，异常时泄漏），
  并支持循环内取消。启动作用域取消令牌（GameEntry 新增 `_bootCts`，早于既有 `_appFlowCts`）经
  LaunchFlow → 更新执行器逐层下传到 Catalog/资源/代码补丁下载，使应用退出/销毁能干净中止在途下载——
  此前启动下载阶段完全无令牌可用。

### 变更

- **包体门禁改「两档」（硬上限阻断 + 相对涨幅告警）**：新增 `BuildSizePolicy.totalBudgetBytes`
  （CLI `-buildSizeBudgetMB N`）绝对上限；`ci.yml` 走大厂形的两档——**硬上限**（100 MiB，超出才 Fail，
  不需要基线、不必逐版滚基线）作唯一阻断闸，**相对上次基线涨幅**仍逐版比对但降级为**仅告警**（保留回归
  可见性与归因，不拦开发速度）。解决孵化期功能快速累积、每次涨幅都要滚基线的运维负担，又不丢回归信号。
  上限量的是 universal APK（含 v7a+arm64 双 .so、全密度），真实按机型切片下载约其一半 → 100 MiB universal
  ≈ 50 MiB 单 arm64 实下载（目标轻量级）。不设预算时仍走原「相对涨幅**阻断**」模式（默认，向后兼容）。
  EditMode 新增预算模式 5 例（预算内放行含巨幅增长 / 超预算阻断 / 无基线也能判 / 跳过单类 / warnOnly 降级）。
- 登录/登出组合根统一贯通和清理 Save、Analytics、RemoteConfig、CrashReporter 的玩家身份。
- 内容发行的 Catalog、配置、AOT 与热更程序集共用 Pending/Active/LKG 确认边界和中断恢复语义。
- Clicker 等参考样例专属验收与业务语义测试归属壳工程，不进入可复用核心包。
- **入库思源黑体 SC 子集作 CJK 回退 + 字体覆盖门禁恢复严格模式**：新增 `Assets/FrameworkTemplate/Fonts/Committed/`
  下的 `SourceHanSansSC-Subset.otf`（思源黑体 SC Regular 子集到 GB2312 + ASCII，7542 字符 / 3.5MB，**SIL OFL 1.1**
  可再分发，随附 `OFL.txt`）与其动态 SDF 资产 `SourceHanSansSC SDF.asset`，挂进 `TMP Settings` 全局 fallback——
  替换此前无人引用又 gitignore 的 `CjkDevFallback`（系统字体、不可再分发）。动态图集按需从子集源字体补字形，入库体积
  由源字体（几 MB）而非预烘全字集大图集决定。`FontCoverageChecker` CI 门禁改为按<b>运行时字形解析链</b>评估：只查
  TMP 默认字体及其回退链（自身 fallback + 全局 fallback，递归去重），不再要求纯 fallback 用途字体各自独立覆盖全字集
  （消除「CJK 回退字缺拉丁字母」伪缺字）；动态字体按<b>源字体文件</b>的字形覆盖判定而非只看已烘焙图集。覆盖与运行时
  对齐、字体入库后，`ci.yml` 资源门禁恢复 `-strictFonts` 严格模式（缺字重新阻断，撤销 e0b19e2 的临时降级）。新增
  `Framework/Localization/Build CJK Fallback SDF` 菜单与 batchmode 入口 `CjkFallbackFontBuilder.BuildForBatch`
  从子集 OTF 重建动态 SDF 资产。**注意**：字体入包后 Android 包体增长，需按门禁提示滚动 build-size 基线。
- **热更低层入口 `HotUpdateManager.DownloadPatchAsync` 收 `internal`（破坏性，孵化期）**：该重载按调用方给定
  `PatchFile.Url` 单源下载，绕过可信 CDN 内容身份路由（无 `TrustedContentIdentity` 绑定、无"URL 属主更新根"
  校验、无多 CDN 故障熔断，见 ADR-005），仅按 Size/SHA-256 校验完整性但无法证明字段来自已验签清单。从公共
  API 面移除以防业务误用（ADR-003 补遗方向），仅保留给框架内部与存储类单测（`InternalsVisibleTo` 已覆盖）。
  正式代码补丁一律走 `DownloadCodePatchAsync`（从 `AppConfig` 构建 `TrustedCdnRouteSet`、按内容身份下载、
  Host 不参与信任判定）。无生产调用方，业务侧零迁移。

## [0.16.0] - 2026-07-09

### 新增

- **包体回归门禁 `BuildSizeGate`**（补齐"CI 门禁"P1 的包体子缺口，工具层）：拦截"包体一版版胖、
  难回溯是哪次胖"的运营隐患。属**构建后置**检查（需产物目录），独立于不依赖出包的资源门禁 `CiGate`。
  - **核心纯逻辑可完全自测**：`BuildSizeGate.Evaluate`（基线+当前快照 → Pass/Warn/Fail）零 Unity 依赖；
    `BuildSizeSnapshotIO` 目录扫描 / 基线 JSON 读写用临时目录单测。
  - **双阈策略** `BuildSizePolicy`：总量（百分比 10% + 可选绝对字节）+ 单类（百分比 25%，带 64KB 最小体积
    门槛防小文件抖动）；只查增长（缩小无害）；`failOnNewEntry` / `warnOnly` 可调。
  - **基线**默认 `Tools/ci/build-size-baseline.json`（应提交进仓库，"包体涨"= "基线更新"走评审）；
    首次无基线直接 Pass 并落盘；`-buildSizeUpdateBaseline` 或菜单 **Framework/发布/更新包体基线** 主动更新。
  - `BuildSizeCiGate.RunBuildSizeGate` batchmode 入口（ASCII 哨兵 `GATE_RESULT exit=N` 收口，同 CiGate）；
    `run-ci.ps1` 加 `-BuildSizeDir`（仅传入时跑，默认跳过；相对路径自动解析绝对）；`BUILD_SIZE_GATE_GUIDE.md`。
  - 单测 15 例（裁决矩阵 11 + 扫描/基线往返 4）。真实 Unity batchmode 端到端自测：首次建基线 Pass、
    回归（+28.6% 总量 / +40% 单类）精确拦截 exit=1。

## [0.15.1] - 2026-07-09

### 文档

- **`CLOUD_SAVE_GUIDE.md` 补"接入 SaveManager 的设计"章节**（固化决策，暂不实现）：明确 `ISaveSync`
  是 SaveManager 面向的策略缝（与传输缝 `ICloudSaveBackend` 分属两高度、不合并）、缝开在**封包字节层**
  （非明文层非裸文件层）、与现有 AES+HMAC 加密**正交零改动**（push 逐字节上行 / pull 逐字节下行后复用现有
  verify→decrypt→migrate）、落地时只动两处（`SaveEnvelope` 加同步计数器 `s`、SaveManager 加 `ISaveSync`+
  `ReconcileAsync`+写裸封包路径）、push 自动 pull 显式的钩子、以及三个利刃（跨 schema 版本降级、写后 push
  失败重试、account-bound key）。**当前刻意不实现**：该集成缝无外部消费者，等真实后端落地再接成本极低（YAGNI）。

## [0.15.0] - 2026-07-09

### 新增

- **云存档抽象 `ICloudSaveBackend` + 同步编排器 `CloudSaveSync`**（补齐"存档/账号"P1 的云存档子缺口）：
  框架主干只提供**缝 + 冲突决策**，厂商后端（Google Play Saved Games / iCloud / 自建服务端）进扩展包
  经 `SetBackend` 注入。遵循与崩溃后端一致的"主干接口 + 默认兜底 + Mock + 平台实现进扩展包"模式。
  - **离线优先**：本地存档永远权威可玩，云同步是尽力而为叠加层；后端不可用即 `Offline`，不阻断本地读写。
  - **决策与 IO 分离**：核心 `Decide`（比对两端元数据 → None/Upload/Download/Conflict）是纯函数，
    零 IO 零 Unity 可直接单测；`SyncAsync` 只透过后端做 IO（不碰文件，下载结果交调用方落盘），
    故配 `InMemoryCloudSaveBackend` 可整链单测。
  - **冲突机制**：同步计数器 `Version`（≠结构 `dataVersion`）为首要依据，同版本比 `ContentHash` 判分叉；
    真冲突交解决器，默认 `ResolveConflictByTimestamp`（新时间戳胜、并列保本地）。文档明示时间戳裁决会丢数据，
    价值存档应传自定义**字段级合并**解决器（合并规则属业务，框架只保证"检测到冲突并交出裁决权"）。
  - `NoOpCloudSaveBackend`（默认关闭，没接云也不崩）+ `InMemoryCloudSaveBackend`（测试）+ `CLOUD_SAVE_GUIDE.md`。
  - 单测 16 例：决策矩阵 7 + 默认裁决 3 + 整链同步 6（离线/上传/下载/一致/自定义解决器/默认关闭）。

## [0.14.0] - 2026-07-09

### 新增

- **`LanguageType` 扩充为常用语言全集**（承 0.13.0 plural/RTL）：从 zh_cn/en_us 两种扩到 15 种
  ——简中/繁中/英/日/韩/法/德/西/葡(巴西)/俄/阿拉伯/泰/越/印尼/土，每项标注对应配表列名。
  这是"框架能翻译到的语言全集"，具体项目开放哪几种由 app 建配表列决定；未建列的语言取词回退默认语言。
- **本地化使用指南 `LOCALIZATION_GUIDE.md`**：覆盖取词/回退、切语言、复数(配表 `_类别` 变体约定 + `GetPlural`)、
  RTL(`CurrentDirection` / `ContainsRightToLeft` + "字形整形交给 TMP"的边界)、结果码文案、伪本地化、
  「扩展新语言」五步清单。

### 变更

- **`Language.ToCode` / `ToType` 改表驱动**：枚举 ↔ 列名收敛到单一映射源 `CodeByType`，反向表自动派生，
  新增语言只改一处，杜绝两个 switch 漂移。新增映射往返单测（全枚举往返一致 + 列名唯一 + 写法容错）。

## [0.13.0] - 2026-07-09

### 新增

- **本地化 plural / RTL 最小集**（补齐国际化短板）：
  - `PluralRules`（纯逻辑、零 Unity 依赖）：按 CLDR 基数规则把数量判入 `zero/one/two/few/many/other`
    六类。内置 6 大规则家族——东亚(仅 other)、日耳曼/多数罗曼语(one/other)、法语(0,1→one)、
    俄乌东斯拉夫(one/few/many/other)、波兰西斯拉夫、阿拉伯(全 6 类)；**未登记语言安全退化为
    other**，不给错误形态。追求"规则家族正确"而非"语言全覆盖"，新增语言只需补一条分派。
  - `TextDirectionResolver`（纯逻辑）：`Of(lang)` / `IsRightToLeft(lang)` 判定语言书写方向
    （ar/he/iw/fa/ur/ps/… 最小集）；`ContainsRightToLeft(text)` 扫强 RTL Unicode 区段，
    给用户名/聊天等动态文本自动定向。方向决策归框架，字形整形仍由 TextMeshPro 负责。
  - `Language` 便捷封装：`GetPlural(keyBase, count[, args])` 依当前语言选 `{keyBase}_{类别}` 变体、
    缺变体回退 `_other`、仍缺返回 `keyBase` 兜底不吐空；`CurrentDirection` / `IsCurrentRightToLeft`
    暴露当前语言方向供 UI 镜像布局。
  - 单测 19 例（复数 10 + 方向 9），覆盖各类别边界、小数退化、负数、区域码解析、强 RTL 检测。

## [0.12.0] - 2026-07-09

### 变更

- **`.Instance` 访问器全量统一**（承 0.11.0，ADR-003）：0.11.0 只转了有同模块自引用的 4 个
  Manager，留下「部分有 `.Instance`、部分没有」的不一致。复议认定 `.Instance` 是**访问约定**
  而非投机功能，约定应一致——遂把全部 **17 个** `FrameworkComponent` 派生 Manager 一律改继承
  `FrameworkComponent<T>`（Analytics / Audio / Auth / Config / Event / HotUpdate / Network /
  RemoteConfig / Sdk / Timer / Tip / UI / GameStageNavigation 等）。每个仅基类声明改一行、
  CRTP 零行为改动；此后「Manager 是否有 `.Instance`」可预测（都有）。`GameEntry.X` 仍为对外
  业务门面，`.Instance` 为框架内部访问。

## [0.11.0] - 2026-07-09

### 新增

- **`FrameworkComponent<T>` + Manager `.Instance` 访问器**（门面解耦前置，ADR-003）：Kernel 新增
  CRTP 基类，构造时登记 `static T Instance`；组合根 `GameEntry` 造 Manager 时即登记，
  `GameEntry.X` 与 `X.Instance` 指向同一对象。
- **命名规约成文（ADR-003）**：`.Instance`=具体硬单例；`.Shared`=接口的可替换注入默认
  （对齐 .NET `ArrayPool.Shared` / Apple `.shared`）。二者语义不同、刻意保留，不统一命名。

### 变更

- **同模块自引用去门面**：Resource / Stage / Input / Scene 四模块改继承 `FrameworkComponent<T>`，
  其模块内「取本模块 Manager」的 6 处从 `GameEntry.<自己模块>` 改为 `<Manager>.Instance`
  （`GameObjectPool` / `AddressableGameObjectProvider` / `GameStageNavigationManager` /
  `InputBlockScope` / `SceneBase`）。这些目录内代码不再为取自己的 Manager 依赖 `Core.GameEntry`
  ——拆 asmdef 时会成环的那类边就此消除。跨模块门面互调（真依赖）与对外公共 API 不动。

## [0.10.0] - 2026-07-08

### 新增

- **凭证安全存储抽象 `ISecureStorage`**（P1）：给登录令牌等敏感串一个<b>不落普通存档</b>的持久化
  落点（强联网项目「跨重启静默重登」的前置）。主干定义接口 + 注入点，硬件级实现（iOS Keychain /
  Android Keystore）留扩展包——与崩溃后端 / SDK 同款「主干接口 + 平台实现」模式。
  - `SecureStorage.Shared` / `SecureStorage.SetBackend` 注入点。
  - 默认 `EncryptedPrefsSecureStorage`：设备密钥 <b>AES + HMAC 防篡改</b>，密文存 PlayerPrefs，
    复用 `AesHelper` 同一套设备绑定密钥；读时先验 HMAC 再解密，篡改 / 跨设备 / 损坏一律按读不到。
    <b>安全边界</b>：比明文强、能挡普通翻存档，但密钥源自可推断的 deviceUniqueIdentifier、非硬件级；
    高价值凭证请接 Keychain / Keystore 扩展包。
  - `InMemorySecureStorage`：测试 / 「重启即清空」语义用。
  - `SecureStorageTests`：往返 / 落盘非明文 / HMAC 防篡改 / 删除 / 注入路由，6 用例。

### 说明

- 本步只落抽象与默认实现，未自动把会话令牌接入持久化——令牌是否持久化是<b>业务安全策略</b>
  （非框架该替业务决定），故留 `AuthSession` 侧接线由业务按需调用 `SecureStorage`。

## [0.9.1] - 2026-07-08

### 修复

- **资源门禁误判失败**：batchmode 下即便 `CiGate` 调 `EditorApplication.Exit(0)`，Unity
  进程仍可能返回非 0 退出码（与测试跑道同款现象），导致 `run-ci.ps1` 把通过的门禁判成失败。
  改为以 `CiGate` 落日志的纯 ASCII 结论哨兵 `[CiGate] GATE_RESULT exit=N` 为准（UTF8 读取、
  ASCII 匹配，免受日志中文编码影响），不再信进程退出码。
- `ci.yml` 资源门禁步骤改 advisory（`continue-on-error`）：unity-builder 以进程退出码判定，
  同样受上述不可靠退出码影响，直接判定会误红卡 PR；强制门禁以本地 `run-ci.ps1` 为准。

## [0.9.0] - 2026-07-08

### 新增

- **CI 资源门禁 `CiGate`**（P1：把已有校验器接进门禁）：新增 batchmode 入口
  `Framework.Editor.CiGate.RunAssetGate`，把既有的 Addressables 深度校验与字体缺字检测
  串成一道<b>不依赖完整出包</b>的检查，拦截「包体一版版胖、真机豆腐块」等长期运营隐患。
  - Addressables 校验（复用 `AddressablesValidator.ValidateForBuild`）——Error 级<b>阻断</b>；
    Settings 不存在（纯框架壳）视为通过。
  - 字体缺字（新增 batchmode 安全的 `FontCoverageChecker.CheckFontsForCi`，扫全工程
    TMP 字体）——默认<b>告警不阻断</b>（避免图标字体/局部字库误报卡 CI），传
    `-strictFonts` 升级为阻断；config.db 不存在或无字体时跳过。
- `run-ci.ps1` 接入资源门禁（EditMode 通过后执行），新增 `-SkipAssetGate` / `-StrictFonts` 开关；
  `.github/workflows/ci.yml` 增资源门禁步骤（借 unity-builder 跑 executeMethod，复用同一许可）。

## [0.8.1] - 2026-07-08

### 变更

- **崩溃归因接线铺满**（P0 收尾，纯接线无新 API）：把 `CrashReporter` 的归因三件套接到既有链路，
  让崩溃报告能定位「谁、在哪个阶段、出了什么前兆」：
  - `LaunchTelemetryHelper.BeginPhaseMetric` / `FinalizeRunMetric` → 每个启动阶段留面包屑
    （`launch:stepNN_...`）——启动崩溃最有价值的上下文。
  - `AuthManager.SetState` → 登录状态迁移留面包屑（`auth:State reason code`）。
  - `GameEntry` 的 `ErrorCenter.ErrorReported` 订阅 → 服务端错误码留面包屑（`error:CODE`）。
  - `SdkManager.InitializeAsync` 成功 → 渠道名写入自定义键（`channel`），供分渠道排查。

## [0.8.0] - 2026-07-08

### 新增

- **崩溃后端抽象 `ICrashBackend`**（P0 可观测性补齐第一步）：主干只定义崩溃后端契约
  （`Install` / `SetUser` / `SetCustomKey` / `LeaveBreadcrumb` / `RecordManagedException` /
  `TryFlushPendingAsync`），厂商实现（Crashlytics / Sentry / Bugly）留各自扩展包，经
  `CrashReporter.Register` 注入、由 `[RuntimeInitializeOnLoadMethod(BeforeSceneLoad)]` 自注册。
  关键设计：崩溃捕获是两层——原生致命崩溃（SIGSEGV / OOM 杀进程 / ANR）只有厂商原生信号
  处理器 / NDK / ANR watchdog 能捕获、走厂商自身管道上报，框架搬不动字节；故接口不是
  `Analytics` 式 `SendAsync(batch)`，而是「尽早 Install 让原生捕获就位 + 透传归因上下文
  （用户 / 自定义键 / 面包屑）+ 转发托管异常为非致命」。
- `MockCrashBackend` 内存参考实现（单测断言 + 厂商实现对照）。
- `Tests/EditMode/CrashReporterTests.cs`：编排时序 / 归因转发 / flush 路由 / 监听挂接 +
  默认后端落盘字段 / 会话上限 / 无 URL 不上报，共 12 用例。

### 变更

- `CrashReporter` 由「实现」重构为「编排器」：挂托管异常监听、维护会话归因上下文、路由到
  注入后端；未注册后端时落默认 `LocalFileCrashBackend`（原落盘 + HTTP 上报逻辑等价搬入，
  仅覆盖托管异常）。`TryUploadPendingAsync` 改无参（上报端点由后端自读）；新增 `Register` /
  `Shutdown`（与 `Install` 对称，供应用退出 / 测试隔离）。
- 登录成功后 `GameEntry` 调 `CrashReporter.SetUser(userId)` 打通崩溃按玩家归因。

### 已知限制

- 默认后端只覆盖托管异常；原生崩溃 / ANR / OOM 需接入厂商扩展包（下一步：Bugly 参考骨架）。

## [0.7.4] - 2026-07-08

### 变更

- **运行时依赖去环**（ADR-002 第三步 3a）：依赖分析确认 18 个运行时模块本身是
  无环 DAG，仅被 4 个"放错模块的跨层文件"打破成 4 个环。将它们经 `git mv` 上移到
  `Core/Composition/` 并按职责细分：`UIAdapters/` 放 `NetworkWaitingUI`
  （Network→UI）、`ReconnectPanel`（UI→Network/Auth）、`LoginAuthPopupPresenter`
  （Auth→UI），`Privacy/` 放 `PrivacyCompliance`（Privacy→Analytics/RemoteConfig
  的 RTBF 编排器）。同属 Framework 程序集、命名空间不变、prefab 按 GUID 绑定
  不断，零代码改动、零引用破坏。此后各服务模块目录不再含
  上行依赖文件，folder 依赖图成真 DAG——未来沿 DAG 切 asmdef 的强制前置。
- 排查确认无"门面自引用"可清理（没有 Manager 在自己文件里调 `GameEntry.<自己>`）；
  残留 `GameEntry.X` 均为文档示例、真实跨模块边界或模块内兄弟类取本模块 Manager
  （消除需实例访问器/注入，属挂起的 3b）。范围决策与依赖全图成文
  `ARCHITECTURE_DECISIONS.md`（ADR-002 第三步）；3b/3c 维持挂起。

## [0.7.3] - 2026-07-08

### 变更

- **Framework.Kernel 程序集拆分**（ADR-002 第二步）：内核层
  `FrameworkComponent / MonoSingleton / Singleton / AppConfig / Telemetry /
  ErrorCenter` 与 `Event` / `Timer` 下沉为独立程序集 `Framework.Kernel`
  （asmref 聚合，共 15 源文件），依赖链自此为 `Runtime → Kernel → Foundation`
  三层单向、编译期强制。`GameLog` 一并从 `Utils/` 移入新目录 `Logging/`
  归入 Foundation（内核诸类依赖它，留在上层会成环）。
- **ErrorCenter 反转为零上行依赖**（本步唯一代码手术，行为等价）：埋点由此前
  直连 `GameEntry.Analytics` 改为暴露 `ErrorReported` 事件、组合根订阅转发；
  Tips/Event 耦合的 `DefaultErrorPresenter` 上移到 Framework 层
  `Core/DefaultErrorPresenter.cs`，Kernel 内仅留仅日志兜底呈现器，真正 UI 呈现器
  由 GameEntry 经 `SetPresenter` 注入。副产：埋点限流单测从"无法断言"升级为强断言。

## [0.7.2] - 2026-07-08

### 变更

- **Framework.Foundation 程序集拆分**（ADR-002 第一步）：`Serialization / Http /
  Storage / Enum` 四个零依赖目录经 asmref 聚合下沉为独立程序集
  `Framework.Foundation`（目录结构、命名空间、代码零改动），`Framework` 引用之，
  层间依赖自此编译期强制。Pooling（`ObjectPool`→`GameLog`/`IPoolable`）与
  Utils（`PerfHud`→`GameEntry`）存在上行引用，本轮不进，待解绳结后下沉。
  后续路线（Kernel → Boot/Runtime）成文 `ARCHITECTURE_DECISIONS.md` ADR-002。

## [0.7.1] - 2026-07-07

### 新增

- **新项目脚手架**（P2B-11）：菜单 Framework → New Project Scaffold——一屏写入
  PlayerSettings 三件套（产品名/公司名/双平台 Bundle ID）+ AppConfig 创建与核心字段
  填充（环境/游戏服/热更 URL），并输出 10 项人工派生清单到 Console（清 sample 协议、
  签名密钥、CI Secrets、事件/错误码字典注册、合规接线、门禁验证）——降低框架复用门槛，
  避免新项目带着壳工程的包名/演示协议上架。
- **架构决策记录 ADR-001**（P2B-12）：asmdef 拆分评估结论——现阶段不拆
  （解耦成本前置巨大、收益当下不成立），已有替代手段与重新评估触发条件、
  未来拆分切法均成文 `ARCHITECTURE_DECISIONS.md`。

## [0.7.0] - 2026-07-07

### 新增

- **隐私合规贯通**（P2B-10）：`PrivacyConsent` 版本化同意管理（同意绑定协议版本，
  改版后旧同意失效须重新征得；变化广播 `GameMessage.PrivacyConsentChanged`）；
  `AnalyticsManager.CollectionEnabled` 采集闸门（false 时 Track 直接丢弃——数据根本
  不产生而非缓存补发，FlushAsync 不出网；默认 true 行为不变）；
  `PrivacyCompliance.EraseAllLocalUserData()` RTBF 本地抹除编排（埋点队列与快照/
  远程配置缓存/全部账号存档/PlayerPrefs/崩溃记录/启动指标/文件日志，逐项异常隔离
  并返回执行报告）。边界如实：只清设备本地，服务端侧删除走业务后台流程。
  接线指南 `Core/Privacy/PRIVACY_GUIDE.md`；单测 6 例。

## [0.6.5] - 2026-07-07

### 新增

- **伪本地化 PseudoLocalizer**（P2B-9）：开启后所有经 Language 取出的文案变形为
  `⟦Ẃéĺćóḿé·~⟧` 风格——重音替换暴露字体缺字、+30% 填充提前暴露 UI 截断、
  ⟦⟧ 界标一眼识别写死没走本地化的文本；`{0}`/`{1:N0}` 格式占位符原样保留。
  仅 Editor / Development Build 生效（Language 出口条件编译），单测 6 例。
- **字体缺字检测**：菜单 Framework → Localization → Check Font Coverage——
  扫描 language 表全部语言列的全部字符，逐个检查选中 TMP 字体（含 fallback
  回退链递归、防环）是否覆盖，输出缺字清单（字符 + Unicode 码点），
  上线前跑一遍避免真机豆腐块。Framework.Editor 新增 Unity.TextMeshPro 引用。

## [0.6.4] - 2026-07-07

### 新增

- **UI 安全区/多分辨率适配**（P2B-8）：`SafeAreaFitter` 把 RectTransform 锚定到
  Screen.safeArea（避让刘海/挖孔/Home 条，各边可选掩码，越界脏数据 Clamp 保护，
  变化检测零成本跟随转屏）；`CanvasScalerAutoMatch` 按 屏幕/参考分辨率宽高比 动态
  设置 CanvasScaler match（更宽按高缩放、更窄按宽缩放，信封式适配不溢出不变形）。
- UIBootstrap 集成：`Auto Match Scaler`（默认开）自动挂载 match 适配；
  `Apply Safe Area To Layers`（默认关，存量项目零影响）开启后各层 Canvas 垫
  SafeArea 容器、GetLayerRoot 返回它，全部经框架打开的 UI 自动避让。
- 纯计算入口可单测（TryCalculateAnchors / CalculateMatch），单测 8 例；
  用法与两种接入方式取舍见 `UI/SAFEAREA_GUIDE.md`。

## [0.6.3] - 2026-07-07

### 新增

- **埋点事件字典（schema 校验）**（P2B-7）：`AnalyticsSchemaRegistry` 把事件契约
  代码化——事件名、必带/可选属性及类型（String/Bool/Integer/Float）、Strict 模式
  （字典外属性也算违规）。`Track` 时校验仅在 Editor / Development Build 执行
  （正式包零开销），违规打 Error 就地暴露但不拦截发送（埋点宁脏勿丢）；
  未注册事件同名去重告警。框架内置事件（launch_run / launch_phase /
  analytics_dropped / server_error）已预注册且与实发属性对齐。
  整数传 Float 无损放行、反向拒绝。单测 8 例；用法见 ANALYTICS_GUIDE.md。

## [0.6.2] - 2026-07-07

### 新增

- **断线待发队列（重连补发）**（P2B-6）：`NetworkRequestConfig.QueueWhileDisconnected`
  （默认 false）让**幂等**请求在断线/重连期间入队，重连 + 重鉴权成功后按 FIFO 补发；
  `QueueTtlMs`（默认 30s）到期、放弃重连、主动 Disconnect 均按失败收尾（返回 null）。
  队列上限 64 超限拒绝；补发只给一次机会（补发瞬间又断线不二次入队）；
  回调逐项异常隔离。框架绝不默认全量补发——非幂等请求的重发一致性只有业务能判断。
  队列纯逻辑（OfflineRequestQueue）单测 7 例；用法见 NETWORK_MANAGER_GUIDE.md。
  至此网络 RPC 语义补全：SeqId 请求-响应配对 + 消息级超时 +（opt-in）断线补发。

## [0.6.1] - 2026-07-07

### 新增

- **CI PlayMode 跑道**（P2-5）：新增 `Tests/PlayMode` 测试程序集与冒烟用例 3 例
  （UniTask 真实帧循环调度 / TimerManager 接真实 Update 驱动的单次与循环定时器 /
  PerfHud 挂载采样渲染）——EditMode 守逻辑正确性，PlayMode 守"接上真实玩家循环没散架"，
  用例保持场景无关，任何工程可跑。
- `Tools/ci/run-ci.ps1` 升级：EditMode 先行、通过后自动续跑 PlayMode
  （`-SkipPlayMode` 可快速自查），分平台产物 `Logs/ci/{editmode,playmode}-*.xml`；
  GitHub Actions 改 `testMode: all`（单 job 串行，避免个人版许可并发冲突）。
- **静态门禁 .editorconfig**：编码/缩进/换行统一 + C# 命名规则
  （私有字段 `_camelCase`、常量 PascalCase、接口 I 前缀）IDE 内即时 warning，
  评审不再纠结格式。

## [0.6.0] - 2026-07-07

### 新增

- **协议错误码字典 + 统一错误处理**（P2-2）：`ErrorCodeRegistry` 表驱动注册
  （精确 > 窄区间 > 宽区间 > 默认规则；区间做模块段兜底，新增码天然有提示）+
  `ErrorCenter.Handle(code, msg)` 一行完成 查字典→执行反应→限流埋点（同码 60s）。
  反应类型：Silent / Toast / Popup / PopupRetry / ForceLogout / Maintenance；
  文案回退链：localizer → key 原样 → 服务端 message → 默认文案。
- 分段约定：0 成功；负数客户端本地合成（`ClientErrorCodes`：超时/断连/解析失败，
  框架已内置规则）；1~999 框架保留；≥1000 业务按模块分段。
- 默认呈现器走 TipManager（弹窗类降级 Error Toast），业务经 `SetPresenter` 替换；
  新增 `GameMessage.ServerForceLogout / ServerMaintenance` 广播占号。
- 用法见 `Core/Errors/ERROR_HANDLING_GUIDE.md`；单测 9 例。
- `EventManager` 新增 `int messageId` 订阅/发布重载，`UIBase.ListenEvent` 同步支持
  `int messageId`，业务热更程序集可自建消息枚举（建议 20000 起按模块分段）而无需修改
  框架层 `GameMessage`。

### 改进 / 修复

- `RuntimeConsole` 自动挂载收紧到非 Editor 的 Development Build；组件即使被手动放进
  正式包场景，也会在 `Awake` 直接禁用，不订阅日志、不绘制面板。
- `Resource/ADDRESSABLES_GUIDE.md` 扩写为 Addressables 分组与打包指南，补充目录约定、
  分组决策、Shared 依赖、Profile、同步菜单与常见校验问题修复。

## [0.5.2] - 2026-07-07

### 新增

- **性能 HUD 叠加层 PerfHud**（P2-3）：屏幕顶部常驻一行——FPS（窗口均值 + 最差帧耗时，
  均值掩盖不了的卡顿尖刺单独暴露）、托管/Native/预留内存与会话 GC 次数、
  Addressables 存活句柄三计数（阶段切换前后不回落即有泄漏，配合 ResourceScope 定位）、
  网络 RTT（心跳采样）。GameEntry 自动挂载（Inspector 可关，`PerfHud.Visible` 运行时可切）；
  仅 Editor / Development Build 编译，正式包整类剥离零开销；文本每 0.5s 重建，帧内零分配。
- 帧统计聚合 `FrameStatsAggregator` 纯逻辑独立（HUD 数字来源），单测 5 例。

## [0.5.1] - 2026-07-07

### 新增

- **资源作用域 ResourceScope**（P2-4）：按 场景/阶段/功能 划定资源生命周期，
  Dispose 一次性归还全部借出（实例 + 按次数的资源引用），把"归还"从 N 处 Release
  收敛成一处，结构上杜绝句柄漏还。提前归还销账、Dispose 幂等、Dispose 后拒借、
  外部销毁实例跳过、await 中途被 Dispose 自动归还迟到引用。
- **泄漏检测**：Editor/Development 下未 Dispose 即被 GC 的作用域由终结器哨兵报
  Error 并附创建堆栈（正式包零开销）；ResourceManager 新增
  LiveAssetHandleCount / LiveInstanceCount / LiveLabelHandleCount 诊断计数（性能 HUD 用）。
- 作用域记账逻辑经 IResourceScopeHost 抽象与 Addressables 解耦，离线单测 8 例；
  用法见 `Resource/RESOURCE_SCOPE_GUIDE.md`。

## [0.5.0] - 2026-07-07

### 新增

- **Addressables 分组打包规范 + 深度校验器**（P2-1）：纯规则引擎（10 条规则）+
  采集层 + 构建门禁三层分离。Error 级（路径错配 / 场景混包 / 组缺 Schema）在
  构建玩家包与整包发布时直接终止；Warning 级覆盖隐式依赖重复打包（包体/内存双份的
  经典坑，按体积降序报告）、地址规范、remote label、单资产/组体积阈值、同步漂移、空组。
  菜单 Framework → Validate Addressables 升级为深度校验；规范文档
  `Resource/ADDRESSABLES_GUIDE.md`。规则引擎单测 12 例（测试程序集新增 Framework.Editor 引用）。

## [0.4.2] - 2026-07-07

### 修复

- **SaveData 版本迁移永不触发**：`dataVersion` 是可序列化字段，读档 `FromJson` 会把它
  覆盖回磁盘旧值，使旧判据 `savedVersion < dataVersion` 恒不成立、`OnMigrate` 永不执行。
  改为读档后以 `new T().dataVersion`（字段初始值，不被反序列化污染）取代码当前版本，
  与封包版本 `envelope.v` 比较决定是否迁移，迁移后归位版本号；子类授权模型不变。
  补 SaveManager 迁移测试 2 例。

## [0.4.1] - 2026-07-07

### 新增 / 改进

- **HTTP / 序列化统一抽象**：`Framework.Http`（`IHttpClient` + `HttpClients.Shared`
  可注入、`HttpRequest/HttpResponse`、`UnityHttpClient`、`HttpClientExtensions`、`HttpUrl`）
  与 `Framework.Serialization`（`IJsonSerializer` + `JsonSerializers.Shared`、
  `JsonObjectParser`、`JsonWriter`）。运行时联网/JSON 一律走这两层，不再直碰
  `UnityWebRequest`/`JsonUtility`；埋点后端、崩溃上报、RemoteConfig、VersionManager 已收口。
  规范见 `Http/HTTP_SERIALIZATION_GUIDE.md`（登记 `PatchDownloader` 为唯一运行时直连例外：
  流式下载 + Range 断点续传）。
- **GameLog 文件日志异步化**：写入改后台线程队列，主线程零阻塞磁盘 I/O；
  按体积轮转 + 按个数清理旧文件，长期运营真机不被日志撑爆存储；退出冲刷不丢尾部。
- **测试补齐**：AesHelper 加密核心 7 例（往返/随机 IV/HMAC 防篡改/设备绑定）、
  VersionManager 热更判定矩阵 8 例、SaveManager 端到端 6 例、GameLog 4 例、HTTP 2 例。

## [0.4.0] - 2026-07-07

### 新增

- **RemoteConfig 模块（远程配置 / 功能开关）**：`RemoteConfigManager` 负责
  三层取值回退（拉取值 → 代码默认值 → 兜底参数）、磁盘缓存 last-known-good
  （断网首装也有一致行为）、类型化取值与功能开关判定；开关支持条件对象写法
  （`enabled` / `rollout` 设备稳定分桶灰度 / `min_version` 版本门控）。
  `IRemoteConfigBackend` 后端抽象 + 内置 HTTP GET 后端（附带
  device/version/channel/env 查询参数供服务端定向），三方平台作扩展包注入。
  用法见 `RemoteConfig/REMOTECONFIG_GUIDE.md`。
- **热更灰度放量**：`version.json` 新增 `GrayPercent` 字段（0/缺省=全量，1~99=灰度），
  未命中分桶的设备按"无更新"继续；分桶盐含目标版本号（每次发布重新洗牌），
  同一发布内放量上调时已命中设备保持命中。判定 `VersionManager.IsDeviceInGrayRollout`，
  闸门接在 LaunchFlow Step 3（version.json 验签之后，字段可信）。
- `StableHash`（FNV-1a）：跨平台稳定分桶工具（string.GetHashCode 结果不稳定，禁止用于分桶）。
- `AppConfig.RemoteConfigUrl`、`GameEntry.RemoteConfig` 静态访问点；
  LaunchFlow 启动时并行拉取（不阻塞、失败静默沿用缓存/默认值）。
- RemoteConfig EditMode 测试 14 例（JSON 解析/取值回退/失败保留现值/磁盘缓存/
  开关判定/灰度边界与单调性/稳定哈希）。

## [0.3.1] - 2026-07-07

### 修复 / 改进（埋点管道，对齐大厂 at-least-once + 服务端去重范式）

- **事件幂等键**：信封新增 `event_id`（每条唯一 GUID，序列化时冻结）。管道是
  at-least-once 投递，采集端须按 `event_id` 去重才能得到精确计数——此前无幂等锚点，
  "回前台发完 → 进程被杀 → 重启重读落盘快照" 会重复上报且无法去重。
- **排空即清快照**：`FlushAsync` 队列排空后删除 `analytics_pending.jsonl`，
  消除上述最常见路径的重复；残余窄窗口（发到一半被杀）交 `event_id` 去重。
- **排水式冲刷**：`FlushAsync` 改为一次触发连发多批直到队列空（单次上限 20 批），
  空闲期积压不再每 15s 才发一批、需多轮才发完。
- 测试补充：event_id 唯一性、排水一次发完、单次排水批次上限、排空后删快照，共 11 例。

## [0.3.0] - 2026-07-06

### 新增

- **Analytics 模块（埋点事件管道）**：`AnalyticsManager` 负责公共维度封装
  （session/device/user/version/channel）、内存缓冲（上限 500，溢出补报
  `analytics_dropped`）、批量上报（≤50/批，15s 定时 + 阈值触发）、失败退避重试、
  切后台/退出落盘防丢与启动补报；`IAnalyticsBackend` 后端抽象 +
  内置 HTTP JSON / 日志两个后端，三方平台作扩展包注入。用法见 `Analytics/ANALYTICS_GUIDE.md`。
- `AppConfig.AnalyticsUrl`（自建采集端点；留空走日志后端）。
- `GameEntry.Analytics` 静态访问点。
- 启动指标接轨：LaunchFlow 收口时自动上报 `launch_run` + 逐阶段 `launch_phase` 事件
  （原本地落盘保留）。
- Analytics EditMode 测试 7 例（信封序列化/批量/失败重试/用户维度/溢出补报）。

## [0.2.0] - 2026-07-06

### 新增

- **Sdk 模块（平台 SDK 抽象层）**：`ISdkProvider` 四能力契约
  （Account 渠道登录 / Purchase 支付含补单确认流 / Push 推送 / Privacy 合规），
  `SdkManager` 注册机制（未注册渠道时 Mock 兜底，正式包告警暴露），
  `MockSdkProvider` 开发期即插即用假实现。主干不含任何渠道厂商代码，
  渠道实现作为独立扩展包接入，写法见 `Sdk/SDK_GUIDE.md`。
- `GameEntry.Sdk` 静态访问点。
- SdkManager EditMode 测试 8 例（注册/兜底/重入规则、Mock 登录与支付全流程往返）。

## [0.1.0] - 2026-07-06

### 首个包化版本

由壳工程 `Assets/Scripts/Framework/` 迁移为嵌入式 UPM 包（embedded package），
分发模型从"源码拷贝"升级为"版本化引用"：新项目可经 git URL / 本地路径引用本包，
修复经版本发布回流所有项目，不再各自分叉。

包含能力（迁移时点快照）：

- **Core**：GameEntry 组件生命周期（异常隔离/低内存链路）、LaunchFlow 九步启动
  （重试/强更闸门/阶段埋点）、AppConfig、Auth 状态机（可插拔后端）
- **HotUpdate**：HybridCLR 三版本热更闭环 + 供应链安全
  （prod 强制 HTTPS / version.json RSA 签名校验 / 补丁强制 MD5 / 构建期门禁）
- **Network**：TCP + 心跳 + 指数退避重连 + 重连后重鉴权 + TLS 指纹固定选项，
  proto-first 协议路由（Google.Protobuf，AOT 安全二进制路径）
- **Resource**：Addressables 封装（引用计数 / 实例与资源句柄分离）+ 对象池
- **UI**：层级 / 导航栈 / 对象池 / 遮罩 / 动画配置 + LoopScroll / TabGroup
- **ConfigData**：Excel→SQLite 管线（导出/校验/代码生成）+ 首包/热更库兼容检查
- **Save**：AES+HMAC 加密存档、账号目录隔离、原子写、按文件锁
- **Telemetry**：崩溃回捞（JSONL 本地 + 可选上报）、启动阶段耗时落盘
- **Editor 工具链**：HotUpdatePublisher / FullPackagePublisher（含清单自动签名）、
  UpdateManifestSigner、BuildEntry（batchmode 构建入口）、ExcelTool、AddressablesSetup
- **Tests**：EditMode 单测（事件/封包/对象池/校时/定时器/循环列表/热更安全）

依赖约束：UniTask、HybridCLR 为 git URL 包，须由工程 manifest.json 提供。
