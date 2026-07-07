# Changelog

本包遵循 [语义化版本](https://semver.org/lang/zh-CN/)。版本策略：
`0.x` 为孵化期（API 可能调整）；首个商业项目立项时冻结为 `1.0.0`，此后破坏性变更必须升主版本。

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
