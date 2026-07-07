# 埋点管道使用指南

## 定位

业务只管 `Track`，管道负责其余一切：公共维度封装（会话/设备/用户/版本/渠道）、
内存缓冲、批量上报、失败退避重试、切后台与退出时落盘防丢、下次启动补报。

三方平台（ThinkingData / Firebase 等）对接放扩展包：实现 `IAnalyticsBackend`
并 `SetBackend` 注入；框架主干不含厂商 SDK。

## 业务侧用法

```csharp
// 记事件（属性只支持扁平键值：string / bool / 整数 / 浮点）
GameEntry.Analytics.Track("stage_enter", new Dictionary<string, object>
{
    { "stage", "main_home" },
    { "from", "login" }
});

// 登录成功后绑定用户维度（登出传空）
GameEntry.Analytics.SetUserId(loginResult.UserId);

// 关键节点强制冲刷（如支付完成）
await GameEntry.Analytics.FlushAsync();
```

事件信封（管道自动封装）：

```json
{ "event_id":"a1b2c3…", "event":"stage_enter", "ts":1720000000000,
  "session_id":"…", "device_id":"…", "user_id":"10001",
  "app_version":"1.0", "channel":"taptap",
  "props": { "stage":"main_home", "from":"login" } }
```

`event_id` 是每条事件的唯一幂等键。管道做 **at-least-once** 投递（切后台落盘 +
启动补报，宁重复不丢失），因此**采集端必须按 `event_id` 去重**才能得到精确计数——
客户端无法单方面保证 exactly-once（"发到一半进程被杀"的窄窗口重复不可避免）。

## 后端选择

| 场景 | 做法 |
|---|---|
| 开发期 | 什么都不配——默认日志后端，Console 直接看事件 JSON |
| 自建采集 | `AppConfig.AnalyticsUrl` 填端点，走内置 HTTP JSON 后端（POST 事件数组） |
| 三方平台 | 扩展包实现 `IAnalyticsBackend`，组合根 `GameEntry.Analytics.SetBackend(...)` |

## 管道参数（内置约定）

- 内存队列上限 500 条，溢出丢最旧并以 `analytics_dropped` 事件补报丢弃数；
- 单批 ≤50 条；每 15s 定时冲刷，队列达 50 立即冲刷；
- **排水式冲刷**：一次触发连发多批直到队列空（单次上限 20 批），空闲期积压不必等下个周期慢慢发；
- 失败退避：15s × 连续失败次数，上限 120s；
- 切后台 / 退出：先落盘 `analytics_pending.jsonl`（≤512KB）再尽力发一批，
  下次启动自动回捞补报；队列排空后删除落盘快照，缩小重启重复窗口，
  残余重复（发到一半被杀）靠采集端按 `event_id` 去重兜底。

## 框架内置事件

| 事件 | 触发 | 关键属性 |
|---|---|---|
| `launch_run` | 启动流程收口 | success / end_reason / total_ms / phase_count |
| `launch_phase` | 每个启动阶段 | phase / success / duration_ms / detail |
| `analytics_dropped` | 队列曾溢出 | count |

业务事件命名建议 `snake_case`，模块前缀（如 `shop_open`），在项目内维护事件字典。
