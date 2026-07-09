# 云存档使用指南

## 定位

给本地存档叠加一层**尽力而为**的云同步。框架主干只提供**抽象缝 + 冲突决策**，
具体云后端（Google Play Games Saved Games / iCloud / 自建服务端）进扩展包注入。

**核心哲学：离线优先。** 本地存档永远权威可玩，云同步是叠加层——后端不可用即返回 `Offline`，
绝不阻断任何本地读写（与崩溃后端本地兜底、远程配置 last-known-good 同一套路）。

## 边界：框架管什么 / 不管什么

| 框架主干（`Framework.Save.Cloud`） | 扩展包 / app |
|---|---|
| `ICloudSaveBackend` 契约 | 厂商后端具体实现（存取字节） |
| 冲突决策 `Decide`（纯函数） | 冲突的**业务合并**（背包并集、货币取大值…） |
| 默认冲突策略（时间戳裁决） | 账号绑定的密钥来源（跨设备解密前提） |
| 默认关闭兜底 + Mock | 同步时机编排（登录后拉、退后台推…） |

## 关键概念

| 概念 | 说明 |
|---|---|
| `key` | 存档唯一标识，建议含账号+类型+槽位，如 `u_10001/PlayerData_0`。 |
| `CloudSaveRecord` | 一条云存档 = 元数据 + 正文字节。正文对后端**不透明**（通常是 SaveManager 的加密封包）。 |
| `Metadata.Version` | **同步计数器**，每次成功上传 +1，冲突判定首要依据。**≠** `SaveData.dataVersion`（结构版本），别混。 |
| `Metadata.ContentHash` | 正文摘要，同版本下判"内容是否一致"。 |
| `Metadata.TimestampUtc` | 墙钟，仅作冲突兜底与展示（多设备时钟不可信，不作首要依据）。 |

## 决策规则（`CloudSaveSync.Decide`，纯函数）

| 本地 | 云端 | 结果 |
|---|---|---|
| 无 | 无 | `None` |
| 有 | 无 | `Upload` |
| 无 | 有 | `Download` |
| v=1 | v=2 | `Download`（云端计数器更高） |
| v=3 | v=2 | `Upload`（本地计数器更高） |
| v=2, hash=X | v=2, hash=X | `None`（同版同内容） |
| v=2, hash=X | v=2, hash=Y | `Conflict`（同版分叉 → 交解决器） |

## 接入（扩展包 / 组合根）

```csharp
// 1) 注入云后端（不注入则云同步关闭，纯本地照常工作）
CloudSaveSync.SetBackend(new GooglePlayCloudBackend());

// 2) 跨设备解密前提：密钥来源必须账号绑定，不能device-bound
//    （否则 A 设备加密的存档 B 设备解不开）
SaveManager.Instance.SetSaveKeyProvider(new ServerIssuedKeyProvider(accountKey));
```

## 同步一次

```csharp
// local：本地存档条目（无则传 null）。正文通常取自 SaveManager 的加密封包字节。
var local = CloudSaveRecord.Create(version: localSyncVersion, payload: encryptedBytes, deviceId: SystemInfo.deviceUniqueIdentifier);

CloudSyncResult r = await CloudSaveSync.SyncAsync("u_10001/PlayerData_0", local);

switch (r.Status)
{
    case CloudSyncStatus.Offline:    /* 后端不可用，保持本地，稍后重试 */ break;
    case CloudSyncStatus.UpToDate:   /* 两端一致 */ break;
    case CloudSyncStatus.Uploaded:   /* 本地已推云，本地 syncVersion 记为已同步 */ break;
    case CloudSyncStatus.Downloaded: // 云端更新已取回，落盘到本地
        WriteBackToLocalSave(r.DownloadedRecord.Payload);
        break;
}
```

> `SyncAsync` **只透过后端做 IO，不碰文件**——下载结果交回调用方落盘，保持编排器可单测、
> 也让"落盘到哪、怎么落"由 SaveManager 侧决定，不越权。

## 冲突：默认策略会丢数据，价值存档务必自定义合并

默认 `ResolveConflictByTimestamp`（新时间戳胜、并列保本地）是**确定性兜底**，
但时间戳裁决会**整份覆盖**、丢掉另一端进度。有价值的存档应传自定义解决器做**字段级合并**：

```csharp
var r = await CloudSaveSync.SyncAsync(key, local, conflictResolver: (localMeta, cloudMeta) =>
{
    // 例：这里只能返回方向；真正的字段合并需先下载云端正文，
    // 与本地合并出新存档后按 Upload 推回。复杂合并建议：
    //   1) 先 DownloadAsync 取云端正文
    //   2) 业务层合并（背包并集、货币取大值、成就取全集…）
    //   3) 以合并结果 + 更高 version 重新 SyncAsync/UploadAsync
    return CloudSyncDirection.Upload;
});
```

**为什么合并归 app**：合并规则是纯业务语义（哪些字段取大、哪些取并集），框架不该臆断；
框架只保证"检测到冲突并把裁决权交给你"。

## 测试

`InMemoryCloudSaveBackend` 进程内模拟云端，`Available` 可切换模拟离线：

```csharp
var backend = new InMemoryCloudSaveBackend();
backend.Seed("k", CloudSaveRecord.Create(5, cloudBytes));
CloudSaveSync.SetBackend(backend);
var r = await CloudSaveSync.SyncAsync("k", null); // → Downloaded
```

决策核心 `Decide` / `ResolveConflictByTimestamp` 是纯函数，可脱离 Unity 直接断言。

## 相关类型

| 类型 | 职责 |
|---|---|
| `CloudSaveSync` | 编排器：注入后端、`Decide` 决策、`SyncAsync` 执行。 |
| `ICloudSaveBackend` | 云后端缝（哑存储，按 key 存取字节）。 |
| `CloudSaveRecord` / `CloudSaveMetadata` | 存档条目 / 同步元数据。 |
| `CloudSyncDirection` / `CloudSyncResult` | 同步动作 / 同步结果。 |
| `NoOpCloudSaveBackend` / `InMemoryCloudSaveBackend` | 默认关闭兜底 / 测试 Mock。 |
