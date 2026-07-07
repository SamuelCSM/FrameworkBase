# HTTP / 序列化使用规范

框架统一封装了 HTTP 传输（`Framework.Http`）与 JSON 序列化（`Framework.Serialization`）。
**运行时任何联网或 JSON 读写，先用这两层，不要直接碰 `UnityWebRequest` / `JsonUtility`。**
目的：传输/序列化实现可整体替换（测试注 Mock、接第三方栈、加统一鉴权/埋点/重试），
业务模块不被 Unity API 绑死，也不各写一份解析。

## HTTP：怎么发请求

```csharp
using Framework.Http;

// 一次性取文本 / 字节（失败返回 null，最省事）
string body  = await HttpClients.Shared.GetTextAsync(url, timeoutSeconds: 10);
byte[] bytes = await HttpClients.Shared.GetBytesAsync(url);

// POST 文本，拿到完整响应（状态码 / 头 / 错误）
HttpResponse resp = await HttpClients.Shared.PostTextAsync(
    url, jsonBody, "application/json", timeoutSeconds: 10);
if (resp.Succeeded) { /* resp.Text / resp.Data */ }

// 需要自定义头 / 方法：直接构造 HttpRequest
var req = HttpRequest.Post(url, payloadBytes, "application/json")
    .WithTimeout(15)
    .WithHeader("Authorization", token);
HttpResponse r = await HttpClients.Shared.SendAsync(req);
```

约定：

- **不抛异常**：网络错误、超时、非 2xx 都折算成非成功响应。判成功一律用
  `response.Succeeded`（无传输错误 且 2xx，或本地 file/jar 传输的 StatusCode==0），
  不要自己比 `StatusCode == 200`。
- **后端可换**：`HttpClients.Shared` 是全局注入点。宿主可在启动时替换（统一鉴权头、
  链路追踪、平台专用传输）；单测注入 `IHttpClient` 假实现（见 `Tests/EditMode/HttpTests.cs`）。
- **URL 转义**走 `HttpUrl.EscapeQueryValue`，不要引 `UnityWebRequest.EscapeURL`。

## 序列化：怎么读写 JSON

分两种，按数据形态选：

| 数据形态 | 用什么 | 说明 |
|---|---|---|
| 强类型 DTO（`[Serializable]` 类/结构） | `JsonSerializers.Shared` | 包 JsonUtility，`ToJson/FromJson/TryFromJson`；后端可换 |
| 动态键值（任意键名的对象/配置负载） | `JsonObjectParser.TryParseObject` / `JsonWriter` | JsonUtility 不支持 Dictionary，故自带极简解析/写出 |

```csharp
using Framework.Serialization;

// 强类型（版本文件、快照等）
string json = JsonSerializers.Shared.ToJson(versionInfo, prettyPrint: true);
if (JsonSerializers.Shared.TryFromJson<UpdateInfo>(json, out var info)) { ... }

// 动态键值（远程配置、埋点属性）
if (JsonObjectParser.TryParseObject(payload, out var dict)) { ... } // object→Dictionary，数组→List，整数→long，小数→double
string outJson = JsonWriter.SerializeObject(dict);
```

约定：

- 解析失败返回 `false`，**不抛异常**——调用方保留现值，别让脏数据打挂客户端。
- 数字映射固定：整数 → `long`，小数/指数 → `double`；取值时按需 `CoerceToLong` 等收窄。
- 别再手写 `StringBuilder` 拼 JSON。埋点信封（`AnalyticsJson`）与远程配置解析
  （`RemoteConfigJson`）都是薄壳，内部转调这两个类——照此模式即可，勿复制解析逻辑。

## 允许直连底层的例外

只有一个，且有明确理由，新增例外须在此登记：

- **`HotUpdate/PatchDownloader.cs`** 直连 `UnityWebRequest`：需要**流式下载 + 进度回调 +
  Range 断点续传 + 416 回退**，当前 `IHttpClient` 是一次性缓冲全量 `byte[]` 的模型，
  承载不了大包分片。热更补丁动辄上百 MB，不能整包进内存，故保留直连。
  若将来给 `IHttpClient` 加流式下载能力，这里应回迁。

**Editor 工具**（`Editor/**`）不受本规范约束：仅在编辑器/发布机跑，可直接用
`UnityWebRequest` / `JsonUtility`，不必为可替换性买单。

## 单测怎么隔离全局注入点

`HttpClients.Shared` / `JsonSerializers.Shared` 是静态可变全局。若某用例替换了它们，
**务必在 `TearDown` 还原**（置回 `new UnityHttpClient()` / `new UnityJsonSerializer()`
或 `null` 触发惰性重建），否则污染同进程后续用例。优先像 `HttpTests` 那样直接对
`IHttpClient` 假实现调用，不碰全局单例。
