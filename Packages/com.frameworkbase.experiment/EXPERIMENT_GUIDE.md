# A/B 实验使用指南（EXPERIMENT_GUIDE）

本指南讲清「怎么用、怎么配、别踩哪些坑」。速览请看 [README](README.md)。

---

## 一、这是什么，什么时候用

主干已有的**灰度放量**是**版本级、按设备分桶**的（`version.json` 的 `GrayPercent`）——用来控制「新包/新资源放给多少比例设备」。它<b>不是</b>对照实验：没有对照组、没有曝光埋点、不按用户分组。

本包补的是**用户级 A/B 对照实验**：把玩家稳定分成若干组（对照 / 实验），每组看到不同实现，并打**曝光埋点**，好让数据侧按组比留存 / 付费 / 时长做**显著性分析**。

**该用它的场景**：验证商店布局、付费礼包定价点、新手引导版本、按钮文案等「想用数据证明哪个更好」的改动。
**不该用它的场景**：纯粹的「新功能放量开关」——那是功能开关 `RemoteConfig.IsFeatureEnabled`，不需要分组与曝光。

---

## 二、四个概念

| 概念 | 说明 |
|---|---|
| **实验（experiment）** | 一个待验证问题，有唯一 `key`（如 `shop_layout`）。 |
| **变体（variant）** | 实验的一种实现，有 `name`（如 `control` / `v1`）与相对 `weight`。 |
| **分配单元（unit）** | 分组锚点：用户 ID 或设备 ID。同一单元对同一实验永远同一组。 |
| **曝光（exposure）** | 「玩家实际被分到并使用了某变体」的埋点事件，数据归因的锚。 |

---

## 三、快速上手（三步）

```csharp
using Framework.Experiment;

// 1) 启动早期设分配单元：
//    - 若实验要在登录前就生效（如启动页 A/B），用设备 ID；
//    - 若都在登录后，用稳定的用户 ID（跨设备一致，最推荐）。
Experiments.Instance.SetUnitId(userId);

// 2) 在分叉点取分组——首次取该实验即自动打一次曝光埋点：
if (Experiments.Instance.IsInVariant("shop_layout", "v1"))
    OpenNewShop();
else
    OpenOldShop();

// 或直接拿变体名做多分支：
switch (Experiments.Instance.GetVariant("onboarding"))
{
    case "short": RunShortOnboarding(); break;
    case "long":  RunLongOnboarding();  break;
    default:      RunDefaultOnboarding(); break; // control / 未命中兜底
}
```

就这些。实验定义、放量比例、开关全在远程配置里，由运营维护、无需发版。

---

## 四、远程配置格式

实验清单放在 **RemoteConfig 的 `experiments` 键**下（一段 JSON）：

```json
{
  "experiments": [
    {
      "key": "shop_layout",
      "enabled": true,
      "salt": "",
      "variants": [
        { "name": "control", "weight": 50 },
        { "name": "v1",      "weight": 50 }
      ]
    },
    {
      "key": "onboarding",
      "enabled": true,
      "salt": "2026q3",
      "variants": [
        { "name": "control", "weight": 80 },
        { "name": "short",   "weight": 10 },
        { "name": "long",    "weight": 10 }
      ]
    }
  ]
}
```

| 字段 | 语义 |
|---|---|
| `key` | 实验标识，业务与埋点按此对齐。 |
| `enabled` | `false` 时该实验一律回落 `control`（用于紧急关停）。 |
| `salt` | 分桶盐。**改盐 = 重开一轮**（重新洗牌分组）；不改盐则玩家分组恒定。 |
| `variants[].name` | 变体名；留空视作 `control`。 |
| `variants[].weight` | **相对**权重，不必凑 100。总和为 0 时回落 `control`。 |

**放量**：把 `v1` 的 `weight` 从 `50` 调到 `80` 即扩大实验组；运营改完，客户端下次 `RemoteConfig` Fetch 生效。
**注意**：单纯调权重会让**部分原对照组玩家漂移到实验组**（分桶边界移动）。若要「已入组的人保持不变、只对新增比例放量」，应改用递增 `weight` 且不改 `salt`——本包按累积权重分桶，扩大某段只会把边界外的人纳入，已在段内的人不动。

---

## 五、曝光埋点

- **何时打**：`GetVariant` / `IsInVariant` 在**本会话首次**取某实验时，打一条 `experiment_exposure`（属性 `experiment` + `variant`）到 Analytics。同一会话再取不重复打。
- **为什么要**：数据侧只有拿到「谁在哪个变体」才能把后续留存/付费按组归因。**没有曝光事件，实验就等于白做。**
- **不想打点时**：用 `PeekVariant(key)` 只解析分组、不打曝光（调试面板 / 预热用，避免污染曝光数据）。
- **失败不反噬**：曝光埋点异常会被吞并转 `GameLog`，不影响业务取到分组。

数据侧用法：把 `experiment_exposure` 作为分组维度，join 到留存/付费事件上，按 `variant` 分桶比指标、算显著性。

---

## 六、分配为什么稳定

`ExperimentAssigner.Assign(unit, def)` = `FNV-1a( unit:key:salt ) % 总权重` → 落到累积权重区间对应的变体。

- 用主干 `StableHash`（FNV-1a），**不是** `string.GetHashCode`（后者跨进程/跨平台不稳定，禁用于分桶）。
- 同一 `(unit, key, salt)` 永远同一结果——跨会话、跨设备、跨版本一致，这是 A/B 归因成立的地基。
- 纯函数、无 Unity 依赖，逻辑有单测覆盖（分布 / 稳定性 / 盐重洗）。

---

## 七、QA 与调试

```csharp
// 强制某实验落指定变体（本机联调 / 提测走查）
Experiments.Instance.SetOverride("shop_layout", "v1");

// 撤销强制，恢复正常分配
Experiments.Instance.ClearOverride("shop_layout");

// 只看当前分组、不打曝光
string v = Experiments.Instance.PeekVariant("shop_layout");
```

---

## 八、可测试 / 可替换

定义来源与曝光出口都是接口，默认走 RemoteConfig / Analytics，可注入替换：

```csharp
// 自定义接入 / 单测：注入假来源与假 sink
var mgr = new ExperimentManager(myConfigSource, myExposureSink);
Experiments.SetInstance(mgr);
```

- `IExperimentConfigSource`：换成从别处（自建实验平台）读定义。
- `IExposureSink`：换成把曝光打到别的埋点通道。

---

## 九、常见坑

1. **忘了 `SetUnitId`**：不设时 unit 为空串，所有人落同一桶 → 分组失去意义。启动早期务必设。
2. **中途切 unit**：某实验已曝光后再换分配单元（如从设备 ID 切用户 ID），会让同一玩家跨单元漂移到另一组，污染实验。要切就在**任何实验曝光之前**切。
3. **误用曝光**：在一个高频 Update 里 `GetVariant` 期望它每帧打点——不会，本会话只打一次；要每次都打点得自己另打埋点。反之，别在「只是想看看分组」的地方用 `GetVariant`（会打曝光），那种用 `PeekVariant`。
4. **改 `salt` 的后果**：等于重开一轮、全员重洗。想「延续同一批人」就别动 salt。
5. **`control` 兜底**：实验未配置 / `enabled=false` / 权重全 0 / key 打错，一律返回 `control`——所以 `control` 分支必须是「安全的老实现」。
6. **依赖 `Framework.Kernel`**：本包 asmdef 引用了 `Framework.Kernel`（`GameEntry` 返回的 Manager 基类在 Kernel），换环境接入时勿删。
