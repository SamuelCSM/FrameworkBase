# Network模块 - Protobuf集成

## 快速开始

### 1. 安装protobuf-net
打开Unity编辑器，进入菜单：**Framework > Install Protobuf-net**

这将打开一个安装指南，提供多种安装方法。

### 2. 定义消息

在 `HotUpdate/Proto/` 目录中创建消息类：

```csharp
using ProtoBuf;
using Framework.Network;

[ProtoContract]
public class LoginRequest : IMessage
{
    [ProtoMember(1)]
    public string Username { get; set; }
    
    [ProtoMember(2)]
    public string Password { get; set; }
    
    public ushort GetMessageId() => 1001;
}

[ProtoContract]
public class LoginResponse : IMessage
{
    [ProtoMember(1)]
    public int ResultCode { get; set; }
    
    [ProtoMember(2)]
    public string Token { get; set; }
    
    public ushort GetMessageId() => 1002;
}
```

### 3. 序列化并发送

```csharp
var request = new LoginRequest 
{ 
    Username = "玩家", 
    Password = "密码123" 
};

// 序列化
byte[] data = ProtobufUtil.Serialize(request);

// 通过网络发送（NetworkManager会处理）
NetworkManager.Instance.SendMessage(request.GetMessageId(), data);
```

### 4. 接收并反序列化

```csharp
// 在消息处理器中
public void OnLoginResponse(byte[] data)
{
    var response = ProtobufUtil.Deserialize<LoginResponse>(data);
    
    if (response.ResultCode == 0)
    {
        Debug.Log($"登录成功！Token: {response.Token}");
    }
}
```

## 本模块文件

- **IMessage.cs** - 所有网络消息必须实现的接口
- **ProtobufUtil.cs** - 序列化工具（序列化、反序列化、压缩、解压缩）
- **SampleMessage.cs** - 示例消息定义（仅供参考）
- **PROTOBUF_SETUP.md** - 详细的设置和使用文档

## 核心功能

✓ **高效序列化** - 二进制格式，比JSON更小  
✓ **类型安全** - 编译时类型检查  
✓ **压缩支持** - 可选的GZip压缩用于大消息  
✓ **错误处理** - 全面的异常处理  
✓ **易于使用** - 简单的API，清晰的方法名  

## 压缩使用指南

使用压缩的场景：
- 消息大小 > 1KB
- 消息包含重复数据
- 网络带宽有限

跳过压缩的场景：
- 消息大小 < 500字节
- 实时性能至关重要
- 数据已经压缩过

## 消息ID命名规范

按类别组织消息ID：
- 1-999: 系统消息（心跳、ping等）
- 1000-1999: 登录和认证
- 2000-2999: 玩家数据
- 3000-3999: 战斗/游戏玩法
- 4000-4999: 社交功能
- 5000-5999: 商店和经济

## 最佳实践

1. **始终实现IMessage** - 确保一致的消息处理
2. **使用连续的ProtoMember编号** - 从1开始，不要有间隙
3. **不要更改ProtoMember编号** - 对现有数据是破坏性更改
4. **在末尾添加新字段** - 保持向后兼容性
5. **使用有意义的消息ID** - 使调试更容易
6. **分析压缩效果** - 不是所有消息都能从压缩中受益

## 后续步骤

设置Protobuf后：
1. 实现 **TcpClient** 用于网络连接
2. 实现 **MessageDispatcher** 用于路由消息
3. 实现 **NetworkManager** 将所有内容整合在一起
4. 定义游戏的消息协议

## 故障排除

**"找不到类型'Serializer'"**
→ 安装protobuf-net包（参见安装指南）

**"找不到ProtoContract特性"**
→ 在文件顶部添加 `using ProtoBuf;`

**序列化失败**
→ 确保所有属性都有 `[ProtoMember(n)]` 特性

**压缩没有帮助**
→ 检查压缩率，对于小消息可能不值得

更多帮助，请参阅本目录中的 **PROTOBUF_SETUP.md**。
