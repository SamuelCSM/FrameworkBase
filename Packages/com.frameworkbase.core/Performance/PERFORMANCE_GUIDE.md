# 线上性能采样（APM）使用指南

## 解决什么问题

上线后"玩家说卡"，没有数据只能靠猜。崩溃有 Bugly、启动有 launch_run/launch_phase 埋点，
但**正式包的运行时性能（帧率/卡顿/内存）此前零采集**——`PerfHud` 只在 Editor / Development
Build 存在。本模块把运行时性能聚合成低频埋点事件，走 `AnalyticsManager` 既有管道上线上大盘。

## 组成

| 文件 | 职责 |
| --- | --- |
| `PerfWindowAggregator` | 纯逻辑窗口聚合器（可单测）：帧统计 + 卡顿计数 + 内存峰值 |
| `PerfSampler` | MonoBehaviour 接线层：喂帧/采内存/上报，GameEntry 自动挂载 |

## 事件口径：`perf_window`

每个窗口（默认 60s）一条，事件量恒定约 **1 条/分钟/玩家**：

| 字段 | 含义 |
| --- | --- |
| `window` | 会话内窗口序号（从 1 起；首窗口常含启动/加载噪声，大盘可单独看） |
| `duration_s` / `frames` | 窗口实际时长与帧数 |
| `avg_fps` | 窗口平均 FPS |
| `worst_ms` | 窗口最差单帧耗时——均值掩盖卡顿，最差帧才是玩家体感 |
| `jank` | 卡顿帧数（帧耗时 ≥ 100ms，含严重卡顿帧） |
| `severe_jank` | 严重卡顿帧数（≥ 500ms，体感"冻住"） |
| `managed_peak_mb` / `native_peak_mb` | 托管堆 / Native 已分配内存的窗口峰值（5s 采样一次） |
| `gc_count` | 距上次上报的 GC(gen0) 次数增量 |
| `scene` | 窗口结算时的活动场景名 |

**阈值为什么是绝对值**：卡顿 100ms / 严重 500ms 不随目标帧率变化。大盘口径必须跨设备可比，
相对阈值会让高刷设备"更容易卡"，污染对比。阈值可经 `PerfWindowAggregator` 构造参数调整，
但除非有明确理由，别动线上口径——历史数据的可比性比"更精确"值钱。

## 大盘怎么看

- **卡顿率** = sum(jank) / sum(frames)，按机型/场景/版本分组——这是核心健康度指标。
- **冻结率** = sum(severe_jank) / sum(duration_s)（次/秒），飙升通常指向同步 IO / 大对象加载。
- **内存水位** = p95(native_peak_mb) 按机型分组，逼近该机型 OOM 阈值即预警。
- 与 `launch_phase` 联查：首窗口（window=1）恶化但后续正常 → 问题在启动/加载链路。

## 灰度与开关

- Inspector：GameEntry 上 `_enablePerfSampling`（默认开）。
- 运行时：`PerfSampler.Enabled`（静态），业务可按 RemoteConfig 灰度采样人群，
  例如只对 10% 会话开启：`PerfSampler.Enabled = hash(sessionId) % 10 == 0;`
- 隐私合规、批量上报、掉线缓存均复用 `AnalyticsManager` 既有行为，本模块不另建通道。

## 已处理的坑

- **切后台**：挂起期间时长不记账（当前半截窗口被丢弃），恢复首帧的巨额 deltaTime 跳过，
  不会污染严重卡顿计数。GC 增量按差分并入下一窗口，总量不失真。
- **采样自身开销**：内存 5s 读一次；上报每分钟一次（一次字典分配），帧内零额外分配。
