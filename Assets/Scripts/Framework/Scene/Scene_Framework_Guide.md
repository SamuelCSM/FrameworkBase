# 场景框架使用手册

本文说明场景内 Mono View 与普通 C# 控制类的推荐写法。UI 请继续阅读 `Assets/Scripts/Framework/UI/UI_Framework_Guide.md`。

## 核心分层

场景框架分为三类对象：

- `SceneView` + `SceneObject<TView>`：场景中稳定存在的预制根对象，例如棋盘、角色展示台、场景表现入口。
- `SceneSubView` + `SceneSubModule<TView>`：已经嵌在场景预制内部的子控件，例如棋盘内固定 HUD 锚点、固定特效挂点组。
- `SceneSubView` + `SceneSubPrefab<TView>` + `SceneSubPrefabHost<TPrefab>`：运行时加载到场景节点下的子预制，例如特效组、临时展示物、可池化场景表现单元。

## 场景 View

场景 View 只做 Inspector 引用容器，不写业务逻辑和状态机。

```csharp
using Framework;
using UnityEngine;

namespace HotUpdate.Scene.Example
{
    /// <summary>
    /// 棋盘场景 View，只持有 Inspector 引用。
    /// </summary>
    public sealed class BoardView : SceneView
    {
        [Header("棋盘格根节点")]
        public Transform gridRoot;

        [Header("棋盘格边长")]
        public float cellSize = 1f;
    }
}
```

注意：

- `SceneView` 和 `SceneSubView` 的序列化字段使用 `[Header("中文说明")]`。
- 不要在 View 中绑定事件、访问 Controller、维护运行时状态或调用业务流程。
- 运行时动态生成的纯展示子节点可以由对应控制类创建。

## 场景对象

场景对象逻辑继承 `SceneObject<TView>`，通过构造函数强制传入 View。

```csharp
using Framework;
using UnityEngine;

namespace HotUpdate.Scene.Example
{
    /// <summary>
    /// 棋盘场景控制类。
    /// </summary>
    public sealed class BoardObject : SceneObject<BoardView>
    {
        /// <summary>
        /// 创建棋盘控制类。
        /// </summary>
        /// <param name="view">棋盘 View。</param>
        public BoardObject(BoardView view) : base(view)
        {
        }

        /// <summary>
        /// 刷新棋盘显示。
        /// </summary>
        public void Refresh()
        {
            View.gridRoot.gameObject.SetActive(true);
        }
    }
}
```

`SceneObject<TView>` 生命周期：

- `OnInit()`：构造时调用一次，适合绑定事件和缓存长期对象。
- `Show(object userData)` / `OnShow(object userData)`：显示并刷新数据。
- `Hide()` / `OnHide()`：隐藏并清理展示期状态。
- `Dispose()` / `OnDispose()`：释放控制类，解除长期订阅。

## 场景上下文接线

`SceneBase` 子类负责持有 View 引用，并创建普通 C# 控制类。

```csharp
using Framework;
using UnityEngine;

namespace HotUpdate.Scene.Example
{
    /// <summary>
    /// 示例场景上下文。
    /// </summary>
    public sealed class BattleSceneContext : SceneBase
    {
        [Header("棋盘 View")]
        public BoardView boardView;

        /// <summary>棋盘控制类。</summary>
        private BoardObject boardObject;

        /// <summary>
        /// 当前棋盘控制类。
        /// </summary>
        public BoardObject BoardObject => boardObject;

        /// <summary>
        /// Unity 唤醒回调。
        /// </summary>
        protected override void Awake()
        {
            base.Awake();
            boardObject = boardView != null ? new BoardObject(boardView) : null;
        }

        /// <summary>
        /// Unity 销毁回调。
        /// </summary>
        protected override void OnDestroy()
        {
            boardObject?.Dispose();
            boardObject = null;
            base.OnDestroy();
        }
    }
}
```

## 内嵌子对象

场景预制内部已经存在的子控件使用 `SceneSubModule<TView>`。

```csharp
using Framework;
using UnityEngine;

namespace HotUpdate.Scene.Example
{
    /// <summary>
    /// 倒计时子 View。
    /// </summary>
    public sealed class TimerSubView : SceneSubView
    {
        [Header("倒计时根节点")]
        public Transform timerRoot;
    }

    /// <summary>
    /// 倒计时子模块。
    /// </summary>
    public sealed class TimerSubModule : SceneSubModule<TimerSubView>
    {
        /// <summary>
        /// 创建倒计时子模块。
        /// </summary>
        /// <param name="view">倒计时子 View。</param>
        public TimerSubModule(TimerSubView view) : base(view)
        {
        }
    }
}
```

## 加载型子预制

运行时加载到场景节点下的子预制使用 `SceneSubPrefab<TView>` 和 `SceneSubPrefabHost<TPrefab>`。

```csharp
using Framework;
using UnityEngine;

namespace HotUpdate.Scene.Example
{
    /// <summary>
    /// 命中特效 View。
    /// </summary>
    public sealed class HitEffectView : SceneSubView
    {
        [Header("粒子组件")]
        public ParticleSystem particle;
    }

    /// <summary>
    /// 命中特效控制类。
    /// </summary>
    public sealed class HitEffectPrefab : SceneSubPrefab<HitEffectView>
    {
        /// <summary>
        /// 显示时播放粒子。
        /// </summary>
        /// <param name="userData">显示参数，可为空。</param>
        protected override void OnShow(object userData)
        {
            View.particle.Play();
        }
    }
}
```

创建 Host：

```csharp
SceneSubPrefabHost<HitEffectPrefab> host =
    new SceneSubPrefabHost<HitEffectPrefab>("Common/Effects/Hit", effectRoot);

await host.ShowAsync(userData);
host.Hide();
host.Dispose();
```

需要对象池时传入 `PooledGameObjectProvider`：

```csharp
var provider = new PooledGameObjectProvider(poolRoot, 8, 64);
var host = new SceneSubPrefabHost<HitEffectPrefab>(
    "Common/Effects/Hit",
    effectRoot,
    provider);
```

单个子预制需要复用时，也可以直接使用池化 Host：

```csharp
var host = new PooledSceneSubPrefabHost<HitEffectPrefab>(
    "Common/Effects/Hit",
    effectRoot,
    poolRoot,
    defaultCapacity: 4,
    maxSize: 32);

await host.PrewarmAsync(4);
await host.ShowAsync(userData);
host.Dispose(); // 当前实例回到池中，Host 保留后下次 ShowAsync 可复用池内实例。
```

同一类子预制需要批量租借和归还时，使用 `SceneSubPrefabPool<TPrefab>` 统一管理：

```csharp
var pool = new SceneSubPrefabPool<HitEffectPrefab>(poolRoot, 4, 32);
await pool.PrewarmAsync("Common/Effects/Hit", 4);

SceneSubPrefabHost<HitEffectPrefab> host =
    await pool.ShowAsync("Common/Effects/Hit", effectRoot, userData);

pool.Release(host);
pool.Dispose();
```

## 选择规则

- 场景中稳定存在的预制根对象：`SceneView` + `SceneObject<TView>`。
- 场景预制里固定存在的子区域：`SceneSubView` + `SceneSubModule<TView>`。
- 运行时加载或池化的场景子预制：`SceneSubView` + `SceneSubPrefab<TView>` + `SceneSubPrefabHost<TPrefab>`。
- UI 窗口、UI 子模块和 UI 子面板仍使用 UI 框架，不要混用场景框架。
