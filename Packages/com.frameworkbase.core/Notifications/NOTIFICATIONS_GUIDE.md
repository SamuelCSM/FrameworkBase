# 本地通知排程使用指南

## 解决什么问题

体力满/签到重置/活动开抢的**本地提醒**是拉回活跃的标配。没有框架层排程，各业务
直接调平台 API 的结果是：半夜弹通知吃差评、回前台通知栏还挂着"回来玩"、iOS 64 条
上限被随意挤爆。本模块把"注册什么"与"何时交给系统"分离，策略集中收口。

## 与远程推送的分工

- 本模块：**本地**通知（设备上定时触发，无需服务端）。
- `ISdkPushService`（Sdk 模块）：**远程**推送的权限申请与设备 token。
  权限没批时系统会静默丢弃本地排程，本模块调用依旧安全（不炸）。

## 组成

| 文件 | 职责 |
| --- | --- |
| `LocalNotificationPlanner` | 纯逻辑注册表+结算（去重/过期过滤/免打扰平移/上限裁剪，可单测） |
| `ILocalNotificationBackend` | 平台后端抽象；默认 `NullLocalNotificationBackend` 只打日志 |
| `LocalNotifications` | 门面：切后台结算排程、回前台全部取消 |
| `LocalNotificationRelay` | 生命周期接线（GameEntry 自动挂载，Inspector 可关） |

## 业务接入

```csharp
using Framework.Notifications;

// 游玩期间随时注册/更新（同 id 覆盖）——框架在切后台时才真正排程
LocalNotifications.Planner.Register(
    "energy_full", "体力满了", "回来清体力！",
    DateTimeOffset.Now.AddMinutes(minutesToFull));

// 状态失效就注销（玩家刚消耗了体力）
LocalNotifications.Planner.Unregister("energy_full");

// 登出/切账号清空
LocalNotifications.Planner.Clear();

// 免打扰：23:00 ~ 次日 8:00 的提醒平移到早上 8 点（宁可晚提醒不可吵醒）
LocalNotifications.Planner.SetQuietHours(23, 8);
```

## 生命周期语义（框架接管，业务不用管）

- **切后台/退出**：先取消旧排程，再按当前注册表结算重排——排程永远反映最新游戏状态。
- **回前台**：全部取消并清通知栏——玩家人在游戏里，还弹"回来玩"是低级错误。
- 后端异常被隔离（暂停回调里抛异常平台行为未定义）。

## 原生后端接入（扩展包）

主干只有日志兜底。原生实现（Unity Mobile Notifications / 厂商通道）进扩展包，
与 ICrashBackend/ISdkProvider 同款注入模式：

```csharp
[RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
static void Install() => LocalNotifications.SetBackend(new MobileNotificationBackend());
```

后端只需实现两个方法：`ScheduleAll`（清单已升序、未过期、已裁剪到上限）与
`CancelAll`（取消未触发 + 清已投递）。
