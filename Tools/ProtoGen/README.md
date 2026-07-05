# ProtoGen —— 一键双端协议生成器

单一 `.proto` 源 → 一条命令同时生成**客户端 + 服务端**的 Google.Protobuf 协议代码，外加**路由伴生 partial**（`GetMainId/GetSubId` + `IRequest/IResponse`）。无需手装 protoc（借 `Grpc.Tools` NuGet 内置的 protoc 二进制）。

## 前置
- 已装 .NET SDK（net8.0+）。
- 首次使用先构建一次（会还原 `Grpc.Tools`，拿到内置 protoc）：
  ```
  dotnet build Tools/ProtoGen/ProtoGen.csproj -c Debug
  ```

## 使用
**最简：双击仓库根的 `gen-proto.bat`**（自动构建 + 生成 + 停留看结果）。

或命令行（以仓库根为工作目录）：
```
dotnet Tools/ProtoGen/bin/Debug/net8.0/ProtoGen.dll
```
读取 `Tools/ProtoGen/protogen.json`，对每个目标各生成一份 `.cs`。也可传自定义配置路径作第一个参数。

## 写协议：`proto/*.proto`
真 proto3。**消息命名约定**（生成器据此产路由）：
```
<方向>_<主号3位>_<子号3位>_<名称>
```
- 方向：`GC2GS`（客户端→服务端）/ `GS2GC`（服务端→客户端）。
- 主号=模块号；子号 **1–99**=请求/响应（同主+子号双向配对），**100+**=纯推送。
- 请求（`GC2GS`，子号 1–99）若存在同号 `GS2GC` 响应 → 生成 `IRequest<响应>`；含 `ResultCode` 字段的响应 → `IResponse`；其余 → `INetMessage`。

示例：
```proto
syntax = "proto3";
option csharp_namespace = "Game.Protocol";   // 双端共用同一命名空间

message GC2GS_009_001_HeartbeatRequest {
  int64 ClientTime = 1;
  int32 SequenceId = 2;
}
message GS2GC_009_001_HeartbeatResponse {
  int64 ServerTime = 1;
  int32 SequenceId = 2;
}
```

> **IL2CPP 红线**：`repeated` 只能用**引用类型消息**（`repeated Xxx`），禁 `repeated int64/bool` 等值类型（AOT 崩）。标量列表须包成 1 字段消息。

## 配置：`protogen.json`
```json
{
  "protoDir": "proto",
  "targets": [
    { "name": "Client", "outDir": "Assets/Scripts/GameProtocol/Messages", "routingNamespace": "Framework.Network" }
    // 服务端示例（新项目按需加）：
    // { "name": "Server", "outDir": "../YourServer/src/Protocol/Generated", "routingNamespace": "YourServer.Network" }
  ]
}
```
- `outDir` 相对仓库根；命名空间取自 `.proto` 的 `csharp_namespace`（双端一致）。
- `routingNamespace` = 路由接口（`INetMessage/IRequest/IResponse`）所在命名空间：客户端 = `Framework.Network`，服务端 = 你服务端框架对应命名空间。

## 产物
每个目标目录下：
- `<文件名>.cs` —— protoc 生成的 Google.Protobuf 消息类（**勿手改**）。
- `ProtoRouting.g.cs` —— 路由伴生 partial（**勿手改**）。

## 注意
- 生成物是产物，改协议改 `.proto` 后重跑本工具，不手改 `.cs`。
- 生成物依赖：客户端需 `Google.Protobuf.dll`（Unity 插件）+ Framework 的 `INetMessage/IRequest/IResponse`；服务端需 `Google.Protobuf` NuGet + 对应路由接口。
- 运行时**禁**访问 `.Descriptor`/JSON/`ToString()`（IL2CPP 反射会崩）；收发只走二进制 `ToByteArray`/`Parser.ParseFrom`。
