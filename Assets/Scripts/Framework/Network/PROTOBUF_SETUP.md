# Protobuf集成设置指南

## 概述
本框架使用 **protobuf-net** 进行Protocol Buffers序列化。Protobuf-net是一个与Unity配合良好的.NET实现。

## 安装方法

### 方法1：NuGet for Unity（推荐）
1. 从Unity Asset Store或GitHub安装NuGet for Unity
2. 打开NuGet包管理器（Window > NuGet > Manage NuGet Packages）
3. 搜索"protobuf-net"并安装 2.4.x 版本（**勿用 3.x**：其 repeated 字段在 IL2CPP/AOT 上会崩 GetRequiredCustomModifiers icall）

### 方法2：手动安装
1. 从以下地址下载protobuf-net：https://github.com/protobuf-net/protobuf-net/releases
2. 解压以下DLL文件（2.4.x 为单一程序集，无 protobuf-net.Core）：
   - protobuf-net.dll
3. 创建文件夹：`Assets/Plugins/protobuf-net/`
4. 将DLL文件复制到Plugins文件夹
5. 重启Unity编辑器

### 方法3：Unity包管理器（如果可用）
```
通过git URL添加：https://github.com/protobuf-net/protobuf-net.git
```

## 验证安装
安装后，验证Framework编译无错误。以下文件应该正常工作：
- `IMessage.cs` - 消息接口
- `ProtobufUtil.cs` - 序列化工具

## 使用示例

### 1. 定义Protobuf消息
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
```

### 2. 序列化消息
```csharp
var request = new LoginRequest 
{ 
    Username = "玩家123", 
    Password = "密码" 
};

// 基础序列化
byte[] data = ProtobufUtil.Serialize(request);

// 使用压缩（适用于大消息）
byte[] compressedData = ProtobufUtil.SerializeWithCompression(request);
```

### 3. 反序列化消息
```csharp
// 基础反序列化
LoginRequest request = ProtobufUtil.Deserialize<LoginRequest>(data);

// 使用解压缩
LoginRequest request = ProtobufUtil.DeserializeWithDecompression<LoginRequest>(compressedData);
```

## 功能特性

### 基础序列化
- `Serialize<T>(T message)` - 将消息序列化为字节数组
- `Deserialize<T>(byte[] data)` - 将字节数组反序列化为消息

### 压缩支持（可选）
- `SerializeWithCompression<T>(T message)` - 使用GZip序列化并压缩
- `DeserializeWithDecompression<T>(byte[] data)` - 解压缩并反序列化
- `Compress(byte[] data)` - 压缩原始字节数组
- `Decompress(byte[] data)` - 解压缩原始字节数组
- `GetCompressionRatio(int original, int compressed)` - 计算压缩效率

### 何时使用压缩
- **使用压缩的场景**：大消息（>1KB）、文本密集型数据、重复模式数据
- **跳过压缩的场景**：小消息（<500字节）、已压缩数据、实时关键消息
- 压缩会增加CPU开销但减少网络带宽

## 错误处理
所有方法在失败时会抛出异常：
- `ArgumentNullException` - 空输入
- `ArgumentException` - 空或无效输入
- `InvalidOperationException` - 序列化/反序列化失败

始终使用try-catch块包装调用：
```csharp
try
{
    byte[] data = ProtobufUtil.Serialize(message);
}
catch (Exception ex)
{
    Logger.Error($"序列化失败: {ex.Message}");
}
```

## 性能建议
1. 尽可能重用消息对象以减少GC压力
2. 仅对大于1KB的消息使用压缩
3. 考虑对频繁发送的消息使用对象池
4. 分析压缩率以决定是否值得CPU开销

## 故障排除

### "找不到类型'Serializer'"
- 确保protobuf-net已正确安装
- 检查DLL是否在正确位置
- 验证Framework.asmdef引用了protobuf-net程序集

### 序列化失败
- 确保所有消息类都有`[ProtoContract]`特性
- 确保所有属性都有`[ProtoMember(n)]`特性且编号唯一
- 检查消息类不是抽象类

### 压缩没有减小大小
- 小消息可能压缩效果不好
- 已压缩的数据（图像、音频）不会进一步压缩
- 使用`GetCompressionRatio()`检查压缩率

## 后续步骤
设置Protobuf后：
1. 定义游戏的消息协议（参见HotUpdate/Proto/）
2. 实现NetworkManager用于消息发送/接收
3. 实现MessageDispatcher用于消息路由
4. 为每种消息类型创建消息处理器
