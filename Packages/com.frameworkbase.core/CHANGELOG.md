# Changelog

本包遵循 [语义化版本](https://semver.org/lang/zh-CN/)。版本策略：
`0.x` 为孵化期（API 可能调整）；首个商业项目立项时冻结为 `1.0.0`，此后破坏性变更必须升主版本。

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
