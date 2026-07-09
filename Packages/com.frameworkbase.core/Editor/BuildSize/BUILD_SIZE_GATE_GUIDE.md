# 包体回归门禁使用指南

## 定位

拦截"包体一版版胖"这类**只在运营中后期才痛、且难回溯"是哪次胖的"**的隐患。
把每次构建产物的尺寸与**基线**比对，增长超阈即告警/阻断。属**构建后置**检查（需已有产物目录），
独立于不依赖出包的资源门禁 [`CiGate`](../CiGate.cs)。

**核心可完全自测**：裁决引擎 `BuildSizeGate.Evaluate` 是纯函数（基线+当前→裁决），
目录扫描 / 基线读写用临时目录单测，不依赖真实出包。

## 组成

| 类型 | 职责 | 可单测 |
|---|---|---|
| `BuildSizeGate.Evaluate` | 纯裁决：比对两份快照按策略给 Pass/Warn/Fail | ✅ 纯函数 |
| `BuildSizeSnapshotIO` | 扫描产物目录成快照 / 基线 JSON 读写 | ✅ 临时目录 |
| `BuildSizePolicy` | 阈值策略（总量+单类，双阈） | — |
| `BuildSizeCiGate` | batchmode 入口 + "更新基线"菜单 | 需真实 Unity（薄胶水） |

## 阈值策略

只查**增长**（缩小无害）。两道阈：

| 策略项 | 默认 | 含义 |
|---|---|---|
| `maxTotalGrowthPercent` | 10% | 总量增长超此百分比 → 违规 |
| `maxTotalGrowthBytes` | 关闭 | 总量增长超此绝对字节 → 违规（>0 启用） |
| `maxEntryGrowthPercent` | 25% | 单条目增长超此百分比 → 违规 |
| `entryMinBytesToCheck` | 64KB | 条目当前体积低于此值跳过百分比检查（防小文件抖动） |
| `failOnNewEntry` | false | 出现基线没有的新条目是否算违规 |
| `warnOnly` | false | true = 只告警不阻断（Warn 而非 Fail） |

## 基线约定

基线是一份 JSON（默认 `Tools/ci/build-size-baseline.json`），记录一次"认可的"构建的总量与分类明细。

- **基线应提交进仓库**——CI 对比的是仓库里那份，"包体涨了"的评审等于"基线更新"的评审。
- **首次运行无基线**：门禁直接 Pass 并落盘当前为基线。
- **主动更新基线**：跑门禁时加 `-buildSizeUpdateBaseline`，或用编辑器菜单 **Framework/发布/更新包体基线**（弹框选产物目录）。

## batchmode 用法

构建产出 bundle / 整包后调用：

```bat
Unity.exe -batchmode -nographics -projectPath <工程根> ^
  -executeMethod Framework.Editor.BuildSize.BuildSizeCiGate.RunBuildSizeGate ^
  -buildSizeDir <产物目录> [-buildSizeBaseline <基线json>] ^
  [-buildSizeLabel v1.2.0] [-buildSizeWarnOnly] [-buildSizeUpdateBaseline] ^
  -logFile Logs/ci/build-size-gate.log
```

结论以 ASCII 哨兵 `[BuildSizeGate] GATE_RESULT exit=N` 为准（batchmode 进程退出码不可靠，与资源门禁同款处理）。

## 接进 run-ci

`run-ci.ps1` 加了 `-BuildSizeDir`，**仅传入时才跑**（本仓库 run-ci 不出包，默认跳过）：

```powershell
# 首次建基线
.\Tools\ci\run-ci.ps1 -SkipPlayMode -SkipAssetGate -BuildSizeDir "<产物目录>" -BuildSizeUpdateBaseline
# 之后每次比对基线（超阈阻断）
.\Tools\ci\run-ci.ps1 -SkipPlayMode -SkipAssetGate -BuildSizeDir "<产物目录>"
# 只告警不阻断
.\Tools\ci\run-ci.ps1 -BuildSizeDir "<产物目录>" -BuildSizeWarnOnly
```

run-ci 会把相对产物目录解析成绝对路径再传给 Unity（batchmode 的 CWD 不确定）。

> **未接进 GitHub `ci.yml`**：云端 CI 目前不构建 player（无产物可查），接了也只会一直跳过。
> 待有出包流水线时，在出包步骤后加一步调 `RunBuildSizeGate` 即可。

## 代码里直接用（写工具 / 自定义流程）

```csharp
var current  = BuildSizeSnapshotIO.FromDirectory(bundleDir, label: "v1.2.0");
var baseline = BuildSizeSnapshotIO.LoadBaseline("Tools/ci/build-size-baseline.json");
var verdict  = BuildSizeGate.Evaluate(baseline, current, new BuildSizePolicy { maxTotalGrowthPercent = 8 });

if (verdict.IsBlocking)
    foreach (var v in verdict.Violations)
        Debug.LogError(v.reason);
```
