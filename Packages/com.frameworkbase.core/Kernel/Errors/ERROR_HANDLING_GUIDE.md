# 协议错误码字典与统一错误处理

## 定位

服务端返回码的处理从"各业务手写 `if (code == …)`"收敛为**表驱动字典**：
业务收到返回码只调一行，查字典 → 执行统一反应（Toast/弹窗/登出广播）→ 限流埋点。

```csharp
// 业务调用点（收到任何服务端响应后）
if (resp.Code != 0)
{
    ErrorCenter.Shared.Handle(resp.Code, resp.Message);
    return;
}
```

## 错误码分段约定

| 段 | 归属 | 说明 |
|---|---|---|
| `0` | — | 成功，Handle 直接跳过 |
| 负数 | 客户端本地 | 框架合成（`ClientErrorCodes`：-1 超时 / -2 断连 / -3 解析失败），服务器永不下发负数，两侧空间天然不冲突 |
| `1 ~ 999` | 框架保留 | 通用协议层错误（会话失效、限流、维护等），与服务端约定后注册 |
| `≥ 1000` | 业务 | 按模块分段（如商店 2000~2999、对局 3000~3999），在项目内维护码表文档 |

## 注册（组合根启动时一次性完成）

```csharp
var reg = ErrorCodeRegistry.Shared;

// 模块段兜底：整段先给通用规则，新增码天然有提示，不至于静默无反应
reg.RegisterRange(2000, 2999, ErrorReaction.Toast);          // 商店段默认 Toast + 服务端 message

// 个别码精确覆写（精确 > 窄区间 > 宽区间 > 默认规则）
reg.Register(2001, ErrorReaction.Popup, "shop_sold_out");    // 文案键走本地化
reg.Register(101, ErrorReaction.ForceLogout, "session_expired");
reg.Register(102, ErrorReaction.Maintenance, "server_maintenance");

// 本地化接入（可选；未注入时键原样显示，键可直接写中文）
reg.SetLocalizer(key => GameEntry.RefData.GetText(key));
```

文案回退链：`MessageKey` 经 localizer → 原样 key → 服务端随包 message → 默认文案。

## 反应类型与默认行为

| Reaction | 默认呈现器行为 | 适用 |
|---|---|---|
| Silent | 只记日志 | 幂等重复提交等无需打扰玩家的错误 |
| Toast | TipManager 轻提示（Warning 样式） | 可自愈普通失败：余额不足、冷却中 |
| Popup / PopupRetry | **降级 Error 样式 Toast** + 日志提醒 | 需要玩家确认/可重试的失败 |
| ForceLogout | Toast + 广播 `GameMessage.ServerForceLogout` | 会话失效/顶号/封禁 |
| Maintenance | Toast + 广播 `GameMessage.ServerMaintenance` | 停服维护 |

框架不内置通用模态弹窗；业务接入自己的弹窗/维护页系统后**替换呈现器**：

```csharp
ErrorCenter.Shared.SetPresenter(new MyGameErrorPresenter()); // 实现 IErrorPresenter
GameEntry.Event.Subscribe<int>(GameMessage.ServerForceLogout, code => BackToLogin());
GameEntry.Event.Subscribe<int>(GameMessage.ServerMaintenance, code => ShowMaintenancePage());
```

## 埋点

每次 Handle 上报 `server_error` 事件（code / reaction），**同码 60s 限流**——
服务端批量报错时不刷爆埋点管道。错误码分布是服务端异常的一手监控信号。

## 与 Auth 弹窗策略的关系

登录流程的 `AuthPopupPolicy`（重试/退出双按钮状态机弹窗）是登录态专用链路，保持独立；
ErrorCenter 面向**登录后的常规业务协议**。两者不重叠。
