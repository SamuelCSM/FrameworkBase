# Network 模块 —— Google.Protobuf 集成

## 快速开始

### 1. 安装 Google.Protobuf 运行时
本工程内置 NuGetForUnity，`Assets/packages.config` 已声明 `Google.Protobuf`（随仓库附带 `Assets/Packages/Google.Protobuf.<版本>/`）。
打开 Unity 即可用；如缺失可 `NuGet > Restore Packages`。编辑器入口：菜单 **Framework > Protobuf Setup**（指引 + 检查）。

### 2. 定义消息（写 .proto，不手写 C#）
在仓库根 `proto/` 下写 proto3 源，命名遵循 `<方向>_<主号3位>_<子号3位>_<名称>`：

```proto
syntax = "proto3";
option csharp_namespace = "Game.Protocol";

message GC2GS_002_001_LoginRequest {
  string Username = 1;
  string Password = 2;
}
message GS2GC_002_001_LoginResponse {
  int32 ResultCode = 1;   // 含 ResultCode 的响应 → 生成为 IResponse
  string Token = 2;
}
```

双击仓库根 `gen-proto.bat`（或运行 `Tools/ProtoGen`）→ 生成消息类 + 路由伴生 partial：
请求自动声明为 `IRequest<同号响应>`、含 `ResultCode` 的响应声明为 `IResponse`。生成物勿手改。

### 3. 收发（推荐走网络层）

```csharp
using Game.Protocol;

// 请求-响应（请求实现 IRequest<TResp>，零泛型参数）
var resp = await GameEntry.Network.RequestAsync(
    new GC2GS_002_001_LoginRequest { Username = "玩家", Password = "密码123" });
if (resp.ResultCode == 0) { /* 登录成功，resp.Token */ }

// 订阅服务端推送
var sub = GameEntry.Network.Subscribe<GS2GC_002_101_SomePush>(msg => { /* ... */ });
// 记得在合适时机 sub.Dispose();
```

底层裸序列化（一般不需手动调用）：`ProtobufUtil.Serialize(msg)` / `ProtobufUtil.Deserialize<T>(bytes)`。

## 本模块关键文件

- **INetMessage.cs** —— 路由接口（`GetMainId/GetSubId`），继承 `Google.Protobuf.IMessage`；`IResponse`/`IRequest<TResp>` 亦在此。
- **ProtobufUtil.cs** —— 序列化工具（`ToByteArray`/`MergeFrom` + GZip 压缩辅助）。
- **NetworkManager.cs** —— 连接、请求-响应、心跳、订阅分发的统一入口。
- **MessagePacket.cs** —— 8 字节包头（Length+MainId+SubId+SeqId）组包/解包。
- **NetworkMessageTypeRegistry.cs** —— 协议号↔类型解析器（惰性显式登记，AOT 安全）。
- **PROTOBUF_SETUP.md** —— 详细设置与 API 文档；**MESSAGE_ID_DESIGN.md** —— 协议编号规则。

## 核心特性
✓ 二进制、跨语言、比 JSON 更小 ✓ 强类型（生成代码编译期检查） ✓ 可选 GZip 压缩 ✓ 统一异常处理 + `GameLog` ✓ IL2CPP/AOT 安全（只走二进制路径）

## 压缩使用指南
- 使用：消息 >1KB、含重复数据、带宽受限。
- 跳过：消息 <500B、实时关键、数据已压缩。

## 协议编号规范（详见 MESSAGE_ID_DESIGN.md）
`<方向>_<主号>_<子号>_<名称>`：主号=模块，子号 **1–99**=请求/响应（同号双向配对），**100+**=纯推送。
请求与其响应同主+子号；生成器据此产 `IRequest<Resp>` 配对。

## 最佳实践
1. **只改 .proto、重跑生成**，不手写/手改协议类。
2. **字段号一经使用不改动**（破坏兼容）；新字段追加在末尾。
3. 请求-响应用 `RequestAsync`；推送用 `Subscribe` 并成对 `Dispose`。
4. **运行时禁访问** `.Descriptor`/JSON/`ToString()`（IL2CPP 反射会崩）。
5. 大消息才考虑压缩，用 `GetCompressionRatio()` 评估。

## 故障排除
- **找不到 `Google.Protobuf`** → NuGetForUnity 还原，确认 `Assets/Packages/Google.Protobuf.*` 存在。
- **收发字段丢失** → 双端用同一份生成物、改协议后重跑 ProtoGen、字段号未变。
- **请求超时转圈** → 检查子号 1–99 的响应由 handler 同步回包（SeqId 回填），别走异步出站端口。

更多见本目录 **PROTOBUF_SETUP.md** 与仓库根 `Tools/ProtoGen/README.md`。
