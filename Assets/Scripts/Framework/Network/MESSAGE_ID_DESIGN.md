# 消息ID设计说明

## 设计概述

消息ID采用**主ID（MainId）+ 子ID（SubId）**的两级结构，而不是单一的消息ID。

## 消息协议格式

```
┌──────────┬──────────┬──────────┬──────────┬──────────────┐
│  Length  │  MainId  │  SubId   │ Reserved │   Payload    │
│ (4 bytes)│(1 byte)  │(1 byte)  │(2 bytes) │  (N bytes)   │
└──────────┴──────────┴──────────┴──────────┴──────────────┘
```

### 字段说明

- **Length** (4字节): 消息总长度，包含头部
- **MainId** (1字节): 主消息ID，表示功能模块（0-255）
- **SubId** (1字节): 子消息ID，表示该模块下的具体消息类型（0-255）
- **Reserved** (2字节): 保留字段，用于未来扩展
- **Payload** (N字节): Protobuf序列化的消息体

## 设计优势

### 1. 更好的消息组织
- 按功能模块分类，结构清晰
- 每个模块独立管理自己的消息类型
- 便于团队协作开发

### 2. 灵活的扩展性
- 支持最多256个功能模块
- 每个模块支持最多256种消息类型
- 总共可支持 256 × 256 = 65,536 种消息

### 3. 便于消息路由
- 可以根据主ID快速定位到对应的模块处理器
- 模块内部再根据子ID分发到具体的消息处理器
- 支持模块级别的消息拦截和过滤

### 4. 易于维护
- 新增模块不影响现有模块
- 模块内消息ID独立编号，避免冲突
- 便于代码组织和文档管理

## 消息模块定义

推荐在 `MessageModule` 类中定义所有模块ID：

```csharp
public static class MessageModule
{
    public const byte System = 1;      // 系统模块（心跳、ping等）
    public const byte Login = 2;       // 登录认证模块
    public const byte Player = 3;      // 玩家数据模块
    public const byte Battle = 4;      // 战斗模块
    public const byte Social = 5;      // 社交模块
    public const byte Shop = 6;        // 商店模块
    public const byte Chat = 7;        // 聊天模块
    public const byte Guild = 8;       // 公会模块
    public const byte Mail = 9;        // 邮件模块
    public const byte Task = 10;       // 任务模块
    // ... 可扩展到255
}
```

## 消息定义示例

协议消息**不手写 C#**：在仓库根 `proto/` 写 proto3 源，主/子号直接编码进**消息名**（`<方向>_<主号3位>_<子号3位>_<名称>`），
双击 `gen-proto.bat` 由 protoc 生成消息类，生成器再补路由伴生 partial（`GetMainId/GetSubId` + `IRequest/IResponse`）。

```proto
syntax = "proto3";
option csharp_namespace = "Game.Protocol";

// 主ID=2(登录模块)，子ID=1；请求与其响应同主+子号双向配对
message GC2GS_002_001_LoginRequest {
  string Username = 1;
  string Password = 2;
}
message GS2GC_002_001_LoginResponse {
  int32 ResultCode = 1;   // 含 ResultCode → 生成为 IResponse
  string Token = 2;
}
```

生成后（**勿手改**）：`GC2GS_002_001_LoginRequest` 自动声明为 `IRequest<GS2GC_002_001_LoginResponse>`，
`GetMainId()=>2`、`GetSubId()=>1`；响应 `GS2GC_002_001_LoginResponse` 声明为 `IResponse`。

> 主/子号写进消息名（而非常量类）后，编号即协议名的一部分：改号=改类型名，配对由生成器保证，避免手工常量漂移。
> 号段与配对规约见下文「子ID分配原则」。

## 消息发送

> 实际业务优先用高层 API：`await GameEntry.Network.RequestAsync(request)`（请求-响应）/ `GameEntry.Network.Subscribe<T>(...)`（推送）。
> 下面展示其底层用到的 `MessagePacket` 原语，便于理解协议头。

### 方式1：使用主ID和子ID

```csharp
var message = new GC2GS_002_001_LoginRequest { Username = "player", Password = "pass" };
byte[] payload = ProtobufUtil.Serialize(message);
byte[] packet = MessagePacket.Pack(
    2,   // 主ID（登录模块）
    1,   // 子ID（登录请求）
    payload
);
client.Send(packet);
```

### 方式2：使用消息对象（推荐）

```csharp
var message = new GC2GS_002_001_LoginRequest { Username = "player", Password = "pass" };
byte[] payload = ProtobufUtil.Serialize(message);
byte[] packet = MessagePacket.Pack(message, payload);  // 从 INetMessage 自动取主ID和子ID
client.Send(packet);
```

## 消息接收和解析

### 基础解析

```csharp
private void OnReceive(byte[] packet)
{
    if (!MessagePacket.Unpack(packet, out byte mainId, out byte subId, out byte[] payload))
    {
        GameLog.Error("消息包解析失败");
        return;
    }
    
    GameLog.Debug($"收到消息: 主ID={mainId}, 子ID={subId}");
    
    // 根据主ID和子ID处理消息
    HandleMessage(mainId, subId, payload);
}
```

### 模块化处理

```csharp
private void HandleMessage(byte mainId, byte subId, byte[] payload)
{
    switch (mainId)
    {
        case MessageModule.Login:
            HandleLoginMessage(subId, payload);
            break;
            
        case MessageModule.Player:
            HandlePlayerMessage(subId, payload);
            break;
            
        case MessageModule.Battle:
            HandleBattleMessage(subId, payload);
            break;
            
        default:
            GameLog.Warning($"未处理的模块ID: {mainId}");
            break;
    }
}

private void HandleLoginMessage(byte subId, byte[] payload)
{
    switch (subId)
    {
        case 1: // 登录响应
            var response = ProtobufUtil.Deserialize<GS2GC_002_001_LoginResponse>(payload);
            OnLoginResponse(response);
            break;
            
        case 3: // 登出响应
            // 处理登出响应
            break;
            
        default:
            GameLog.Warning($"未处理的登录消息: {subId}");
            break;
    }
}
```

## MessagePacket工具方法

### 打包方法

```csharp
// 使用主ID和子ID打包
byte[] Pack(byte mainId, byte subId, byte[] payload)

// 使用消息对象打包（推荐）
byte[] Pack(INetMessage message, byte[] payload)
```

### 解包方法

```csharp
// 完整解包
bool Unpack(byte[] packet, out byte mainId, out byte subId, out byte[] payload)
```

### 辅助方法

```csharp
// 获取主ID
byte GetMainId(byte[] packet)

// 获取子ID
byte GetSubId(byte[] packet)

// 获取完整消息ID（主ID和子ID组合为ushort）
ushort GetMessageId(byte[] packet)

// 组合主ID和子ID为完整消息ID
ushort CombineMessageId(byte mainId, byte subId)

// 拆分完整消息ID为主ID和子ID
void SplitMessageId(ushort messageId, out byte mainId, out byte subId)

// 获取消息长度
int GetMessageLength(byte[] packet)

// 验证消息包有效性
bool IsValid(byte[] packet)
```

## 消息ID分配建议

### 主ID分配原则

1. **系统级消息** (1-10): 心跳、ping、时间同步等
2. **账号相关** (11-20): 登录、注册、账号管理
3. **玩家数据** (21-50): 玩家信息、背包、装备等
4. **游戏玩法** (51-100): 战斗、副本、任务等
5. **社交功能** (101-150): 好友、公会、聊天等
6. **商业功能** (151-200): 商店、充值、交易等
7. **预留扩展** (201-255): 未来功能

### 子ID分配原则

子ID 标识**一个业务主题**，按方向前缀（`GC2GS` 客户端→服务端 / `GS2GC` 服务端→客户端）区分收发方。规约如下：

1. **请求与其响应 同号双向**：主动请求 `GC2GS_005_00X_Xxx` 的回包是 `GS2GC_005_00X_XxxResp`，二者共用同一子ID（号段 **1-99**）。
   - **每个主动请求都必须有且仅有一个对应回包**（哪怕只带一个 `ResultCode` 受理确认）。
   - 协议生成器据此自动把请求声明为 `IRequest<同号的 GS2GC 响应>`，子ID 编错会导致请求配对到错误的响应类型。
2. **纯服务端推送**（无对应请求，服务端主动广播/单发）号段 **从 100 起**（100, 101, 102...）。一个请求可触发零或多个推送。

> 不再使用"请求=奇数、响应=偶数"的旧约定——请求与响应天然分处两个方向、各有独立路由表，用同号双向比奇偶更直观，也能让生成器正确配对。

示例：
```
主ID=5 (对战模块)
  GC2GS 子ID=2: SurrenderRequest (请求)
  GS2GC 子ID=2: SurrenderResp    (回包，同号)
  GS2GC 子ID=100: TurnChanged    (推送)
  GS2GC 子ID=102: GameEnded      (推送)
```

### 服务端硬约束：回包必须 handler 同步返回（否则丢 SeqId → 客户端超时）

请求与响应靠**包头 SeqId** 精确配对：客户端 `RequestAsync` 发请求时分配一个 SeqId，只认相同 SeqId 的回包来兑现 await。服务端 `MessageRouter.Dispatch` **只在 handler 同步返回响应包时**才把请求 SeqId 回填进回包（`ApplySeqId(reply, seqId)`）。因此：

1. **1-99 号段的回包，handler 必须 `return MessagePacket.Pack(resp, ...)` 同步返回**，由路由统一回填 SeqId。
2. **禁止**把 1-99 回包改走 Service 内部出站端口（`SendMessage`/`Broadcast`）异步发送——那条路径不知道请求 SeqId，回包会以 **SeqId=0** 发出，客户端当成推送（落到 `DispatchMessage` 触发"未注册的消息"告警），原 `RequestAsync` 永远等不到对应 SeqId 而**超时转圈**。
3. 出站端口（SeqId=0）**只用于 100+ 推送**（广播给房间其他人、主动单发等）。一次请求处理里"给请求者的回包同步返回 + 给其他人的状态变更走推送"二者并存是正常的（如 `JoinRoom` 同步回 `RoomJoined`、同时 `BroadcastRoomUpdate` 给房内其余人）。
4. **失败路径同样要回包**：未登录/校验失败等分支不能 `return null`，否则客户端同样卡死；应回带失败 `ResultCode` 的同号响应。

> 反例（已修复）：建房/加入房曾让 handler `return null`、由 `RoomService` 异步发 `RoomCreated`/`RoomJoined`，回包 SeqId=0 导致界面一直转圈直至超时。正例参见同模块其余 `Handle*`（`HandleLeaveRoom`/`HandleStartRoom` 等）与 `HandleQueryActiveBattle` 的同步返回写法。

> **已登记的唯一例外——`GC2GS_005_001_PlacePiece`（落子）**：其结果 `GS2GC_005_001_PlaceResult` 由 `BattleRoom` 在房间锁内裁定后**广播给全部座位**（含请求者本人，SeqId=0），handler 成功路径 `return null` 避免向请求者双发；BaseSeq 失配时则直接回 `GS2GC_005_003_StateSnapshot`（异号包）令客户端纠偏。因此**客户端对落子必须走 fire-and-forget `Send` + 订阅 `PlaceResult` 推送（现行 `NetworkBattleGateway.SubmitMove` 即如此），严禁对该请求调用 `RequestAsync`**——同 SeqId 回包永远不会来，必然超时。新增协议不得效仿此模式，除非同样具备"结果天然广播给包括请求者在内的多方"的语义并在此登记。

## 与MessageDispatcher集成

消息分发器可以利用主ID和子ID实现两级路由：

```csharp
public class MessageDispatcher
{
    // 模块处理器字典
    private Dictionary<byte, IModuleHandler> _moduleHandlers;
    
    public void DispatchMessage(byte mainId, byte subId, byte[] payload)
    {
        // 根据主ID找到模块处理器
        if (_moduleHandlers.TryGetValue(mainId, out var handler))
        {
            // 模块处理器根据子ID分发消息
            handler.HandleMessage(subId, payload);
        }
        else
        {
            GameLog.Warning($"未注册的模块: {mainId}");
        }
    }
}
```

## 兼容性说明

如果需要与旧系统兼容（使用单一ushort消息ID），可以使用以下方法：

```csharp
// 将主ID和子ID组合为ushort
ushort fullMsgId = MessagePacket.CombineMessageId(mainId, subId);

// 将ushort拆分为主ID和子ID
MessagePacket.SplitMessageId(fullMsgId, out byte mainId, out byte subId);
```

组合规则：`fullMsgId = (mainId << 8) | subId`
- 高8位为主ID
- 低8位为子ID

## 总结

主ID+子ID的消息设计提供了：
- ✓ 清晰的模块划分
- ✓ 灵活的扩展能力
- ✓ 高效的消息路由
- ✓ 便于团队协作
- ✓ 易于维护和管理

这种设计特别适合大型游戏项目，能够有效组织和管理数百种消息类型。
