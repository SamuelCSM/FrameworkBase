# Protobuf 集成 —— 实现说明

网络层序列化基于官方 **Google.Protobuf**（protoc 生成显式代码，只走二进制路径，IL2CPP/AOT 安全）。
协议消息由仓库根 `proto/*.proto` 经 `Tools/ProtoGen` 一键生成，不手写。

## 核心组件

### INetMessage 接口（`INetMessage.cs`）
- 网络消息的路由契约：`GetMainId()` / `GetSubId()`（由生成的路由伴生 partial 实现）。
- **继承 `Google.Protobuf.IMessage`**，框架可直接在接口上做二进制序列化（`ToByteArray`/`MergeFrom`），无需反射。
- 派生：`IResponse`（带 `ResultCode`）、`IRequest<TResp>`（声明对应响应类型，供 `RequestAsync` 零泛型参数推断）。

### ProtobufUtil 类（`ProtobufUtil.cs`）
- `Serialize(IMessage message)` → `message.ToByteArray()`。
- `Deserialize<T>(byte[] data)` → `new T()` + `MergeFrom`；空 payload 返回全默认实例（不抛异常）。
- `SerializeWithCompression(IMessage)` / `DeserializeWithDecompression<T>(byte[])` + `Compress`/`Decompress`/`GetCompressionRatio` —— GZip 压缩辅助（大消息可选）。
- 统一异常包装（`ArgumentNullException`/`ArgumentException`/`InvalidOperationException`）。

### NetworkMessageTypeRegistry（`NetworkMessageTypeRegistry.cs`）
- 协议号 ↔ 类型解析器映射，供错误码拦截与协议日志还原字段。
- **惰性显式登记**：首次 `Subscribe<T>`/`RequestAsync<TResp>` 以具体类型 `Register<T>()`（`new T()`+`MergeFrom`），无反射扫描/Activator/MakeGenericMethod → AOT 安全。

### 生成协议（`Assets/Scripts/GameProtocol/`）
- `GameProtocol.asmdef`（引用 Framework），生成的 `<源>.cs`（消息类）+ `<源>.Routing.g.cs`（路由 partial）。
- 样例：System（心跳）、Echo、Inventory（含 `repeated CommonItem`）、Common（纯数据）。

### 编辑器助手（`ProtobufInstaller.cs`）
- 菜单 **Framework > Protobuf Setup**：Google.Protobuf 安装指引（NuGetForUnity）+ ProtoGen 使用指引 + 一键检查是否已加载。

## 定义 → 生成 → 收发

```csharp
// 1) proto/ 里写（名字编码主/子号），双击 gen-proto.bat 生成：
//    message GC2GS_003_001_PlayerDataRequest { int64 PlayerId = 1; }

// 2) 序列化 / 反序列化
var request = new GC2GS_003_001_PlayerDataRequest { PlayerId = 12345 };
byte[] data = ProtobufUtil.Serialize(request);
var back = ProtobufUtil.Deserialize<GC2GS_003_001_PlayerDataRequest>(data);

// 3) 走网络层（推荐）
var resp = await GameEntry.Network.RequestAsync(request);   // 请求实现 IRequest<TResp>
```

## 与网络模块集成

```
消息对象 → ProtobufUtil.Serialize() → MessagePacket.Pack() → TcpClient.Send()
TcpClient 收 → MessagePacket.Unpack() → MessageDispatcher 分发/RequestAsync 兑现 → ProtobufUtil.Deserialize()
```
- `MessagePacket`：8 字节头（Length+MainId+SubId+SeqId），序列化库无关。
- `MessageDispatcher`：主线程队列 + 类型化订阅分发；`NetworkManager`：连接/请求-响应/心跳/拦截统一入口。

## AOT / IL2CPP 注意
- **禁**运行时访问 `.Descriptor`/`JsonFormatter`/`ToString()`（反射会崩），只走二进制。
- `repeated 标量` 与 `repeated 消息`均 AOT 安全（生成代码显式实例化 codec）。

## 性能考虑
- 二进制紧凑、无运行时反射开销；序列化产生临时 byte[]（GC 压力），频发消息可复用对象。
- 压缩：文本密集数据省 50-80% 带宽但增 CPU，用 `GetCompressionRatio()` 评估后再启用。

## 往返测试建议（EditMode）
```csharp
[Test]
public void ProtobufUtil_往返()
{
    var msg = new GS2GC_010_001_GetInventoryResponse { ResultCode = 0 };
    msg.Items.Add(new CommonItem { Id = 1, Name = "gold", Count = 99 });

    byte[] data = ProtobufUtil.Serialize(msg);
    var back = ProtobufUtil.Deserialize<GS2GC_010_001_GetInventoryResponse>(data);

    Assert.AreEqual(1, back.Items.Count);
    Assert.AreEqual("gold", back.Items[0].Name);
}
```

## 相关文档
- [PROTOBUF_SETUP.md](PROTOBUF_SETUP.md) —— 安装与 API。
- [MESSAGE_ID_DESIGN.md](MESSAGE_ID_DESIGN.md) —— 主/子号与 SeqId 规约。
- 仓库根 `Tools/ProtoGen/README.md` —— 生成器使用说明。
