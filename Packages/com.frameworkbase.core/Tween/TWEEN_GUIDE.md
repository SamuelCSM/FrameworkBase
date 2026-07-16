# 通用补间（Tween）指南

框架选定 **[PrimeTween](https://github.com/KyryloKuzyk/PrimeTween)** 作为通用补间标准：零 GC、结构体句柄、
AOT / IL2CPP 安全、WebGL 可 `await`、销毁目标即安全终止动画。

## 设计取舍（为什么不包一层门面）

框架**刻意不**把 `PrimeTween.Tween` / `PrimeTween.Sequence` 再包一层框架接口。补间库的价值就在其零分配的
流式 API，包一层要么重造整个表面、要么照样泄漏厂商类型，且会引入分配、得不偿失。业界（DOTween / PrimeTween）
均以「直接调用」为惯例。框架只做三件它该做的事：

1. **启动期配置**（`TweenBootstrap`）：预分配补间容量 + 默认缓动，杜绝运行期扩容 GC。
2. **UniTask + 取消桥接**（`TweenAsyncExtensions`）：把框架统一的 `CancellationToken` 取消语义接到 PrimeTween。
3. **UI 过渡预设复用**（`UIAnimator`）：窗口开关动画建立在 PrimeTween 之上。

> 详见 `ARCHITECTURE_DECISIONS.md` 的 **ADR-007**。

## 依赖

PrimeTween 是 npm scoped registry 包（`com.kyrylokuzyk`），**由工程 `manifest.json` 提供**
（本仓库壳工程已内置 scopedRegistries + 依赖）。它与 UniTask / HybridCLR 同为框架**硬依赖**——补间是
框架选定的标准底座能力（UI 依赖它），不是可选厂商件，因此**不加** `#if` define 门控、也不留手写回退分支
（可选 define 的正当场景是像 Bugly 那种「有则接、无则用本地后端」的可选厂商集成，见 ADR-007）。

## 常规用法（业务 / 热更程序集）

直接 `using PrimeTween;`，一行animate 任意对象：

```csharp
using PrimeTween;

// 位移 / 缩放 / 旋转 / 颜色 / CanvasGroup alpha / 相机抖动 …… 打出 "Tween." 让 IDE 补全
Tween.PositionY(transform, endValue: 10, duration: 1, ease: Ease.OutCubic);
Tween.Scale(icon, endValue: 1.2f, duration: 0.2f, cycles: 2, cycleMode: CycleMode.Yoyo);
Tween.ShakeCamera(Camera.main, strengthFactor: 0.5f);

// 序列：并行 Group / 串行 Chain
Sequence.Create()
    .Group(Tween.Alpha(canvasGroup, 1f, 0.3f))
    .Chain(Tween.LocalPositionY(panel, 0f, 0.3f, Ease.OutBack));
```

### 在 UniTask 流程里等待（带取消）

框架的 UI/场景/阶段流程都以 `CancellationToken` 传播取消。用扩展把补间接进来：

```csharp
using Framework;              // ToUniTask 扩展
using PrimeTween;

// ct 取消时：补间停在当前值（Stop，非 Complete），await 正常返回、不抛异常
await Tween.Alpha(cg, 1f, 0.3f).ToUniTask(ct);
await Sequence.Create().Group(...).Chain(...).ToUniTask(ct);
```

> `await` 的 C# 状态机本身有分配（PrimeTween 补间自身零 GC）。高频路径请用 `Sequence` + `OnComplete` 回调，勿 `await`。

## UI 窗口过渡

窗口过渡沿用既有 `UIAnimationConfig`，`UIBase` 子类重写 `AnimConfig` 即可，无需直接写补间：

```csharp
protected override UIAnimationConfig AnimConfig => UIAnimationConfig.ScalePop();
```

`UIAnimator` 内部按是否接入 PrimeTween 自动选实现。若游戏会在 `Time.timeScale == 0`（如暂停菜单）时弹窗，
设 `UIAnimator.UseUnscaledTime = true` 避免过渡因时停卡住（默认 false，与历史行为一致）。

## 容量调优

真机跑一遍典型战斗 / 高峰界面，看 `PrimeTweenManager` Inspector 的 **Max alive tweens**，加安全裕量后：

```csharp
// 默认 200；按需在启动早期调整（须早于首个补间）
TweenBootstrap.Initialize(capacity: 400);
```
