# MessageDispatcher 使用指南

## 概述

`MessageDispatcher` 负责把网络线程收到的协议消息切回 Unity 主线程，并按协议号分发给所有订阅者。

它是 `NetworkManager` 的内部组件，业务代码通常不直接接触 `MessageDispatcher`，而是通过 `NetworkManager` 的 API：

- **请求-响应** → `NetworkManager.RequestAsync`（SeqId 匹配，自动等待与超时）
- **推送监听** → `NetworkManager.Subscribe`（底层调用 MessageDispatcher）

## 分发顺序

收到响应包时的处理顺序：

1. 如果消息实现 `IResponse` 且 `ResultCode > 0`，先交给 `NetworkManager` 的全局错误码拦截器。
2. 拦截器返回 `true` 时，本条消息被消费，不再进入业务订阅，也不会让 `RequestAsync` 返回正常响应。
3. 未被拦截时，先进入 `MessageDispatcher` 的多播订阅分发。
4. 如果 `SeqId > 0` 且存在 pending 请求，最后恢复对应的 `RequestAsync`。

## 核心接口

- `Subscribe<T>(handler, priority)`：订阅类型化消息，协议号从 `T : IMessage` 中读取，内部自动反序列化。
- `MessageSubscription.Unsubscribe()`：释放当前订阅。
- `ClearAllHandlers()`：框架关闭时清除全部订阅。
- `EnqueueMessage(mainId, subId, payload)`：从网络线程加入主线程队列。
- `ProcessMessageQueue()`：在主线程处理队列消息。

## 使用示例

```csharp
// 业务代码通过 NetworkManager 的 Subscribe 间接使用：
var sub = GameEntry.Network.Subscribe<TurnChangedPush>(OnTurnChanged);

// 不再需要时释放
sub.Unsubscribe();
```

## 设计约束

- 不提供按协议号整体覆盖或整体注销的注册接口，避免一个组件误删其他组件的监听。
- 分发时会复制订阅快照，允许回调过程中新增或释放订阅。
- 单个订阅者抛异常只会记录错误，不会阻断其他订阅者。
- `priority` 值越大越先执行，同优先级按订阅顺序执行（内部用递增 Id 保证稳定）。
