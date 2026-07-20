# 反作弊值类型使用指南

## 解决什么问题

GameGuardian 类工具的作弊路径：内存里搜"当前金币数"→ 改值 → 再搜确认，三轮就能锁定地址改数。
明文 `int`/`float` 对这类工具零防御。`AntiCheatInt` / `AntiCheatLong` / `AntiCheatFloat`
把真值**异或实例密钥**后存储（内存里搜不到明文），另存校验和——直改混淆字段会在下次读取时
校验失败并触发 `AntiCheat.TamperDetected`。

## 防护边界（务必先读）

- **提高门槛，不是不可破**：注入级作弊（hook 读写路径、改代码段）挡不住。强对抗接专业方案
  （厂商反作弊 SDK），本类型是框架自带的基线。
- **权威数据以服务端为准**。本类型只保护"客户端自持的运行时数值"：单机数值、离线进度、
  本地表现层数值。联网玩法的货币/战力，客户端值本来就只是显示用。
- **整结构清零攻击**只能把值归零（default 态读 0），对金币/战力属自残，不在防护目标内。
- **持久化取 `Value` 明文**走加密存档（SaveManager AES）。混淆态含实例密钥,序列化落盘后
  下次进程读不回来，也别给混淆字段做序列化支持——那会把密钥一起落盘,等于白混淆。

## 用法

```csharp
using Framework.Security;

// 与原生类型隐式互转，声明处换类型即可
AntiCheatInt gold = 100;
gold += 50;                       // 算术照写
int display = gold;               // 读取自动校验

AntiCheatLong exp = 9_000_000_000L;   // 会溢出 int 的用 Long
AntiCheatFloat critRate = 0.25f;      // float 按位混淆，往返无损

// 篡改上报（启动时挂一次）：埋点 + 按项目策略处置（标记账号/踢下线）
AntiCheat.TamperDetected += typeName =>
    GameEntry.Analytics?.Track("anticheat_tamper", new Dictionary<string, object>
    {
        { "type", typeName },
    });
```

## 什么值得包

**值得**：金币/钻石余额、体力、离线收益进度、本地判定用的关键参数（暴击率、移速上限）。
**不值得**：帧率、临时局部变量、每帧高频读写的物理量（混淆有每次读写一次异或+乘法的开销，
热路径慎用）、服务端权威且客户端只显示的值（改了也没用）。

## 实现要点

- 实例密钥来自无锁计数器混时钟（`AntiCheat.NextKey`），同值不同实例混淆结果不同——
  搜到一个地址不等于搜到全部。
- 校验和双输入（值+密钥），直改混淆字段或校验和字段任意一个都失配。
- `TamperDetected` 在读值线程同步触发，回调里别做重活；无订阅时静默返回解码值
  （fail-open：框架不替业务决定处置策略）。
