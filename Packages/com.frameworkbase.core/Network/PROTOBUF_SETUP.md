# Protobuf 集成设置指南

## 概述
本框架使用官方 **Google.Protobuf** 做 Protocol Buffers 序列化：由 `protoc` 生成显式 C# 代码，
只走二进制路径（`ToByteArray` / `MergeFrom` / `Parser`），在 Unity IL2CPP(AOT) 上安全（不触碰 `Descriptor`/JSON/反射）。
协议消息**不手写**，统一由仓库根的一键生成器 **ProtoGen** 从 `proto/*.proto` 生成。

## 安装 Google.Protobuf 运行时
本工程已内置 NuGetForUnity，`Assets/packages.config` 已声明 `Google.Protobuf`，随仓库附带 `Assets/Packages/Google.Protobuf.<版本>/`。

- 正常打开 Unity 即可用；若丢失：菜单 `NuGet > Restore Packages`，或 `Manage NuGet Packages` 搜索 `Google.Protobuf` 安装。
- 只需 `Google.Protobuf.dll` 一个托管程序集；若个别 Unity 配置报缺 `System.Memory`/`System.Buffers`，再按提示补对应 NuGet 包。
- 也有编辑器入口：菜单 `Framework > Protobuf Setup`（安装指引 + 一键检查是否已加载）。

## 定义并生成协议
1. 在仓库根 `proto/` 下写 proto3 源，消息命名遵循 `<方向>_<主号3位>_<子号3位>_<名称>`（`GC2GS_*` 上行 / `GS2GC_*` 下行）。
2. 双击仓库根 `gen-proto.bat`（或运行 `Tools/ProtoGen`）→ 生成 Google.Protobuf 消息类 + 路由伴生 partial（`GetMainId/GetSubId` + `IRequest/IResponse`）。
3. 生成物勿手改；命名/编号/配置规则见 [`../../../Tools/ProtoGen/README.md`](../../../../Tools/ProtoGen/README.md) 与 [`MESSAGE_ID_DESIGN.md`](MESSAGE_ID_DESIGN.md)。

生成的消息即实现 `Framework.Network.INetMessage`（继承 `Google.Protobuf.IMessage`），可直接进入下面的收发链路。

## 使用示例

### 序列化 / 反序列化
```csharp
using Framework;            // ProtobufUtil
using Game.Protocol;        // 生成的协议命名空间（由 .proto 的 csharp_namespace 决定）

var request = new GC2GS_001_001_HeartbeatRequest { ClientTime = 123, SequenceId = 7 };

byte[] data = ProtobufUtil.Serialize(request);                 // = request.ToByteArray()
var back = ProtobufUtil.Deserialize<GC2GS_001_001_HeartbeatRequest>(data); // new + MergeFrom
```

### 通过网络层收发（推荐）
```csharp
// 请求-响应（请求实现 IRequest<TResp>，零泛型参数）
var resp = await GameEntry.Network.RequestAsync(new GC2GS_001_001_HeartbeatRequest { ClientTime = now });

// 订阅服务端推送
var sub = GameEntry.Network.Subscribe<GS2GC_010_101_SomePush>(msg => { /* ... */ });
```

## ProtobufUtil API
- `Serialize(IMessage message)` — 序列化为字节数组（`ToByteArray`）。
- `Deserialize<T>(byte[] data)` — 反序列化（`new T()` + `MergeFrom`；空 payload 返回全默认实例，不抛异常）。
- `SerializeWithCompression(IMessage)` / `DeserializeWithDecompression<T>(byte[])` — GZip 压缩变体（大消息可选）。
- `Compress` / `Decompress` / `GetCompressionRatio` — 裸字节压缩辅助。

### 何时使用压缩
- 适合：大消息（>1KB）、文本密集、重复模式数据。
- 跳过：小消息（<500B）、已压缩数据、实时关键消息。压缩省带宽但增 CPU。

## 错误处理
失败抛异常：`ArgumentNullException`（空输入）、`ArgumentException`（空/无效输入）、`InvalidOperationException`（序列化/反序列化失败）。
框架内部收发已统一 try-catch 并经 `GameLog` 记录；业务直接用网络层 API 即可。

## AOT / IL2CPP 注意
- **禁**在运行时访问 `.Descriptor`、`JsonFormatter`、`ToString()`（走反射，IL2CPP 会崩）；收发只走二进制。
- `repeated 标量` 与 `repeated 消息` 在 Google.Protobuf 下均 AOT 安全（生成代码显式实例化 codec，不走反射），无需再把标量列表包成 1 字段消息。
- 协议类型采用惰性显式登记：首次 `Subscribe<T>`/`RequestAsync<TResp>` 以具体类型登记，不做启动期全程序集反射扫描。

## 故障排除
- **找不到 `Google.Protobuf`**：确认 NuGetForUnity 已还原、`Assets/Packages/Google.Protobuf.*` 存在、Framework 能引用到该插件（自动引用）。
- **收发字段丢失**：确认 `.proto` 字段号未变、双端用同一份生成物、改协议后已重跑 ProtoGen。
- **压缩没减小体积**：小消息/已压缩数据压缩效果差，用 `GetCompressionRatio()` 判断是否值得。

## 后续步骤
1. 在 `proto/` 定义业务协议并一键生成（双端同源）。
2. 用 `GameEntry.Network.RequestAsync` / `Subscribe` 收发。
3. 需要还原/展开协议日志时，登记发生在订阅/请求时自动完成。
