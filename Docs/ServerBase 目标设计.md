# ServerBase 目标设计

> 状态：草案。配套 FrameworkBase 客户端底层框架的服务端基础工程。
> 本文是目标态 + 阶段验收清单，风格与执行纪律对齐 `ReleaseSystemTargetDesign.md`。

## 0. 背景与定位

FrameworkBase 客户端侧的对接面已经冻结：TCP 长连接（protobuf、心跳、重连、TLS 指纹）、
四个 HTTP 端点（Auth / Analytics / RemoteConfig / Crash）、热更静态资源（Release Center
产物 + IIS/CDN 托管，**非本工程职责**）。ServerBase 是承接这份契约的服务端底层框架：

- 与客户端同级的"底层框架"定位，**主干严禁业务概念**（背包/商店/战斗等一律不进）。
- 独立仓库，工程纪律照搬客户端已验证的那套：一项一提交 + 中文说明、纯逻辑与 IO 分离
  可单测、CI 门禁、ADR 记录架构决策、每模块中文 GUIDE。

## 1. 目标 / 非目标

**目标**
1. 客户端不改一行框架代码即可从"Mock/本地假登录"切到真服务端（`UseNetworkLogin` 直连）。
2. 协议单一事实源：与客户端共享同一份 `proto/`，两端各自生成，禁止手抄第二份契约。
3. 每个阶段都以"现有客户端真机/Editor 联调通过"为验收，不写无人消费的接口。

**非目标（首期明确不做）**
- 具体玩法逻辑、房间/匹配/帧同步（留扩展位，不做承诺）。
- 数据库选型与运维编排（k8s 等）：持久化先走抽象 + 内存/文件实现，单进程自宿主起步。
- 热更资源分发：已由 Release Center + 静态托管闭环，本工程只在联调拓扑中消费。

## 2. 技术底座与工程布局

- .NET（LTS 版本）+ ASP.NET Core 承载 HTTP；TCP 网关基于 System.IO.Pipelines。
- 解决方案分层（对齐客户端 asmdef 分层思路，用项目引用表达依赖方向）：
  - `Server.Kernel` —— 纯逻辑（会话状态机、路由、限流、时间服务），零 IO 依赖，可单测。
  - `Server.Protocol` —— proto 生成物 + 分帧编解码（与客户端 NetworkManager 的帧格式逐字节对齐）。
  - `Server.Gateway` —— TCP 接入、心跳、连接生命周期。
  - `Server.Web` —— Auth / Analytics / RemoteConfig / Crash 四端点。
  - `Server.Persistence.Abstractions` + 内存/文件默认实现 —— 对齐客户端"主干只定接口 + 默认兜底 + 注入替换"惯例。
  - `Server.Tests` —— 单测 + 契约测试（详见 §4）。
- **依赖门禁**：项目引用白名单检查进 CI（对齐客户端 `check-asmdef-deps.ps1` 的思路）。

## 3. 阶段分解（每阶段 = 一个可独立验收的切片）

| 阶段 | 内容 | 验收标准 |
| --- | --- | --- |
| 一 | 仓库骨架 + CI（build/test/格式化/依赖门禁） | CI 绿；空跑测试通过 |
| 二 | 登录/鉴权 HTTP 端点（游客登录、token 签发/校验、会话存储抽象） | 客户端 `UseNetworkLogin=true` + `AutoGuestLogin` 走通 LoginFlow，全程无客户端改动 |
| 三 | TCP 网关 + 心跳（分帧对齐、心跳回包带服务端 Unix ms 时间戳） | 客户端 `ServerTime.IsSynchronized == true`，PerfHud 显示真实 RTT；断线重连过 `NetworkDeviceAcceptance.md` 清单 |
| 四 | 消息路由与会话（dispatcher、单玩家消息串行化、模板切片消息落地） | 模板 Play 验收器（batchmode）对真服务端全绿 |
| 五 | 埋点接收端（AnalyticsUrl 的批量 JSON ingest、落盘/管道抽象） | `perf_window` / `launch_run` / `launch_phase` 端到端入库，`tier` 维度可分组查询 |
| 六 | RemoteConfig 服务端（版本化配置、灰度按会话 hash） | 客户端 last-known-good 缓存 + 灰度开关链路演练通过 |
| 七 | 崩溃接收端（CrashReportUrl） | 客户端 LocalFileCrashBackend 之外的 HTTP 路径打通（低优先） |
| 八 | TLS + 证书指纹联调（自签证书） | 客户端 `TlsCertSha256Pins` 配置下握手成功/指纹不符拒连两个用例都过 |
| 九 | 可观测性（结构化日志、指标、trace id 贯通客户端 sessionId） | 一次客户端会话可在服务端日志按 sessionId 串出完整轨迹 |
| 十 | 部署形态（自宿主 → IIS/服务托管，环境配置对齐 ReleaseProfiles 四环境） | dev/qa 两环境配置切换演练通过 |

阶段顺序 = 依赖顺序；二、三可并行。每阶段独立提交序列，验收不过不进下一阶段。

## 4. 契约冻结点（双端各自变更都必须过的门）

1. **帧格式与心跳**：心跳回包携带服务端时间戳的字段位置一经冻结不得改动（客户端校时依赖）。
2. **错误码域**：服务端错误码与客户端错误码注册表的映射关系单独成文。
3. **埋点 schema**：`perf_window` 等事件字段口径以客户端 GUIDE 为准，服务端只透传入库不改写。
4. **协议版本协商**：预留版本字段与"低版本拒绝"路径，首期实现为固定版本。
5. **契约测试**：`Server.Tests` 内用客户端同款 proto 生成物做编解码往返测试，作为 CI 门禁。

## 5. 本地联调拓扑

单机全链路：IIS（静态热更，`current.json` 指针 no-cache、版本目录长缓存、补 `.bundle/.bytes/.sig`
MIME 映射）+ ServerBase 自宿主（TCP 9000、HTTP 端点若干）。多 CDN 回退用双 IIS 站点模拟。
端口表与各端点 URL 在阶段一落成一张表，作为客户端 `AppConfigAsset(dev)` 的填写依据。

## 6. 风险与开放问题

- 持久化抽象的边界（哪些状态允许进程内、哪些必须落盘）在阶段四前出 ADR。
- 水平扩展：首期单进程，但会话路由留分区键位置，避免未来推倒。
- 帧同步/房间语义是否进入 ServerBase 主干：默认不进，若做则以扩展包形态（对齐客户端扩展包惯例）。