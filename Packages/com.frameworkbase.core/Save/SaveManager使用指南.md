# SaveManager 使用指南

## 概述

`SaveManager` 是框架的本地数据持久化层，采用三层架构：

> **账号隔离**：每个账号有独立的存档目录，同一设备多账号数据完全隔离。  
> 登录后调用 `SetCurrentUser(cid)` 即可，所有存档自动走该账号目录。

| 层级 | 适用数据 | 实现 |
|------|---------|------|
| **SaveManager.SaveAsync** | 游戏存档、玩家进度、背包等结构化数据 | JSON + AES-128 加密 + SHA-256 完整性校验 |
| **SaveManager.SetPref** | 设置项（音量、语言、画质等） | PlayerPrefs（轻量 K-V） |
| **SQLite（可选）** | 背包、邮件等有查询需求的数据 | 复用框架已有 SQLiteHelper |

---

## 账号隔离（多账号同设备）

同一设备登录不同 CID 账号时，存档自动隔离在各自目录下：

```csharp
// 登录成功后调用一次（CID 会被自动净化，防止路径注入）
SaveManager.Instance.SetCurrentUser("10001");

// 之后所有存取自动走账号目录
await SaveManager.Instance.SaveAsync(playerData);
// → saves/u_10001/PlayerData_0.sav

// 切换账号
SaveManager.Instance.SetCurrentUser("20002");
await SaveManager.Instance.LoadAsync<PlayerData>();
// → saves/u_20002/PlayerData_0.sav（读的是另一个账号的数据）

// 退出登录，切回 guest 目录
SaveManager.Instance.ClearCurrentUser();
```

**PlayerPrefs 是全局的，不区分账号**（适合音量、语言等设备级别设置）。  
如果需要账号级别的设置，把数据放进继承 `SaveData` 的类里存档文件中。

---

## 快速上手

### 1. 定义存档数据类

```csharp
using System;
using Framework.Save;

[Serializable]
public class PlayerData : SaveData
{
    public string nickname  = "";
    public int    level     = 1;
    public float  coins     = 0f;
    public long   playTime  = 0L;   // 累计游戏时长（秒）
}
```

> **注意**：`[Serializable]` 是必须的，`SaveManager` 内部使用 `JsonUtility` 序列化。
> `JsonUtility` 不支持 Dictionary，如需复杂数据结构请使用列表或自定义封装。

---

### 2. 写档

```csharp
// 推荐：await 确保写入完成
var data = new PlayerData { nickname = "玩家001", level = 5 };
await SaveManager.Instance.SaveAsync(data);

// 多档位（slot 0 是默认档，slot 1/2 可用于备用存档）
await SaveManager.Instance.SaveAsync(data, slot: 1);

// 不关心写入时机时可用同步触发（内部 Forget）
SaveManager.Instance.Save(data);
```

---

### 3. 读档

```csharp
// 无存档时自动返回 new PlayerData()，不会抛异常
var data = await SaveManager.Instance.LoadAsync<PlayerData>();

Debug.Log($"昵称: {data.nickname}, 等级: {data.level}");

// 多档位
var slot1 = await SaveManager.Instance.LoadAsync<PlayerData>(slot: 1);
```

---

### 4. 检查 / 删除存档

```csharp
// 是否存在存档
if (SaveManager.Instance.HasSave())
{
    Debug.Log("有存档");
}

// 删除指定槽位（同时删除备份）
SaveManager.Instance.DeleteSave(slot: 0);

// 删除全部存档
SaveManager.Instance.DeleteAllSaves();
```

---

### 5. 玩家设置（PlayerPrefs）

Key 常量统一定义在 `PlayerSettings`，不要在代码中写裸字符串：

```csharp
using Framework.Save;

// 写设置
SaveManager.Instance.SetPref(PlayerSettings.MusicOn, false);
SaveManager.Instance.SetPref(PlayerSettings.MusicVolume, 0.8f);
SaveManager.Instance.SetPref(PlayerSettings.Language, "en-US");

// 读设置（带默认值）
bool  musicOn = SaveManager.Instance.GetPref(PlayerSettings.MusicOn, defaultValue: true);
float vol     = SaveManager.Instance.GetPref(PlayerSettings.MusicVolume, defaultValue: 1f);
string lang   = SaveManager.Instance.GetPref(PlayerSettings.Language, defaultValue: "zh-CN");

// 检查 / 删除
bool exists = SaveManager.Instance.HasPref(PlayerSettings.Language);
SaveManager.Instance.DeletePref(PlayerSettings.Language);
```

新增 Key 时在 `PlayerSettings.cs` 中添加常量：

```csharp
public static class PlayerSettings
{
    public const string MusicOn     = "pref_audio_music_on";
    public const string MusicVolume = "pref_audio_music_vol";
    // ... 按模块分组，命名规范：pref_{模块}_{含义}
}
```

---

## 存档版本迁移

当游戏更新后修改了存档数据结构，通过 `dataVersion` + `OnMigrate` 平滑过渡：

```csharp
[Serializable]
public class PlayerData : SaveData
{
    // v1 字段
    public string nickname = "";
    public int    level    = 1;

    // v2 新增字段
    public long   lastLoginTime = 0L;

    // 将 dataVersion 升为 2
    public new int dataVersion = 2;

    protected override void OnMigrate(int fromVersion)
    {
        if (fromVersion < 2)
        {
            // 旧存档没有 lastLoginTime，补一个合理默认值
            lastLoginTime = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        }
        // 未来 v3 时在这里继续加 if (fromVersion < 3) { ... }
    }
}
```

`SaveManager` 在读档后自动调用 `TryMigrate`，无需手动触发。

---

## 安全机制

| 机制 | 说明 |
|------|------|
| **AES-128-CBC 加密** | Key 由主密钥种子 + `Salt` 经 SHA-256 派生（默认种子 = `deviceUniqueIdentifier`），防止直接阅读存档 |
| **HMAC-SHA256 完整性校验** | 用独立派生的 MAC Key 做 encrypt-then-MAC；读档常数时间校验，被篡改时拒绝加载并 fallback 到备份。旧版裸 SHA-256 存档仍可读入，下次写档自动升级为 HMAC |
| **可插拔密钥来源** | 主密钥种子由 `ISaveKeyProvider` 提供，默认 `DeviceSaveKeyProvider`（绑定设备）。上云/跨设备时注入账号或服务端下发密钥的实现即可 |
| **原子写入** | 先写 `.tmp` 再重命名，防止写到一半崩溃导致存档损坏 |
| **自动备份** | 每次写档前把旧档备份为 `.sav.bak`，主档损坏时自动恢复 |

> **防作弊说明**：默认主密钥绑定设备 ID，不同设备的存档互不通用。
> HMAC 让本地篡改无法在不知 MAC Key 的情况下重算出合法完整性码，抬高了改档门槛；
> 但客户端密钥终究可被逆向提取，**重要数值（金币、等级）的最终校验必须在服务端完成**，客户端加密/签名只是防低门槛修改。

### 跨设备 / 上云存档

默认存档绑定设备，无法跨设备解密。若需跨设备，在**任何读写存档之前**（例如登录拿到服务端下发密钥后）注入自定义密钥来源：

```csharp
// 示例：用账号维度的稳定密钥（建议由服务端下发，勿用可公开推断的纯账号 ID）
SaveManager.Instance.SetSaveKeyProvider(new AccountSaveKeyProvider(serverIssuedKey));
```

> 注意：更换密钥来源会使此前用旧来源加密的存档无法解密，需配合迁移策略。

---

## 存档文件位置

```
Application.persistentDataPath/saves/
  u_10001/                      ← 账号 10001 的目录
    PlayerData_0.sav            ← 主存档（AES 加密 JSON）
    PlayerData_0.sav.bak        ← 自动备份
    ActivityData_0.sav
    ActivityData_0.sav.bak
  u_20002/                      ← 账号 20002 的目录（完全隔离）
    PlayerData_0.sav
    ...
  guest/                        ← 未登录时的目录
    PlayerData_0.sav
```

各平台路径：

| 平台 | persistentDataPath |
|------|--------------------|
| Android | `/data/data/{包名}/files/` |
| iOS | `{沙盒}/Documents/` |
| Windows | `%AppData%\..\LocalLow\{Company}\{Product}\` |
| macOS | `~/Library/Application Support/{Company}/{Product}/` |

---

## 常见问题

### Q: `JsonUtility` 不支持 Dictionary，怎么存 Key-Value 数据？

用 `[Serializable]` 包装类或列表：

```csharp
[Serializable]
public class StringPair { public string key; public string value; }

[Serializable]
public class PlayerData : SaveData
{
    public List<StringPair> customFlags = new List<StringPair>();
}
```

### Q: 存档文件在设备上能被玩家直接修改吗？

AES 加密后文件是二进制密文，玩家无法直接编辑。SHA-256 校验确保即使文件被修改也会被检测到并拒绝加载（回退到备份档）。

### Q: 如何在编辑器中查看/重置存档？

```csharp
// 编辑器快捷方式（可加到 MenuItem）
[MenuItem("Debug/Delete All Saves")]
static void ClearSaves() => SaveManager.Instance.DeleteAllSaves();

[MenuItem("Debug/Delete All Prefs")]
static void ClearPrefs() => SaveManager.Instance.DeleteAllPrefs();
```

或者直接删除 `Application.persistentDataPath/saves/` 目录。

---

## 相关文件

```
Packages/com.frameworkbase.core/Save/
  AesHelper.cs        ← AES-128-CBC 加解密（内部使用）
  SaveData.cs         ← 存档基类，重写 OnMigrate 处理版本迁移
  SaveManager.cs      ← 核心管理器，Save/Load/PlayerPrefs 统一入口
  PlayerSettings.cs   ← PlayerPrefs Key 常量表
```
