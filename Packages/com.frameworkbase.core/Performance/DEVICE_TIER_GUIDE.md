# 设备分级 / 画质自适应使用指南

## 解决什么问题

不分级，就只有一套画质默认值：低端机卡成盘 + OOM 被系统杀，高端机白白闲置性能。
本模块启动时把本机粗分**低/中/高三档**，自动映射到项目 Quality Level，并把档位暴露给
业务按档取值（粒子密度、分辨率缩放、可选特效开关）。

## 组成

| 文件 | 职责 |
| --- | --- |
| `DeviceTierClassifier` | 纯逻辑分级器（可单测）：内存/显存/核数 → 三档 |
| `DeviceTierService` | 静态服务：读 SystemInfo 分级、映射 QualitySettings、玩家覆盖持久化 |

## 分级规则（刻意保守）

- **任一已知维度踩低端线即判低端**（默认 ≤3GB 内存或 ≤1GB 显存）。误判低只是画质保守，
  误判高是 OOM 和卡顿投诉——不对称代价决定规则偏向。
- **判高端要求内存已知且所有已知维度达标**（默认 ≥6GB 内存、≥2GB 显存、≥8 核；
  取不到的维度不参与否决）。内存未知封顶中端。
- 阈值经 `DeviceTierThresholds` 按目标市场调整；线上校准依据是 perf_window 大盘
  按 `tier` 分组后的实测卡顿率——分级不追求先验精确，追求可被数据修正。

## 画质映射

低端→Quality Level 0、高端→最高档、中端→中间档（向下取整）。
前提：项目 Quality Levels 按从低到高排布（Unity 默认约定）。
Inspector 上 `_autoQualityByDeviceTier` 可关掉映射（只分级不动画质），
适合项目自己管理画质但仍想要 `tier` 维度的场景。

## 业务接入

```csharp
// 按档取值
int particleBudget = DeviceTierService.Tier switch
{
    DeviceTier.Low  => 50,
    DeviceTier.High => 400,
    _               => 150,
};

// 设置界面：展示推荐档 + 玩家手动选档（持久化，重启保持）
var recommended = DeviceTierService.AutoTier;
DeviceTierService.SetOverride(DeviceTier.High);  // 玩家选"高"
DeviceTierService.SetOverride(null);             // 玩家选"自动"
```

## 与 APM 的联动

`perf_window` 事件自带 `tier` 字段。大盘按 tier 分组看卡顿率：
某档位卡顿率显著高于相邻档位 → 该档阈值划错或该档画质配置过重，用数据回调阈值。
