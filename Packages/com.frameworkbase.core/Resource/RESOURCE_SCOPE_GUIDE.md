# 资源作用域使用指南（ResourceScope）

## 为什么需要它

Addressables 句柄泄漏是长期运营项目的头号内存问题：借出的资源散落在各业务里，
谁借的、退出时还了没有，没人对账。作用域把归还从"N 处 Release 调用"收敛成
"一处 Dispose"——**阶段退出 = 资源归还**，结构上杜绝漏还。

## 用法

```csharp
// 推荐：一个阶段/场景/功能一个作用域
private ResourceScope _scope;

public async UniTask OnEnterAsync()
{
    _scope = GameEntry.Resource.CreateScope("BattleStage");
    var cfg  = await _scope.LoadAssetAsync<BattleConfig>("Battle/config");
    var unit = await _scope.InstantiateAsync("Battle/unit_01", _root);
}

public void OnExit()
{
    _scope?.Dispose();   // 借出的实例与资源引用全部自动归还，幂等
    _scope = null;
}

// 短生命周期直接 using
using (var scope = GameEntry.Resource.CreateScope("RewardPopup"))
{
    var icon = await scope.LoadAssetAsync<Sprite>("UI/reward_icon");
    ...
} // 离开即归还
```

## 规则（违反会告警）

| 规则 | 说明 |
|---|---|
| 借还走同一个作用域 | 经 scope 借的资源直接找 ResourceManager 还，会导致 Dispose 二次归还、计数错乱 |
| 提前还用 scope.ReleaseAsset / ReleaseInstance | 作用域会销账，Dispose 不再重复归还这一笔 |
| Dispose 幂等 | 重复 Dispose 无副作用；Dispose 后再借被拒绝（Error 日志 + 返回 null） |
| 外部 Destroy 的实例 | Dispose 时自动跳过（实例句柄随对象销毁已失效） |
| 异步打断 | await 加载期间作用域被 Dispose，完成后自动归还该笔，不泄漏 |

## 泄漏检测

- **未 Dispose 哨兵**（Editor / Development Build）：作用域被 GC 回收却没 Dispose，
  终结器打 Error 并附**创建位置堆栈**——直接定位谁漏的。正式包零开销。
- **兜底**：ResourceManager.OnShutdown 释放全部残留句柄/实例并打日志（防崩溃泄漏，
  不是让业务依赖它——依赖 Shutdown 兜底说明作用域没用对）。
- **诊断计数**：`LiveAssetHandleCount / LiveInstanceCount / LiveLabelHandleCount`
  供性能 HUD 常驻显示；阶段切换前后对比这些数字，不回落即有泄漏。
  `PrintLoadedAssets()` 打印逐地址引用计数。

## 架构说明

作用域只依赖 `IResourceScopeHost` 四个方法（Load/Instantiate/ReleaseAsset/ReleaseInstance），
不绑死 ResourceManager——记账逻辑用假宿主离线单测（`Tests/EditMode/ResourceScopeTests.cs`），
不需要初始化 Addressables。
