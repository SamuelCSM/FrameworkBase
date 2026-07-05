# TCP客户端使用指南

## 概述

TcpClient类提供了底层TCP网络连接功能，支持异步连接、线程安全的消息发送和自动消息解析。

## 核心功能

### 1. 连接管理
- 异步连接：`ConnectAsync(host, port)`
- 同步连接：`Connect(host, port)`
- 断开连接：`Disconnect()`
- 连接状态：`IsConnected` 属性

### 2. 消息收发
- 发送消息：`Send(byte[] data)` - 线程安全
- 接收消息：通过 `OnReceive` 事件回调

### 3. 事件回调
- `OnConnected` - 连接成功
- `OnDisconnected` - 断开连接
- `OnReceive` - 接收到完整消息
- `OnError` - 发生错误

## 消息协议格式

```
┌──────────┬──────────┬──────────┬──────────┬──────────────┐
│  Length  │  MainId  │  SubId   │ Reserved │   Payload    │
│ (4 bytes)│(1 byte)  │(1 byte)  │(2 bytes) │  (N bytes)   │
└──────────┴──────────┴──────────┴──────────┴──────────────┘
```

- **Length**: 消息总长度（包含头部，4字节）
- **MainId**: 主消息ID/模块ID（1字节，0-255）
- **SubId**: 子消息ID/消息类型ID（1字节，0-255）
- **Reserved**: 保留字段（2字节）
- **Payload**: Protobuf序列化的消息体（N字节）

### 消息ID设计

消息ID采用主ID+子ID的两级结构：
- **主ID（MainId）**: 表示功能模块，如登录模块、战斗模块等
- **子ID（SubId）**: 表示该模块下的具体消息类型

示例：
- 主ID=1, 子ID=1: 系统模块的心跳请求
- 主ID=2, 子ID=1: 登录模块的登录请求
- 主ID=3, 子ID=5: 玩家模块的获取玩家信息

这种设计的优点：
- 更好的消息组织和分类
- 每个模块最多支持256种消息类型
- 便于消息路由和处理器管理

## 使用示例

### 基础使用

```csharp
using Framework.Network;
using Cysharp.Threading.Tasks;

public class NetworkExample
{
    private TcpClient _client;

    public async UniTask ConnectToServer()
    {
        _client = new TcpClient();

        // 注册事件
        _client.OnConnected += OnConnected;
        _client.OnDisconnected += OnDisconnected;
        _client.OnReceive += OnReceive;
        _client.OnError += OnError;

        // 异步连接
        try
        {
            await _client.ConnectAsync("127.0.0.1", 8888);
        }
        catch (Exception ex)
        {
            Debug.LogError($"连接失败: {ex.Message}");
        }
    }

    private void OnConnected()
    {
        Debug.Log("连接成功！");
        // 发送登录消息
        SendLoginMessage();
    }

    private void OnDisconnected()
    {
        Debug.Log("连接已断开");
    }

    private void OnReceive(byte[] packet)
    {
        // 解析消息包
        if (MessagePacket.Unpack(packet, out byte mainId, out byte subId, out byte[] payload))
        {
            Debug.Log($"收到消息 主ID: {mainId}, 子ID: {subId}, 长度: {payload.Length}");
            // 将消息交给MessageDispatcher处理
        }
    }

    private void OnError(string error)
    {
        Debug.LogError($"网络错误: {error}");
    }

    private void SendLoginMessage()
    {
        // 创建登录请求
        var request = new LoginRequest 
        { 
            Username = "玩家123", 
            Password = "密码" 
        };

        // 序列化消息
        byte[] payload = ProtobufUtil.Serialize(request);

        // 打包消息（使用主ID和子ID）
        byte[] packet = MessagePacket.Pack(request.GetMainId(), request.GetSubId(), payload);
        // 或者直接使用消息对象
        // byte[] packet = MessagePacket.Pack(request, payload);

        // 发送
        _client.Send(packet);
    }

    public void Disconnect()
    {
        if (_client != null)
        {
            _client.Disconnect();
            _client = null;
        }
    }
}
```

### 完整的消息发送流程

```csharp
// 1. 定义消息模块ID
public static class MessageModule
{
    public const byte System = 1;
    public const byte Login = 2;
    public const byte Chat = 3;
}

// 2. 定义消息
[ProtoContract]
public class ChatMessage : IMessage
{
    [ProtoMember(1)]
    public string Content { get; set; }

    public byte GetMainId() => MessageModule.Chat;
    public byte GetSubId() => 1; // 发送聊天消息
}

// 3. 发送消息
public void SendChatMessage(string content)
{
    // 创建消息对象
    var message = new ChatMessage { Content = content };

    // 序列化
    byte[] payload = ProtobufUtil.Serialize(message);

    // 打包（方式1：使用主ID和子ID）
    byte[] packet = MessagePacket.Pack(message.GetMainId(), message.GetSubId(), payload);
    
    // 打包（方式2：直接使用消息对象）
    // byte[] packet = MessagePacket.Pack(message, payload);

    // 发送
    _client.Send(packet);
}

// 4. 接收消息
private void OnReceive(byte[] packet)
{
    // 解析消息包
    if (!MessagePacket.Unpack(packet, out byte mainId, out byte subId, out byte[] payload))
    {
        Debug.LogError("消息包解析失败");
        return;
    }

    // 根据主ID和子ID处理
    if (mainId == MessageModule.Chat && subId == 1)
    {
        var chatMsg = ProtobufUtil.Deserialize<ChatMessage>(payload);
        Debug.Log($"收到聊天消息: {chatMsg.Content}");
    }
    else if (mainId == MessageModule.Login && subId == 2)
    {
        var loginResp = ProtobufUtil.Deserialize<LoginResponse>(payload);
        Debug.Log($"登录响应: {loginResp.ResultCode}");
    }
    else
    {
        Debug.LogWarning($"未处理的消息: 主ID={mainId}, 子ID={subId}");
    }
}
```

## MessagePacket工具类

### 打包消息

```csharp
// 方法1：使用主ID和子ID
byte[] payload = ProtobufUtil.Serialize(message);
byte[] packet = MessagePacket.Pack(mainId, subId, payload);
_client.Send(packet);

// 方法2：使用消息对象（推荐）
byte[] payload = ProtobufUtil.Serialize(message);
byte[] packet = MessagePacket.Pack(message, payload);
_client.Send(packet);

// 方法3：空消息（只有消息头）
byte[] packet = MessagePacket.Pack(mainId, subId, null);
_client.Send(packet);
```

### 解析消息

```csharp
// 完整解析
if (MessagePacket.Unpack(packet, out byte mainId, out byte subId, out byte[] payload))
{
    // 处理消息
}

// 只获取主消息ID
byte mainId = MessagePacket.GetMainId(packet);

// 只获取子消息ID
byte subId = MessagePacket.GetSubId(packet);

// 获取完整消息ID（主ID和子ID组合为ushort）
ushort fullMsgId = MessagePacket.GetMessageId(packet);

// 组合主ID和子ID
ushort msgId = MessagePacket.CombineMessageId(mainId, subId);

// 拆分完整消息ID
MessagePacket.SplitMessageId(msgId, out byte mainId, out byte subId);

// 只获取消息长度
int length = MessagePacket.GetMessageLength(packet);

// 验证消息包
bool isValid = MessagePacket.IsValid(packet);
```

## 线程安全

### 发送线程安全
`Send()` 方法是线程安全的，可以从任何线程调用：

```csharp
// 主线程发送
_client.Send(packet);

// 工作线程发送
Task.Run(() => {
    _client.Send(packet);
});
```

### 接收事件处理
`OnReceive` 事件在接收线程中触发，如果需要访问Unity对象，需要切换到主线程：

```csharp
private void OnReceive(byte[] packet)
{
    // 在接收线程中
    
    // 切换到主线程处理
    UniTask.Post(() => {
        // 在主线程中，可以安全访问Unity对象
        ProcessMessage(packet);
    });
}
```

## 错误处理

### 连接错误

```csharp
try
{
    await _client.ConnectAsync("127.0.0.1", 8888);
}
catch (SocketException ex)
{
    Debug.LogError($"Socket错误: {ex.Message}");
}
catch (Exception ex)
{
    Debug.LogError($"连接失败: {ex.Message}");
}
```

### 发送错误

发送失败会触发 `OnError` 事件，并自动断开连接：

```csharp
_client.OnError += (error) => {
    Debug.LogError($"发送错误: {error}");
    // 尝试重连
    ReconnectAsync().Forget();
};
```

### 接收错误

接收线程异常会自动断开连接并触发 `OnDisconnected` 事件。

## 性能优化

### 1. 禁用Nagle算法
TcpClient已自动设置 `NoDelay = true`，减少小包延迟。

### 2. 缓冲区大小
- 接收缓冲区：8KB（可根据需要调整）
- 消息缓冲区：64KB（支持最大64KB的单个消息）

### 3. 消息合并
如果需要发送多个小消息，可以合并后一次发送：

```csharp
List<byte[]> messages = new List<byte[]>();
messages.Add(MessagePacket.Pack(1001, payload1));
messages.Add(MessagePacket.Pack(1002, payload2));

// 合并发送
byte[] combined = CombineMessages(messages);
_client.Send(combined);
```

## 最佳实践

### 1. 资源清理
在应用退出或场景切换时，确保断开连接：

```csharp
void OnDestroy()
{
    _client?.Disconnect();
}

void OnApplicationQuit()
{
    _client?.Disconnect();
}
```

### 2. 重连机制
连接断开后，实现自动重连：

```csharp
private async UniTask ReconnectAsync()
{
    int retryCount = 0;
    int maxRetries = 5;

    while (retryCount < maxRetries)
    {
        try
        {
            await UniTask.Delay(TimeSpan.FromSeconds(Math.Pow(2, retryCount)));
            await _client.ConnectAsync("127.0.0.1", 8888);
            break;
        }
        catch
        {
            retryCount++;
        }
    }
}
```

### 3. 心跳机制
定期发送心跳包保持连接：

```csharp
private async UniTask StartHeartbeat()
{
    while (_client.IsConnected)
    {
        SendHeartbeat();
        await UniTask.Delay(TimeSpan.FromSeconds(30));
    }
}

private void SendHeartbeat()
{
    var heartbeat = new HeartbeatRequest { ClientTime = DateTime.Now.Ticks };
    byte[] payload = ProtobufUtil.Serialize(heartbeat);
    byte[] packet = MessagePacket.Pack(heartbeat, payload);
    _client.Send(packet);
}
```

## 与NetworkManager集成

TcpClient是底层组件，通常不直接使用，而是通过NetworkManager：

```csharp
// NetworkManager内部使用TcpClient
public class NetworkManager : FrameworkComponent
{
    private TcpClient _client;
    private MessageDispatcher _dispatcher;

    public void Connect(string host, int port)
    {
        _client = new TcpClient();
        _client.OnReceive += OnReceive;
        _client.ConnectAsync(host, port).Forget();
    }

    private void OnReceive(byte[] packet)
    {
        // 解析并分发消息
        if (MessagePacket.Unpack(packet, out byte mainId, out byte subId, out byte[] payload))
        {
            _dispatcher.DispatchMessage(mainId, subId, payload);
        }
    }

    public void SendMessage<T>(T message) where T : IMessage
    {
        byte[] payload = ProtobufUtil.Serialize(message);
        byte[] packet = MessagePacket.Pack(message, payload);
        _client.Send(packet);
    }
}
```

## 故障排除

### 连接超时
- 检查服务器地址和端口是否正确
- 检查防火墙设置
- 检查网络连接

### 消息解析失败
- 确保客户端和服务器使用相同的消息格式
- 检查字节序（大端/小端）
- 验证Protobuf版本一致

### 内存泄漏
- 确保在不使用时调用 `Disconnect()`
- 取消注册事件监听器

## 下一步

实现以下组件以完善网络模块：
1. **MessageDispatcher** - 消息分发器
2. **NetworkManager** - 网络管理器
3. **心跳机制** - 保持连接活跃
4. **重连机制** - 自动重连

参考设计文档中的相关章节。
