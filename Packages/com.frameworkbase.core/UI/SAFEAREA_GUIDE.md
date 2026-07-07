# UI 安全区与多分辨率适配指南

## 两个问题，两个组件

| 问题 | 组件 | 作用 |
|---|---|---|
| 刘海/挖孔/圆角/Home 条遮挡 UI | `SafeAreaFitter` | 把 RectTransform 锚定到 `Screen.safeArea` |
| 异形宽高比设备上 UI 溢出/留黑边 | `CanvasScalerAutoMatch` | 按屏幕宽高比动态设置 CanvasScaler match |

## 安全区：两种接入方式（二选一）

**方式 A：逐 prefab 挂组件（默认，对存量项目零影响）**

在 UI prefab 的**内容根 Panel** 上挂 `SafeAreaFitter`：

```text
LoginWindow
├── Background      ← 不挂：全屏出血铺满，被圆角裁掉也无所谓
└── Content         ← 挂 SafeAreaFitter：按钮/文本避让刘海和 Home 条
```

各边避让可单独关闭（Edges 掩码）：底部沉浸式面板只避让顶部时取消 Bottom。

**方式 B：UIBootstrap 层级统一垫（新项目推荐）**

UIBootstrap Inspector 勾选 **Apply Safe Area To Layers**：每个 UILayer Canvas 下自动
垫一个 SafeArea 容器，`GetLayerRoot` 返回它——所有经框架打开的 UI 自动避让，
业务 prefab 什么都不用挂。

注意：开启会整体改变既有 UI 的可用区域，**存量项目先在真机 / Device Simulator
过一遍再开**；需要全屏出血的背景层窗口，业务可自行挂到 `GetLayerCanvas` 下。

## 分辨率适配：CanvasScalerAutoMatch

固定 `matchWidthOrHeight` 在异形比例设备上必翻车（21:9 带鱼屏 UI 溢出、4:3 平板留黑边）。
UIBootstrap 默认开启 **Auto Match Scaler**，运行时按比例动态调整：

```text
屏幕比参考分辨率更宽（平板 → 带鱼屏）→ match = 1（按高度缩放，UI 不超出上下）
屏幕比参考分辨率更窄（超长竖屏）    → match = 0（按宽度缩放，UI 不超出左右）
```

效果 = 参考分辨率画布始终完整可见（信封式适配），多余空间由背景出血填充。
CanvasScaler 须为 **Scale With Screen Size** 模式，参考分辨率照常在 prefab 上配置。

## 行为细节

- 两个组件都做**变化检测**：分辨率/朝向/safeArea 变了才重算，无变化零成本；转屏立即生效。
- `SafeAreaFitter` 对系统偶发的越界 safeArea 做了 Clamp——宁可不避让也不会把 UI 翻出屏幕。
- 纯计算入口 `SafeAreaFitter.TryCalculateAnchors` / `CanvasScalerAutoMatch.CalculateMatch`
  可单测（见 `Tests/EditMode/UiAdaptationTests.cs`）。
- 真机验证工具：Unity **Device Simulator**（Window → General → Device Simulator）
  可在编辑器内模拟各机型 safeArea。
