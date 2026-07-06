# NetworkManager 使用指南

## 概述

`NetworkManager` 是客户端网络通信的统一入口，整合 `TcpClient`、`MessageDispatcher` 和 `NetworkRequestTracker`。

主要能力：
- **请求-响应**：`RequestAsync` — SeqId 精确匹配，自动超时、自动转圈 UI
- **通知发送**：`Notify` — 无需等回包的单向消息
- **推送监听**：`Subscribe` — 长生命周期的服务端推送订阅
- **心跳 + 重连**：自动心跳、指数退避重连、回前台检测

## 消息包格式

```
Length(4 字节) + MainId(1) + SubId(1) + SeqId(2) + Payload(N)
```

- `SeqId > 0`：请求-响应包，服务端原样回传
- `SeqId = 0`：通知 / 推送包

## 核心 API

### 1. RequestAsync — 请求-响应（最常用）

发送请求并等待匹配响应。框架自动管理 SeqId 分配、超时、转圈 UI。
如果响应实现 `IResponse` 且 `ResultCode > 0`，会先经过全局错误码拦截器；拦截器返回 `true` 时，本次请求返回 `null`。

```csharp
// 方式一：请求类实现了 IRequest<TResp>，零泛型参数
var request = new PlayerInfoRequest { PlayerId = 123 };
var response = await GameEntry.Network.RequestAsync(request);

// 方式二：显式指定泛型（未实现 IRequest<T> 时）
var response = await GameEntry.Network.RequestAsync<
    PlayerInfoRequest,
    PlayerInfoResponse>(request);

if (response == null)
{
    // 超时 — 框架已自动弹提示并隐藏转圈
    return;
}

// 正常处理 response...
```

#### 请求配置

```csharp
// 使用默认配置：1秒后转圈、15秒超时、超时弹提示
await GameEntry.Network.RequestAsync(request);

// 静默请求：不转圈、不弹提示
await GameEntry.Network.RequestAsync(request, NetworkRequestConfig.Silent);

// 自定义配置
await GameEntry.Network.RequestAsync(request, new NetworkRequestConfig
{
    TimeoutMs = 10000,          // 10秒超时
    ShowLoadingDelayMs = 500,   // 0.5秒后转圈
    ShowTimeoutTip = true,      // 超时弹提示
    TimeoutMessage = "登录超时", // 自定义提示文案
});
```

### 2. Notify — 通知/单向消息

发送后不等回包。适用于投降、聊天、操作确认等场景。

```csharp
GameEntry.Network.Notify(new SurrenderRequest());
```

### 3. Subscribe — 推送监听

服务端主动推送的消息（回合切换、聊天、好友上线等）用 Subscribe 长期监听。

```csharp
// 返回订阅句柄，用于精确释放
MessageSubscription sub = GameEntry.Network.Subscribe<TurnChangedPush>(OnTurnChanged);

// 不再需要时释放
sub.Unsubscribe();
```

#### UI 窗口中推荐用 ListenMessage（自动释放）

```csharp
public class BattleWindow : UIBase<BattleView>
{
    protected override void OnOpen(object userData)
    {
        base.OnOpen(userData);
        // 窗口关闭时自动注销，不需要手动 Unsubscribe
        ListenMessage<TurnChangedPush>(OnTurnChanged);
    }
}
```

## 事件

| 事件 | 触发时机 | 典型用途 |
|------|----------|----------|
| `OnConnected` | 连接/重连成功 | 刷新在线状态 |
| `OnDisconnected` | 连接断开 | 清理对局状态 |
| `OnReconnecting(attempt, max, waitSec)` | 每次重连尝试 | ReconnectPanel 显示 |
| `OnReconnectSucceeded` | 重连成功 | ReconnectPanel 隐藏 |
| `OnReconnectFailed` | 重连放弃 | 提示用户手动重连或退出 |
| `OnWaitingStart` | 请求等待超过延迟 | NetworkWaitingUI 转圈 |
| `OnWaitingEnd` | 所有请求完成 | NetworkWaitingUI 隐藏 |
| `OnRequestTimeout(msg)` | 请求超时 | Toast/弹窗提示 |
| `OnError(msg)` | 网络层错误 | 日志/UI 提示 |

## 全局错误码拦截

在框架层统一处理通用错误码（登录过期、频率限制等），避免业务层到处判断。
拦截发生在业务订阅和 `RequestAsync` 恢复之前，只处理实现 `IResponse` 且 `ResultCode > 0` 的协议：
协议类型会在调用 `RequestAsync<TReq, TResp>` 或 `Subscribe<T>` 时登记到 `NetworkManager` 的类型注册表。

```csharp
GameEntry.Network.SetGlobalErrorInterceptor(code =>
{
    if (code == 401) { HandleTokenExpired(); return true; }
    if (code == 429) { ShowRateLimitTip(); return true; }
    return false; // 不处理，继续下发给业务
});
```

## 协议类型约定

| 接口 | 用途 | 示例 |
|------|------|------|
| `INetMessage` | 所有协议消息基接口（继承 `Google.Protobuf.IMessage`） | 响应消息、推送消息 |
| `IResponse` | 带 `ResultCode` 的响应消息 | `GS2GC_003_001_PlayerInfoResponse : IResponse` |
| `IRequest<TResp>` | 请求消息，声明对应响应类型 | `GC2GS_003_001_PlayerInfoRequest : IRequest<GS2GC_003_001_PlayerInfoResponse>` |

`IRequest<TResp>` 和 `IResponse` 由代码生成器自动生成（客户端目标）。请求与响应按约定匹配：同 MainId + SubId 的上行请求与下行响应。

## 服务器校时（ServerTime）

心跳响应携带服务端时间戳时，注入解析器即可开启自动校时（RTT/2 补偿 + 劣化样本过滤）：

```csharp
// 组合根启动时注入（与 SetHeartbeatProvider 对称）
GameEntry.Network.SetHeartbeatProvider((clientTime, seq) =>
    new GC2GS_001_001_HeartbeatRequest { ClientTime = clientTime, SequenceId = seq });
GameEntry.Network.SetHeartbeatResponseParser(payload =>
    ProtobufUtil.Deserialize<GS2GC_001_001_HeartbeatResponse>(payload).ServerTime);

// 之后任意处读取服务端时间（倒计时、每日重置等一律用它，不用本地时间）
long nowMs = ServerTime.NowMs;          // 未同步时回退本地 UTC
bool synced = ServerTime.IsSynchronized;
```

断线不清除偏移；切换服务器/环境时调用 `ServerTime.Reset()`。

## 配置

```csharp
GameEntry.Network.SetHeartbeatInterval(30f);
GameEntry.Network.EnableHeartbeat(true);
GameEntry.Network.EnableAutoReconnect(true);
GameEntry.Network.SetMaxReconnectAttempts(5);
GameEntry.Network.SetReconnectIntervals(new float[] { 1, 2, 5, 10, 30 });
```

## UI 组件

| 组件 | 位置 | 职责 |
|------|------|------|
| `NetworkWaitingUI` | Canvas_System | 请求转圈，订阅 OnWaitingStart/End |
| `ReconnectPanel` | Canvas_System | 断线重连，订阅 OnReconnecting/Succeeded/Failed |

两者独立运行，NetworkManager 不知道它们的存在（解耦）。

## 相关文档

- [MessageDispatcher 使用指南](MESSAGE_DISPATCHER_GUIDE.md)
- [TcpClient 使用指南](TCP_CLIENT_GUIDE.md)
- [消息 ID 设计文档](MESSAGE_ID_DESIGN.md)
- [Protobuf 设置指南](PROTOBUF_SETUP.md)
