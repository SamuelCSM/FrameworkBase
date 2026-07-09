# FrameworkBase Experiment（A/B 实验）

在主干已成熟的 **RemoteConfig**（实验配置下发）+ **Analytics**（曝光埋点）之上，补齐
「稳定分组 + 曝光追踪」——这是主干灰度放量（版本级、按设备分桶）补不上的**用户级 A/B 对照实验**能力：
运营要用 A/B 验证数值 / 付费点 / 引导，需要稳定分组 + 曝光事件与埋点打通做显著性分析。

> 主干 `com.frameworkbase.core` 不含实验逻辑；本包是其一个扩展。

## 三件事

| 关注点 | 谁负责 |
|---|---|
| 实验定义（分组权重 / 开关 / 盐） | 运营在后台维护 → RemoteConfig 的 `experiments` 键下发 |
| 稳定分配（同一玩家永远同一组） | `ExperimentAssigner`（FNV-1a 稳定哈希 + 权重分桶，纯逻辑可单测） |
| 曝光追踪（谁看到了哪个变体） | 首次取分组时打 `experiment_exposure` 埋点（本会话去重） |

## 用法

```csharp
// 启动早期设分配单元（登录前用设备 ID，登录后可切用户 ID）
Experiments.Instance.SetUnitId(userId);

// 取分组——首次取该实验即自动打曝光埋点
if (Experiments.Instance.IsInVariant("shop_layout", "v1"))
    ShowNewShop();
else
    ShowOldShop();

// QA / 联调强制某组
Experiments.Instance.SetOverride("shop_layout", "v1");

// 只想预览分组、不打曝光
string v = Experiments.Instance.PeekVariant("shop_layout");
```

## 远程配置格式（RemoteConfig 的 `experiments` 键）

```json
{
  "experiments": [
    {
      "key": "shop_layout",
      "enabled": true,
      "salt": "",
      "variants": [
        { "name": "control", "weight": 50 },
        { "name": "v1", "weight": 50 }
      ]
    }
  ]
}
```

- `weight` 是相对权重，不必凑 100；总和为 0 或 `enabled=false` 时一律回落 `control`。
- 改 `salt` 等于**重开一轮**（重洗分组）；调 `weight` 即放量，运营改完下次 Fetch 即生效、无需发版。

## 可测 / 可换

- 分配是纯函数（`ExperimentAssigner.Assign`），逻辑单测覆盖分布 / 稳定性 / 盐值重洗。
- 定义来源 `IExperimentConfigSource` 与曝光出口 `IExposureSink` 都可注入：默认走
  RemoteConfig / Analytics，测试注入假实现，也可换成自建后端。

## 契约要点

- **稳定单元**：分配锚在 `SetUnitId` 给的单元上；某实验一旦曝光就别再切单元，否则同一玩家跨单元漂移。
- **曝光即一次**：`GetVariant` / `IsInVariant` 首次取某实验才打曝光（本会话去重），`PeekVariant` 不打。
- **失败不反噬**：曝光埋点异常被吞并转 GameLog，不影响业务取分组。
