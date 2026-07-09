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

---

## 接入 SaveManager 的设计（决策已定，待第一个真实后端落地时照此开缝）

> 本节固化"云同步如何接进 `SaveManager`"的架构决策。**当前刻意不实现**：这条集成缝没有外部消费者
> ——业务只调 `SaveManager.SaveAsync`，接了同步该 API 也不变；它纯是 SaveManager 内部管线，
> `SaveManager` 又是唯一调用者，等有真实 `ICloudSaveBackend` 时再接成本极低（改内部不改门面）。
> 现在建 = 挂个 NoOp 的投机接线（YAGNI）。对比 `ICloudSaveBackend` 有外部消费者（厂商包/测试按它写契约），
> 故先建。

### 两条缝在两个高度，别合并

```
业务  →  SaveManager.SaveAsync / LoadAsync            ← 公开 API，接同步也不变
              ├─ ISaveKeyProvider  (密钥源，已存在)
              ├─ AES+HMAC → SaveEnvelope 字节          (加密，已存在)
              └─ ISaveSync         ← 待开的缝：SaveManager 面向的"同步策略"
                     └─ NoOpSaveSync (默认关闭，行为不变)
                     └─ CloudBackedSaveSync
                            └─ CloudSaveSync (本包，编排+纯决策)
                                   └─ ICloudSaveBackend  ← 传输缝(哑字节存储)，已存在
```

- `ICloudSaveBackend` = **传输缝**：字节物理上去哪，厂商包实现。
- `ISaveSync` = **SaveManager 面向的策略缝**：`SaveManager` 只知道"有个策略要通知"，**不知道云存在**。
  合并两者会让 `SaveManager` 硬依赖云命名空间，破坏"存档核心不知道云"。

### 缝开在"封包字节层"，不在明文层、不在裸文件层

```
SaveData(明文)
  → JSON → AES+HMAC → SaveEnvelope 字节 → 原子写盘+备份
                        ▲
                        └── 缝在这里：云拿到的就是磁盘上那份加密+签名 blob
```

- **开在加密之上（SaveData 层）**：云端见明文，丢掉"后端永不见明文"，且重复造序列化。✗
- **开在封包字节层**：后端保持哑存储、加密留在框架、完整性码随 blob 走。✓（`CloudSaveRecord.Payload` 即此层）
- **开在裸文件层**：同步自己做文件 IO，和 `SaveManager` 的 per-file 原子写锁打架。✗

### 与现有加密的衔接：正交，加密代码零改动

- **Push（写后上行）**：本地封包字节 → 云端**逐字节原样**。
- **Pull（下行）**：云端封包字节 → 本地 `.sav` **逐字节原样写入** → 走**现有** `LoadAsync` 校验 HMAC→解密→迁移。

拉回的 blob 是另一台设备加密+签名的合法封包，流过与本地档**同一条** verify/decrypt/migrate 路径，
`AesHelper` / `SaveEnvelope` 解析 / `RunMigrationFrom` **全不用改**。

> **唯一硬约束**：密钥源须**账号绑定**（`SetSaveKeyProvider` 传服务端下发的账号密钥，非默认 device-bound），
> 否则 A 设备封包 B 设备解不开。这是约束，不是改动。

### 落地时只动现有代码两处

1. **`SaveEnvelope` 加同步计数器字段 `s`**（legacy 无此字段→0）。现有 `v` 是 `dataVersion`（**schema 版本**），
   冲突判定要的是**同步版本**（每次成功写档 +1）——两个概念。放进封包最干净：与写档原子、单一真相源，
   填充 `CloudSaveMetadata.Version`。
2. **`SaveManager` 加**：`ISaveSync` 字段 + `SetSaveSync()`；`SaveAsync` 成功后通知 push；
   一个**显式** `ReconcileAsync<T>(slot)`；一个内部"写裸封包字节"路径（pull 落盘用，复用 `AtomicWriteText`+备份，
   **跳过再加密**——字节已是封包）。

### 生命周期钩子：push 自动、pull 显式

- **Push**：`SaveAsync` 写盘成功后 fire-and-forget 通知（关键档口可 await）。
- **Pull / Reconcile**：**只在登录 / 回前台显式调**，**绝不塞进每次 `LoadAsync`**——
  LoadAsync 频繁调用拉云太吵；更致命的是会话中途自动拉会**覆盖玩家正在改的内存态**。
  会话内本地权威，同步只在边界做。

### 落地前必须认下的三个利刃

1. **跨 schema 版本**：同步可能把**新版档投给旧客户端**（新包写 `v2`，旧包拉到）。
   现有 `RunMigrationFrom` 只处理 `saved < current` 前向迁移；`saved > current` 是降级，会被强行按当前版读、
   `dataVersion` 压回、下次写档丢字段。**必须 min-app-version 门控同步，或拒载 `envelope.v > current` 的封包**。
2. **写后 push 失败**：需重试/离线队列（`SyncAsync` 返回 `Offline` 即为此留的钩子）。
3. **account-bound key**（见上）。

## 相关类型

| 类型 | 职责 |
|---|---|
| `CloudSaveSync` | 编排器：注入后端、`Decide` 决策、`SyncAsync` 执行。 |
| `ICloudSaveBackend` | 云后端缝（哑存储，按 key 存取字节）。 |
| `CloudSaveRecord` / `CloudSaveMetadata` | 存档条目 / 同步元数据。 |
| `CloudSyncDirection` / `CloudSyncResult` | 同步动作 / 同步结果。 |
| `NoOpCloudSaveBackend` / `InMemoryCloudSaveBackend` | 默认关闭兜底 / 测试 Mock。 |
