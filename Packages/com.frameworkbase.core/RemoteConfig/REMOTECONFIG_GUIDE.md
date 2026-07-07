# 远程配置 / 功能开关使用指南

## 定位

不发包改配置、按设备灰度放量新功能。管道负责：默认值合并、磁盘缓存
（last-known-good）、类型化取值、功能开关判定（含设备分桶灰度与最低版本门控）。

三方平台（Firebase Remote Config 等）对接放扩展包：实现 `IRemoteConfigBackend`
并 `SetBackend` 注入；框架主干不含厂商 SDK。

## 业务侧用法

```csharp
// 组合根启动早期注册代码默认值（断网首装的行为底线，每个会读的键都要有默认值）
GameEntry.RemoteConfig.SetDefaults(new Dictionary<string, object>
{
    { "matchmaking_timeout_sec", 30 },
    { "new_lobby_ui", false }
});

// 读配置（三层回退：拉取值 → 默认值 → 兜底参数）
int timeout = GameEntry.RemoteConfig.GetInt("matchmaking_timeout_sec", 30);

// 功能开关
if (GameEntry.RemoteConfig.IsFeatureEnabled("new_lobby_ui"))
    OpenNewLobby();

// 需要硬门控时手动拉取（LaunchFlow 已在启动时并行拉过一次，通常不用管）
bool fetched = await GameEntry.RemoteConfig.FetchAndActivateAsync();
```

## 配置负载格式

端点返回顶层 JSON 对象（可以是配置服务，也可以是 CDN 上的静态文件）：

```json
{
  "matchmaking_timeout_sec": 30,
  "maintenance_notice": "",
  "new_lobby_ui": { "enabled": true, "rollout": 30, "min_version": "1.2.0" },
  "hard_kill_switch_x": false
}
```

功能开关两种写法：

| 写法 | 语义 |
|---|---|
| `true` / `false` | 全量开 / 全量关 |
| `{ "enabled": …, "rollout": …, "min_version": … }` | 条件开关，逐项过滤 |

条件开关判定顺序：`enabled=false` → 关；当前版本 < `min_version` → 关；
设备稳定分桶 ≥ `rollout` 百分比 → 关；全过 → 开。
分桶对 `设备号:键名` 做 FNV-1a 稳定哈希，同一设备同一键结果永远一致，
放量从 10% 上调到 50% 时已命中设备保持命中（不会开了又关）。

## 行为约定

- **失败永不破坏现值**：拉取失败 / 解析失败保留当前值；远端配置挂了客户端照常跑。
- **磁盘缓存**：拉取成功即整体落盘 `remote_config_cache.json`；下次启动先用上次的值。
- **启动接入**：LaunchFlow 开始时并行发起一次拉取（不阻塞启动）；本次拉取值一般在
  登录前激活，来不及时先用缓存/默认值，`FetchedThisSession` 可查询是否已是新值。
- **服务端定向**：HTTP 后端请求附带 `device_id / user_id / app_version / channel / env`
  查询参数，配置服务可按此下发不同内容；静态 CDN 文件忽略参数，定向全靠开关字段。

## 热更灰度放量（version.json）

`version.json` 支持 `GrayPercent` 字段（0 或缺省 = 全量；1~99 = 灰度百分比）：
未命中分桶的设备把本次 version.json 当作"无更新"，放量上调后自动纳入。
分桶盐含目标版本号——每次新发布重新洗牌，避免同一批设备永远当小白鼠；
同一发布内上调放量时已命中设备保持命中。判定在 `VersionManager.IsDeviceInGrayRollout`。
