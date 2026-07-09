# Changelog

本包遵循 [语义化版本](https://semver.org/lang/zh-CN/)。`0.x` 为孵化期。

## [0.1.0] - 2026-07-09

### 新增

- A/B 实验能力（用户级对照实验，补主干版本级灰度之外的一块）：
  - `ExperimentAssigner`：纯分配逻辑（FNV-1a 稳定哈希 + 权重分桶），同一
    `(unit, key, salt)` 跨会话 / 跨设备恒定，复用主干 `StableHash`。
  - `ExperimentManager` / `Experiments` 门面：解析变体 + 首次使用打曝光埋点（本会话去重）+
    QA 覆盖 + `PeekVariant`（不打点预览）。
  - `IExperimentConfigSource`（默认 `RemoteConfigExperimentSource` 读 `experiments` 远程键）、
    `IExposureSink`（默认 `AnalyticsExposureSink` 打 `experiment_exposure`）——均可注入，可单测 / 可换后端。
  - 单测：分配分布 / 稳定性 / 盐值重洗（Assigner）+ 解析 / 曝光去重 / 覆盖 / 预览（Manager），共 13 用例。
