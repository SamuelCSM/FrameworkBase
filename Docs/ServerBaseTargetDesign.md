# ServerBase 目标设计（服务端配套工程）

> 状态：目标设计（Target Design）。ServerBase 为独立仓库（2026-07-18 已建立）。
> 本文迁入 ServerBase `Docs/` 后以服务端仓库份为**唯一权威**，客户端仓库原位置改为只读
> 指针；`proto/` **不随迁**——ProtoGen 工具链与双端消息定义留在客户端仓库作为事实源，
> 服务端经生成脚本跨仓库消费（FrameworkBase 根路径参数化，默认兄弟目录）。
> 实施按阶段推进，每阶段必须以"现有客户端（Editor/真机）联调通过"为验收；
> 实施中发现与现实冲突时，先回到本文对齐再动代码，不允许代码悄悄偏离设计。
> 文中"客户端对接面"各节均已逐条对照 `com.frameworkbase.core` 现行代码核实（2026-07）。

## 0. 背景与定位

FrameworkBase 客户端侧的对接面已经冻结：TCP 长连接（protobuf、心跳、重连、TLS 指纹）、
四个 HTTP 端点（Auth / Analytics / RemoteConfig / Crash）、热更静态资源（Release Center
产物 + IIS/CDN 托管，**非本工程职责**）。ServerBase 是承接这份契约的服务端底层框架：

- 与客户端同级的"底层框架"定位，**主干严禁业务概念**（背包/商店/战斗等一律不进）。
- 独立仓库，工程纪律照搬客户端已验证的那套：一项一提交 + 中文说明、纯逻辑与 IO 分离
  可单测、CI 门禁、ADR 记录架构决策、每模块中文 GUIDE。

## 1. 目标 / 非目标

**目标**

1. 客户端不改一行框架代码即可从"Mock/本地假登录"切到真服务端
   （`UseNetworkLogin=true` + `AuthServerUrl` / `GameServerHost` 直连）。
2. 协议单一事实源：与客户端共享同一份 `proto/`（TCP 消息）与本文的 HTTP JSON 契约，
   两端各自生成/实现，禁止手抄第二份契约。
3. 每个阶段都以"现有客户端真机/Editor 联调通过"为验收，不写无人消费的接口。

**非目标（首期明确不做）**

- 具体玩法逻辑、房间/匹配/帧同步（留扩展位，不做承诺）。
- 数据库选型与运维编排（k8s 等）：持久化先走抽象 + 内存/文件实现，单进程自宿主起步。
- 热更资源分发：已由 Release Center + 静态托管闭环，本工程只在联调拓扑中消费。
- 动态定向的配置下发服务：客户端已内建设备分桶灰度（见 §3.4），首期 RemoteConfig
  服务端只做版本化静态 JSON。

## 2. 技术底座与工程布局

- .NET（LTS 版本）+ ASP.NET Core 承载 HTTP；TCP 网关基于 System.IO.Pipelines。
- 解决方案分层（对齐客户端 asmdef 分层思路，用项目引用表达依赖方向）：

| 项目 | 职责 | 依赖约束 |
| --- | --- | --- |
| `Server.Kernel` | 纯逻辑：会话状态机、路由、限流、去重、时间服务 | 零 IO 依赖，可单测 |
| `Server.Protocol` | proto 生成物 + 分帧编解码（与客户端逐字节对齐，见 §3.1） | 只依赖 Kernel |
| `Server.Gateway` | TCP 接入、TLS 终结、心跳、连接生命周期、背压 | Kernel + Protocol |
| `Server.Web` | Auth / Analytics / RemoteConfig / Crash 四端点 | Kernel + Persistence 抽象 |
| `Server.Persistence.Abstractions` | 存储接口 + 内存/文件默认实现 | 对齐客户端"主干只定接口 + 默认兜底 + 注入替换"惯例 |
| `Server.Observability` | 结构化日志、指标、trace 贯通（见 §7） | 被各宿主项目引用 |
| `Server.Tests` | 单测 + 契约测试（见 §5） | —— |

- **依赖门禁**：项目引用白名单检查进 CI（对齐客户端 `Tools/ci/check-asmdef-deps.ps1` 的思路）。

## 3. 客户端对接面契约（以代码为准，已逐项核实）

本节是两端契约的权威描述。列出的字段名、字节序、语义全部来自客户端现行实现，
服务端**不得**擅自"优化"任何一项；确需变更走 §5 冻结点流程。

### 3.1 TCP 分帧与心跳（`MessagePacket` / `NetworkManager`）

帧格式（`MessagePacket.cs`）：

```
Length(4B) + MainId(1B) + SubId(1B) + SeqId(2B) + Payload(N B)
```

- **Length 为含 8 字节包头的总长**，小端序（客户端用 `BitConverter`，未做网络字节序转换）；
  SeqId 同为小端序。服务端编解码必须按小端实现并以逐字节对拍测试锁死。
- SeqId：客户端请求分配非零 SeqId，服务端响应**原样回传**；服务端主动推送 SeqId=0。
- 客户端对迟到/重复响应（SeqId 对应的 pending 已不存在）直接丢弃，服务端无需兜底，
  但也**不得依赖**"响应一定被消费"。
- 入站有界队列：客户端消息队列溢出会主动断连自保。服务端推送必须有节制，
  不允许把客户端当无限缓冲。

心跳：

- 协议号固定 `MainId=MessageModule.System, SubId=1`，请求/响应同号，靠方向区分；
  **心跳帧 SeqId=0**（不走请求-响应配对），配对靠 payload 内业务自增序号与
  "一次发送只配对一次采样"的客户端约束。
- 心跳消息体**不是框架内建结构**：客户端经 `SetHeartbeatProvider(clientTimeMs, seq)` 注入
  请求工厂、经 `SetHeartbeatResponseParser(payload)->serverTimeMs` 注入响应解析器。
  心跳 proto 已冻结于仓库根 `proto/system.proto`：
  `GC2GS_001_001_HeartbeatRequest{ClientTime,SequenceId}` ↔
  `GS2GC_001_001_HeartbeatResponse{ServerTime,SequenceId}`，ServerBase 据此 codegen，
  回包必须携带**服务端 Unix 毫秒时间戳**并回显客户端序号。
- 客户端存活判定是"任意入站数据即存活"（默认间隔 30s，2.5×间隔未收到任何数据判超时断连）。
  服务端心跳回包除保活外真正的价值是校时：客户端按
  `ServerTime.AddSample(serverTimeMs, sentLocalMs, receivedLocalMs)` 估算偏移
  （offset = server + RTT/2 − 本地接收时刻；RTT>10s 的样本丢弃，只采信接近历史最优 RTT 的样本）。
  服务端时间戳必须取自可信时钟源（NTP 对时），这是全体客户端"服务器时间"的锚。

会话与重连语义：

- 客户端"传输层重连成功 ≠ 会话恢复"：重连后经注入的重鉴权钩子**重放登录握手**，
  成功前不补发应用请求。服务端必须提供"新连接凭 sessionToken 重绑会话"的握手路径
  （`proto/system.proto` 的 `GC2GS_001_002_SessionBind*`，见 §4 前置项），
  并区分"可重试失败"与"会话已过期"（后者客户端停止重连、引导重登）。
- 客户端支持按服务端下发的重连宽限窗口编排退避
  （`ConfigureReconnectWithinWindow`，窗口值约定来自配置表，如
  `battle_rule_general.ReconnectWindowSec`）。服务端会话保留时长与该窗口是同一约定，
  必须同源配置，不得两端各自硬编码。
- 错误码：TCP 响应实现 `IResponse.ResultCode`，非零触发客户端全局拦截器
  （示例语义：401 重登、429 限流提示）。ResultCode 域由 §5.2 错误码注册表管理。

### 3.2 Auth（`HttpAuthBackend` / `AuthManager` / `LoginFlow`）

- **单一 POST 端点**：URL 即 `AppConfig.AuthServerUrl` 本身，无路径/动词约定，
  游客登录、账号登录、令牌重绑共用一个端点，靠请求体 `mode` 与字段组合区分。
- 请求体：`{ "mode":"guest|account", "account", "password", "sessionToken", "deviceId" }`；
  响应体：`{ "success":bool, "userId", "sessionToken", "expiresAt", "errorCode", "errorMessage" }`。
  `expiresAt` 为令牌过期时刻（Unix 毫秒，**可选**：0/缺省 = 未提供，客户端不做过期预判，
  老服务端兼容）。客户端已消费（2026-07）：已知过期时跳过注定被拒的重绑往返——账号模式
  回登录界面、游客模式按 DeviceId 静默恢复同一身份；权威判定仍在服务端。
  令牌重绑（断线重连 / 冷启动恢复）时 `password` 为空、仅携带 `sessionToken`；
  游客身份锚定 `deviceId`（`SystemInfo.deviceUniqueIdentifier`）。
- HTTP 状态码语义：2xx 走响应体判定；**401/403 客户端视为凭据/令牌失效**（引导重登），
  其余非 2xx 视为网络问题（可重试）。服务端限流不要对登录端点回 401/403，应回 429。
- 冷启动顺序（`LoginFlow.RunAsync`）：先 `TryRestorePersistedSessionAsync`
  （即令牌重绑调用）→ 失败才走 `AutoGuestLogin` / 登录 UI。服务端若不支持令牌校验路径，
  每次冷启动都会退化成新游客，**阶段二必须含令牌重绑用例**。
- 该 JSON 契约是客户端"参考默认"（业务可整体替换 `IAuthBackend`）；ServerBase 阶段二
  按此契约原样实现，保证目标 1 的"零客户端改动"。

### 3.3 Analytics（`AnalyticsManager` / `AnalyticsJson` / `HttpJsonAnalyticsBackend`）

- 传输：把一批事件拼成 **JSON 数组** `POST` 到 `AnalyticsUrl`，
  `Content-Type: application/json`；2xx 即成功，其余客户端退避重试。
- 事件信封（固定字段在前）：
  `{ event_id, event, ts, session_id, device_id, user_id, app_version, channel, props{...} }`。
- **投递语义是 at-least-once**：客户端切后台落盘 + 启动补报，宁重复不丢失。
  `event_id` 是每条事件的唯一幂等键——**按 event_id 去重是服务端 ingest 的硬职责**，
  不是可选优化（草案"只透传入库"不成立，见 §10 差异清单）。
- `perf_window` 事件 props 字段（`PerfSampler`，服务端建表/查询口径以此为准）：
  `window / duration_s / frames / avg_fps / worst_ms / jank / severe_jank /
  managed_peak_mb / native_peak_mb / gc_count / scene / tier`。
  `tier` 为设备分级维度（`DeviceTierService.Tier` 字符串），验收要求可按其分组聚合。
  启动链路事件 `launch_run` / `launch_phase` 同走该管道（字段见客户端 ANALYTICS_GUIDE）。
- **签名头（客户端已实现，2026-07）**：已登录时批量 POST 附带
  `X-Telemetry-Ts`（Unix 毫秒）、`X-Telemetry-Uid`（userId）、`X-Telemetry-Sign`
  （小写 hex HMAC-SHA256，key = UTF8(sessionToken)，message = UTF8(`"{ts}\n"`) + body 字节）。
  服务端验证：按 Uid 查活跃会话令牌重算比对 + Ts 时间窗校验（防重放）；未签名流量
  **不拒收**、归入更严限流通道（登录前的启动埋点仍有价值）。算法已由客户端 golden
  测试锁死（`TelemetryRequestSignerTests`），改算法 = 改契约 = 过 §5 冻结点评审。

### 3.4 RemoteConfig（`RemoteConfigManager` / `HttpRemoteConfigBackend`）

- 传输：`GET RemoteConfigUrl`，客户端属性以查询参数附带（空值省略）：
  `device_id / user_id / app_version / channel / env`。响应为一份**扁平 JSON 对象**。
- **last-known-good 是客户端行为**：拉取成功即原样落盘，失败/解析失败保留现值，
  下次启动先用缓存。服务端不需要实现 LKG，需要的是**配置版本化与可回滚**
  （坏配置推下去后能一键回到上一版，语义对齐 Release Center 的"指针回切"）。
- **灰度也已在客户端**：功能开关条件对象
  `{ "enabled":bool, "rollout":0-100, "min_version":"x.y.z" }` 按设备稳定分桶判定，
  放量上调时已命中设备保持命中。因此服务端首期只需按 env/channel 版本化托管静态 JSON
  （CDN 文件即可满足，查询参数忽略）；按 device_id 服务端定向留二期（ADR-S004）。

### 3.5 Crash（`LocalFileCrashBackend` / `CrashReporter`）

- 传输：把本地积压崩溃记录 `POST` 到 `CrashReportUrl`，**body 为 JSON Lines**
  （逐行一条记录，非 JSON 数组——与 Analytics 的批量格式不同，服务端两个端点不得共用解析器）。
- 客户端默认后端只覆盖托管异常，Native 崩溃走厂商 SDK 自身管道；本端点定位为兜底，低优先。
- 上报同样携带 §3.3 的遥测签名头（两端点共用同一签名器与验签契约）。

### 3.6 `AppConfigAsset` 对接字段速查（联调时的填写面）

| 字段 | 用途 | 核实备注 |
| --- | --- | --- |
| `UseNetworkLogin` / `AutoGuestLogin` | 切真实登录链路 / 免 UI 游客直进 | 均存在 |
| `AuthServerUrl` | Auth 单端点（§3.2） | 存在；留空回退 Mock |
| `GameServerHost` / `GameServerPort` | TCP 长连接地址 | 草案写作 "Port"，实名 **GameServerPort**，默认 9000 |
| `UseTls` / `TlsServerName` / `TlsCertSha256Pins` | TLS 与证书 Pin 集合 | `TlsServerName` 默认 `clientbase-gs`（SNI+主机名校验），自签证书 CN/SAN 必须匹配；另有旧版单 Pin 字段 `TlsCertSha256` |
| `AllowPinnedCertificateWithoutSystemTrust` | 仅 dev：自签证书绕过系统信任链 | 生产构建强制失败，联调拓扑依赖它 |
| `NetworkTimeoutSeconds` | DNS/TCP/TLS 统一超时 | 存在 |
| `AnalyticsUrl` / `RemoteConfigUrl` / `CrashReportUrl` | 三个 HTTP 端点 | 均存在 |
| `LoginServerHost` / `LoginServerPort` | 独立登录服预留 | **当前未被框架消费**（HttpAuthBackend 只读 AuthServerUrl），联调表不填 |

## 4. 阶段分解（每阶段 = 一个可独立验收的切片）

**前置项（已落地，2026-07，客户端仓库）**：核查结果——心跳消息此前已是正式事实源
（`proto/system.proto` 的 `GC2GS/GS2GC_001_001_Heartbeat*`，经 `Tools/ProtoGen` 一键
双端生成，ServerBase 只需在 `protogen.json` 增加 Server target 即得生成物）；缺失的
另一半是 TCP 会话绑定/重绑握手，已补为 `GC2GS_001_002_SessionBindRequest` ↔
`GS2GC_001_002_SessionBindResponse`（含 `ProtocolVersion` 协商字段；响应带 `ResultCode`，
非零自动走客户端全局错误拦截）。客户端对握手消息的实际消费（TCP 版重鉴权钩子替换
现行 HTTP 令牌重绑）在模板切片随阶段四联调落地，届时不改框架代码，仅换注入实现。

| 阶段 | 内容 | 验收标准 |
| --- | --- | --- |
| 一 | 仓库骨架 + CI 门禁（§9）+ Protocol 生成物接入与帧级契约测试（服务端路由接口、Heartbeat/SessionBind 编解码往返、§3.1 帧格式逐字节对拍——纯逻辑不依赖网关，提前锁契约）+ 联调端口表 | CI 绿；空跑测试通过；往返/对拍契约测试进 CI；端口表落地为客户端 `AppConfigAsset(dev)` 填写依据 |
| 二 | Auth 端点（游客登录、token 签发/TTL/重绑、会话存储抽象、登录限流） | 客户端 `UseNetworkLogin=true` + `AutoGuestLogin` 走通 LoginFlow；**冷启动令牌重绑命中**（不产生新游客）；错误口令 5 次触发 429；全程无客户端改动 |
| 三 | TCP 网关 + 心跳（心跳回包带服务端 Unix ms、协议版本握手字段；帧对拍已在阶段一进 CI） | 客户端 `ServerTime.IsSynchronized==true`，PerfHud 显示真实 RTT；断线重连过 `Docs/NetworkDeviceAcceptance.md` 清单 |
| 四 | 消息路由与会话（dispatcher、单会话消息串行化、token 重绑握手、请求去重、模板切片消息） | 模板 Play 验收器（batchmode）对真服务端全绿；重连后重鉴权恢复会话、离线队列补发不产生重复副作用 |
| 五 | Analytics ingest（批量 JSON 数组、**event_id 幂等去重**、体积/速率限制、落盘/管道抽象） | `perf_window` / `launch_run` / `launch_phase` 端到端入库；人工重放同批次不产生重复行；`tier` 维度可分组查询 |
| 六 | RemoteConfig 托管（版本化 JSON、按 env/channel 目录、发布/回滚） | 客户端 LKG 缓存 + 条件开关灰度链路演练通过；坏配置回滚演练通过 |
| 七 | Crash ingest（JSON Lines 解析、原文落盘） | 客户端 LocalFileCrashBackend 积压上报打通（低优先，排期冲突时允许整体裁撤，见 §12） |
| 八 | TLS + 证书指纹联调（自签证书，CN/SAN=`TlsServerName`） | `TlsCertSha256Pins` 配置下握手成功 / 指纹不符拒连两个用例都过；双 Pin 轮换演练通过 |
| 九 | 可观测性收口（§7 三件套 + 健康检查 + 优雅停机 drain） | 一次客户端会话可在服务端日志按 `session_id` 串出完整轨迹；停机演练：在线客户端在宽限窗口内自动重连恢复会话 |
| 十 | 部署形态（自宿主 → IIS/服务托管，环境配置对齐 ReleaseProfiles 四环境） | dev/qa 两环境配置切换演练通过 |

阶段顺序 = 依赖顺序；二、三可并行。每阶段独立提交序列，验收不过不进下一阶段。
压测基线在阶段三验收后即建（§8），不等阶段九。

## 5. 契约冻结点（双端各自变更都必须过的门）

1. **帧格式**：§3.1 的字段布局、小端序、Length 含头语义一经对拍锁定不得改动。
2. **心跳 proto**：服务端时间戳与序号回显字段位置由共享 `proto/` 冻结（客户端校时依赖）。
3. **错误码域**：TCP `ResultCode`（int 域）与 Auth `errorCode`（字符串域）是**两套注册表**，
   分别成文管理映射；新增错误码走注册表提交，禁止散落魔法值。
4. **埋点 schema**：信封字段与 `perf_window` 等事件口径以客户端 GUIDE 为准，
   服务端按 `event_id` 去重后**原文入库不改写**；schema 演进只加不改。
5. **协议版本协商**：TCP 会话绑定握手（`GC2GS_001_002_SessionBindRequest`）已含
   `ProtocolVersion` 字段；服务端策略为
   "支持 N 与 N-1，更低版本明确拒绝并携带原因码"。首期实现为固定版本 + 拒绝路径可测。
   灰度期双版本并存依赖此约定，禁止用"猜字段"式兼容。
6. **契约测试**：`Server.Tests` 内(a)用客户端同款 proto 生成物做编解码往返 + 帧级字节对拍；
   (b)HTTP JSON 契约用双端共享的 golden 样例文件断言（Auth 请求/响应、埋点信封、
   perf_window props、Crash JSON Lines）。两组测试同为 CI 门禁；golden 文件与
   本文同源维护，改样例=改契约=过冻结点评审。

## 6. 安全基线（首期就做，不是"以后再说"）

1. **token 生命周期**：服务端定义 TTL + 滑动续期（每次登录/重绑成功刷新），token 采用
   带签名的不透明令牌，服务端可单方失效；签发时把过期时刻写入响应可选字段 `expiresAt`
   （契约已含、客户端已消费，见 §3.2）。refresh token 双令牌机制留待真实需求（ADR-S001）。
2. **凭据与敏感数据**：`password` 仅在 TLS 信道内传输（prod 强制 HTTPS/TLS）；
   服务端存储只留加盐慢哈希（Argon2/bcrypt）；日志脱敏清单进 CI 检查——
   `password`/`sessionToken` 全量禁止入日志，`device_id`/`user_id` 仅结构化字段不进消息文本。
3. **限流/防刷**：登录端点按 IP + deviceId 双桶限流（错误口令指数惩罚，回 429）；
   Analytics/Crash ingest 限单批体积、单事件体积、每设备速率，超限丢弃并计数——
   这两个端点是最先被刷的 DDoS/写放大面。已登录流量携带 §3.3 签名头可验身份，
   未签名流量走更严阈值；签名只区分通道，不是体积/速率限制的替代。
4. **重放防护**：HTTP 面依赖 TLS + token；TCP 登录握手带时间戳容差校验。
   业务请求级防重放依赖 §3.1 去重语义（ServerDeduplicated 请求的业务幂等键），
   不在传输层做逐包 nonce（收益/复杂度不成比例，见 ADR-S003）。
5. **secrets 管理**：对齐客户端惯例——仓库零 secret，密钥经 CI 密钥系统/环境注入；
   TLS 私钥、token 签名密钥支持不停机轮换（对应客户端 Pin 集合双证书轮换）。

## 7. 可靠性与可观测性最低标准

**可靠性**

- **优雅停机**：收到停机信号后 →(1)健康检查转 not-ready、停止 accept 新连接；
  (2)向在线会话推送服务端维护通知（预留协议）；(3)宽限期（≥客户端重连窗口）等待
  请求排空；(4)强制断开。客户端自动重连 + 会话重绑是兜底而非借口——会话状态先落
  持久化抽象再断开，重连回来的客户端必须能恢复。
- **幂等语义分级**（与客户端 `NetworkRequestConfig` 对齐）：ReadOnly 天然可重放；
  ServerDeduplicated 请求服务端必须按业务幂等键去重——客户端断线补发与重试
  以此为前提，**没有服务端去重，客户端的离线队列语义就是谎言**。
- **背压**：镜像客户端策略——每连接入站/出站有界队列，溢出断连；慢消费者
  （出站积压超阈值）主动断开，禁止无界缓冲拖垮进程。
- **ingest 不丢**：Analytics/Crash 先追加写本地 WAL 再回 2xx，管道消费异步化；
  宁可延迟入库，不可回了 2xx 又丢数据（客户端拿到 2xx 即删本地备份）。

**可观测性（三件套最低标准）**

- 结构化日志（JSON）：每条含 `trace_id`、连接 id、`session_id`（贯通客户端埋点信封的
  `session_id`）、`user_id`；HTTP 面接受客户端透传的会话头。
- 指标：四端点 + 网关的 RED（速率/错误/时延分布）、在线连接数、会话数、
  ingest 去重命中率、队列水位；Prometheus 文本格式起步。
- 追踪：首期不上全链路 APM，但 `trace_id` 字段与传播约定先落，避免日后翻打点。
- 健康检查：`/healthz`（存活）与 `/readyz`（可服务，优雅停机联动）分离。
- SLO 雏形（dev/qa 阶段先立口径，数值联调后校准）：登录成功率 ≥99.9%、
  登录 p99 ≤500ms、心跳回包 p99 ≤100ms（服务端处理耗时）、ingest 丢弃率 <0.1%。
  告警先按"SLO 口径 + 连接数/队列水位越限"两类立规则，接入渠道后启用。

## 8. 容量与压测基线

- **时间点**：阶段三验收后立即建立（网关是容量瓶颈主体，晚了每个后续阶段的回归都没锚）。
- **基线场景**：(1)空载心跳连接数爬坡（目标口径：单进程 ≥1 万连接稳态，心跳 p99 达标）；
  (2)回声消息吞吐（固定连接数下 QPS-延迟曲线）；(3)Analytics ingest 突发批量。
- **预算口径**：每连接内存上限、单进程目标并发、CPU 水位 70% 时的 QPS 记入基线报告，
  之后每阶段回归对比，劣化超 10% 视为门禁失败。
- 压测脚本进仓库（`Server.Tests` 旁的 `loadtest/`），与 CI 的 smoke 档（缩小规模）联动。

## 9. CI 门禁清单（对齐客户端已有门禁思路）

| 门禁 | 对齐客户端的对应物 |
| --- | --- |
| build + 全量单测 | `run-ci.ps1` |
| `dotnet format` 格式检查 | 客户端格式化纪律 |
| 项目引用白名单 | `check-asmdef-deps.ps1` |
| 契约测试（proto 往返 + 帧对拍 + HTTP golden） | 模板切片验收器思路 |
| banned API 扫描（如 `DateTime.Now`、裸 `Task.Run`、未脱敏日志接口） | `check-banned-apis.ps1` |
| workflow 一致性检查 | `check-workflows.ps1` |
| 压测 smoke 档（缩小规模跑通不测水位） | `release-rehearsal.ps1` 的演练思路 |

## 10. 草案与代码差异清单（本次核对结果，实施时以本节为准）

1. 帧格式草案未写明：**Length 含 8 字节包头、小端序**；SeqId 小端。已在 §3.1 冻结。
2. 草案"心跳回包字段位置一经冻结"：心跳消息体不在框架内，由仓库根
   `proto/system.proto` + 注入的工厂/解析器定义；冻结对象修正为**共享 proto 中的
   心跳消息**（§5.2）。
3. 心跳帧 **SeqId=0**，不走请求-响应配对；客户端存活判定是"任意入站数据"。
4. Auth 是**单一 POST 端点（URL 即 AuthServerUrl）**，无路径约定；401/403 有特殊语义
   （凭据失效），服务端限流必须用 429。草案未涉及。
5. 冷启动先走令牌重绑再游客登录——阶段二验收补"重绑命中不产生新游客"。
6. 埋点信封含 **event_id 幂等键**，管道 at-least-once；"服务端只透传入库"修正为
   "**按 event_id 去重后**原文入库"（§3.3、阶段五验收）。
7. `perf_window` 字段口径已展开为具体字段清单（§3.3），tier 为字符串维度。
8. RemoteConfig 灰度已在**客户端**按设备稳定分桶（rollout/min_version 条件对象）；
   草案"灰度按会话 hash"与实现不符且首期不需要——服务端只做版本化静态 JSON + 回滚
   （§3.4、ADR-S004）。last-known-good 同为客户端行为，服务端无此职责。
9. Crash 上报 body 是 **JSON Lines**，非 JSON 数组（§3.5）。
10. `AppConfigAsset` 无 `Port` 字段，实名 `GameServerPort`；`LoginServerHost/Port`
    当前未被框架消费；TLS 相关另有 `TlsServerName`（自签证书 CN/SAN 必须匹配）与
    `AllowPinnedCertificateWithoutSystemTrust`（联调拓扑依赖）。
11. 客户端入站队列有界、溢出断连；服务端推送策略与背压设计（§7）由此约束，草案未涉及。
12. 客户端离线队列补发依赖服务端请求去重（ServerDeduplicated 级别），草案未把
    "服务端幂等"列为阶段四职责，已补（§7、阶段四验收）。

## 11. 本地联调拓扑

单机全链路：IIS（静态热更，`current.json` 指针 no-cache、版本目录长缓存、补
`.bundle/.bytes/.sig` MIME 映射）+ ServerBase 自宿主（TCP `GameServerPort=9000`、
HTTP 端点若干）。多 CDN 回退用双 IIS 站点模拟。TLS 联调用自签证书：CN/SAN 填
`TlsServerName`（默认 `clientbase-gs`），客户端开
`AllowPinnedCertificateWithoutSystemTrust` + 配 `TlsCertSha256Pins`。
端口表与各端点 URL 在阶段一落成一张表，作为客户端 `AppConfigAsset(dev)` 的填写依据
（`LoginServerHost/Port` 不填，见 §3.6）。

## 12. 风险与开放问题

- **token 过期字段（原契约债，客户端侧已还清 2026-07-18）**：Auth 响应已加可选
  `expiresAt`（缺省 0 = 未提供，老服务端兼容），客户端过期预判与跳过逻辑已落地（§3.2）；
  剩服务端阶段二按 TTL 签发即可闭环。
- **阶段七价值存疑**：LocalFileCrashBackend 只覆盖托管异常，正式项目基本必接厂商
  Crash SDK，本端点长期价值有限。排期冲突时优先保 §8 压测基线，阶段七可整体延后或裁撤。
- **Auth 单端点的演进空间**：mode 复用一个 URL 在加验证码/第三方登录时会膨胀；
  首期不动（零客户端改动优先），扩展时以新增端点方式演进，旧端点语义冻结。
- **deviceId 作为游客身份锚**：`SystemInfo.deviceUniqueIdentifier` 在部分平台会漂移/重装重置，
  游客数据丢失风险由上层"游客转正/绑定"策略兜，ServerBase 只保证 token 语义正确。
- 持久化抽象的边界（哪些状态允许进程内、哪些必须落盘）在阶段四前出 ADR（ADR-S005 先立骨架）。
- 水平扩展：首期单进程，但会话路由留分区键位置，避免未来推倒。
- 帧同步/房间语义是否进入 ServerBase 主干：默认不进，若做则以扩展包形态
  （对齐客户端扩展包惯例）。
- **埋点/崩溃端点签名头（原契约债，客户端侧已还清 2026-07-18）**：两端点上报已按
  §3.3 契约携带签名头（凭据 = 当前会话令牌，未登录不签名）；剩服务端阶段五/七实现
  验签与未签名通道从严限流。§6.3 的体积/速率限制仍不可省。

## 附录 A：ADR 条目雏形（服务端仓库建立后正式编号入库）

| 编号 | 决策 | 理由与代价 |
| --- | --- | --- |
| ADR-S001 | Auth 沿用客户端参考 JSON 契约与单端点形态；token 生命周期为服务端 TTL+滑动续期，过期时刻经**可选** `expiresAt` 字段暴露（缺省兼容，2026-07 已进契约） | 目标 1"零客户端改动"与过期可预判兼得；可选字段不破坏新旧任意组合；refresh 双令牌留待真实需求 |
| ADR-S002 | 帧格式沿用"小端 + Length 含头"，不改网络字节序 | 与线上客户端逐字节兼容压倒教科书惯例；以对拍测试锁死防两端漂移 |
| ADR-S003 | 埋点 at-least-once + 服务端按 event_id 去重（有限去重窗口，如 7 天）；不做传输层逐包 nonce 防重放 | 客户端管道已按"宁重复不丢失"设计，去重是数据正确性的唯一闭环；逐包 nonce 复杂度/收益不成比例 |
| ADR-S004 | RemoteConfig 首期为版本化静态 JSON（按 env/channel 目录 + 可回滚），不建动态定向服务 | 灰度分桶已在客户端实现；服务端定向属于重复建设，等出现"按用户属性定向"的真实需求再立项 |
| ADR-S005 | 会话状态首期进程内 + 持久化抽象双写关键字段（token 绑定、宽限窗口内可恢复），分区键字段从第一天预留 | 单进程起步不做分布式会话，但优雅停机与未来水平扩展都依赖"会话可外置"的接口形状 |
| ADR-S006 | 遥测签名密钥选用**会话令牌**（HMAC-SHA256 + 时间戳窗），不内嵌上传密钥（2026-07 客户端已落地） | 客户端资产禁 Secret 红线；内嵌对称密钥可被提取属伪安全；令牌服务端可查可失效，签名与会话生命周期天然对齐；代价是登录前流量未签名，靠通道分级限流兜 |
