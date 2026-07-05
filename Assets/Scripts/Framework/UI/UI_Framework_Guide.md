 # UI 框架使用手册

本文档说明当前项目 UI 框架的推荐写法。简要规则写在仓库根目录 `AGENTS.md`，具体示例和生命周期细节以本文档为准。

## 核心分层

UI 框架分为四类对象：

- `UIView`：UI Prefab 根视图组件，只持有 Inspector 引用，不写业务逻辑。
- `UIBase<TView>`：窗口逻辑类，负责按钮绑定、打开关闭、数据刷新和场景接线。
- `UISubModule<TView>`：窗口 Prefab 内已经存在的嵌入型子模块，例如 `TurnHud`。
- `UISubPanel<TView>` + `UISubPanelHost<TPanel>`：运行时加载到窗口内部的子面板。

## 窗口 View

UI Prefab 根节点应挂一个继承 `UIView` 的 View 类。View 只做引用容器。

```csharp
using Framework;
using Framework.UI;
using UnityEngine;
using UnityEngine.UI;

namespace HotUpdate.UI.Example
{
    /// <summary>
    /// 排行榜窗口 View，只持有 Inspector 引用。
    /// </summary>
    public sealed class RankingView : UIView
    {
        [Header("关闭按钮")]
        public Button closeButton;

        [Header("标题文本")]
        public TextMeshProEx titleText;

        [Header("子面板挂载容器")]
        public Transform panelContainer;
    }
}
```

注意：

- 不要在 `UIView` 中绑定按钮、订阅事件、访问 Controller 或写状态机。**所有 `onClick`/事件绑定都在逻辑类（`OnInit` 等），不在 View 内，包括 `Awake`/`Start`。**
- View **允许**「渲染方法」：入参是控制器传进来的基本类型/状态（bool/enum/string/int），效果只作用于自身序列化控件（sprite/color/SetActive/text），可含 `selected ? A : B` 这类纯外观分支。判据——**只随美术/外观变、不随业务规则变**就留 View，会随业务规则变就进控制器。范例见下方「View 渲染方法 vs 业务逻辑」。
- View **禁止**：持有跨调用业务状态；做业务规则判断/决策（选谁、能否落子等）；读配置 / `SceneContext` / 任何组件或服务；订阅事件；在 `Awake`/`Start` 给自身控件绑回调。
- `UIView`、`UISubView` 等 Mono 序列化字段使用 `[Header("中文说明")]` 标注 Inspector 用途，不使用 XML 注释标注字段。
- UI 文本组件 Prefab 上优先使用 `Framework.UI.TextMeshProEx`。
- 业务字段可声明为 `TMP_Text` 或 `TextMeshProEx`；声明为 `TextMeshProEx` 时可直接调用 `SetRawText(...)`，声明为 `TMP_Text` 时需要先判断具体组件或直接设置原始文本。

## View 渲染方法 vs 业务逻辑

`UIView`/`UISubView` 是 Prefab（策划维护引用）与逻辑层（程序维护逻辑）之间的契约。它不是「零方法」，而是「零业务」：**可以有把自己渲染成某个样子的方法，不能有任何业务意图。**

判据一条：**这个方法会不会因为业务规则变化而被改？** 不会（只随美术/外观变）→ 留 View；会 → 进控制器。

范例（标准做法）：`TabItemView` 透出按钮引用 + 提供渲染方法，`UITabGroup` 持状态、绑点击、做决策。

```csharp
// View：只透出引用 + 渲染自己；无 Awake、无状态、不决定"选谁"
public sealed class TabItemView : MonoBehaviour
{
    [SerializeField] private Button button;
    [SerializeField] private GameObject activeObject;
    [SerializeField] private Image icon;
    [SerializeField] private Color selectedColor = Color.white;
    [SerializeField] private Color normalColor = Color.white;

    /// <summary>透出点击按钮，供控制器绑定。</summary>
    public Button Button => button;

    /// <summary>渲染选中/未选皮肤：纯外观，只随美术变、不随选中规则变。</summary>
    public void SetSelected(bool selected)
    {
        if (activeObject != null) activeObject.SetActive(selected);
        if (icon != null) icon.color = selected ? selectedColor : normalColor;
    }
}

// 控制器：持"当前选中下标"、在 OnInit 绑点击、决定选谁，再命令各 View 渲染
public sealed class UITabGroup : UISubModule<UITabGroupView>
{
    public int SelectedIndex { get; private set; } = -1;

    protected override void OnInit()
    {
        TabItemView[] items = View.Items;
        for (int i = 0; i < items.Length; i++)
        {
            int index = i;
            items[i]?.Button?.AddClick(() => Select(index)); // 接线在控制器
        }
    }

    public void Select(int index, bool notify = true)
    {
        SelectedIndex = index;                               // 状态在控制器
        for (int i = 0; i < View.Items.Length; i++)
        {
            View.Items[i]?.SetSelected(i == index);          // View 只执行显示命令
        }
    }
}
```

要点：`SetSelected` 里的 `selected ? A : B` 合法（纯外观分支）；"选谁"由 `UITabGroup.Select` 决定（业务/状态）。View 从不自己主张选谁，只执行控制器递下来的显示命令——按钮点击也由控制器在 `OnInit` 绑，View 内不出现 `Awake`/`onClick.AddListener`。

## 窗口逻辑

窗口逻辑继承 `UIBase<TView>`。生命周期与用途如下：

- `OnInit()`：只调用一次，适合绑定按钮、缓存长期对象。
- `OnOpen(object userData)`：每次打开调用，适合读取参数、刷新界面、接入场景表现层。
- `OnClose()`：每次关闭调用，适合解除展示期引用、停止动画和清理临时状态。
- `OnDestroy()`：窗口销毁时调用，适合释放长期订阅和对象。

```csharp
using Cysharp.Threading.Tasks;
using Framework;
using Framework.Core;

namespace HotUpdate.UI.Example
{
    /// <summary>
    /// 排行榜窗口逻辑。
    /// </summary>
    public sealed class RankingWindow : UIBase<RankingView>
    {
        /// <summary>
        /// 初始化窗口并绑定固定按钮。
        /// </summary>
        protected override void OnInit()
        {
            View.closeButton.AddClick(CloseSelf);
        }

        /// <summary>
        /// 打开窗口时刷新首屏数据。
        /// </summary>
        /// <param name="userData">打开参数，可为空。</param>
        protected override void OnOpen(object userData)
        {
            View.titleText.SetLang("#2_ranking_title");
        }

        /// <summary>
        /// 请求关闭当前窗口。
        /// </summary>
        private void CloseSelf()
        {
            GameEntry.UI.CloseUIAsync(this).Forget();
        }
    }
}
```

## 注册和打开窗口

注册 UI：

```csharp
GameEntry.UI.RegisterUI<RankingWindow>(
    "UI/Prefabs/RankingWindow",
    UILayer.Normal);
```

打开 UI：

```csharp
RankingWindow window = await GameEntry.UI.OpenUIAsync<RankingWindow>();
```

带参数打开：

```csharp
RankingWindow window = await GameEntry.UI.OpenUIAsync<RankingWindow>(userData);
```

切换到指定 UI：

```csharp
await GameEntry.UI.GoToUIAsync<RankingWindow>(userData);
```

关闭 UI：

```csharp
await GameEntry.UI.CloseUIAsync(window);
await GameEntry.UI.CloseUIAsync<RankingWindow>();
GameEntry.UI.CloseUI(window);
GameEntry.UI.CloseAllUI<RankingWindow>();
```

查询 UI：

```csharp
RankingWindow window = GameEntry.UI.GetUI<RankingWindow>();
bool opened = GameEntry.UI.IsUIOpened<RankingWindow>();
int count = GameEntry.UI.GetUICount<RankingWindow>();
```

注意：当前 `OpenUIAsync` 和 `GoToUIAsync` 只需要传窗口类型，不再传 View 类型。

## 嵌入型子模块

嵌入型子模块用于窗口 Prefab 内已经存在的区域，例如回合 HUD、工具栏、页签头。它不参与全局 UI 注册、层级和回退栈。

子视图继承 `UISubView`：

```csharp
using Framework;
using Framework.UI;
using UnityEngine;

namespace HotUpdate.UI.Example
{
    /// <summary>
    /// 回合 HUD 子视图，只持有文本引用。
    /// </summary>
    public sealed class TurnHudView : UISubView
    {
        [Header("状态文本")]
        public TextMeshProEx statusText;
    }
}
```

子模块继承 `UISubModule<TView>`，通过构造函数强制传入 View：

```csharp
using Framework;
using HotUpdate.UI.Example;

namespace HotUpdate.UI.Example
{
    /// <summary>
    /// 回合 HUD 子模块。
    /// </summary>
    public sealed class TurnHud : UISubModule<TurnHudView>
    {
        /// <summary>
        /// 创建回合 HUD 子模块。
        /// </summary>
        /// <param name="view">回合 HUD 子视图。</param>
        public TurnHud(TurnHudView view) : base(view)
        {
            Show();
        }

        /// <summary>
        /// 设置状态文本。
        /// </summary>
        /// <param name="status">状态文本。</param>
        public void SetStatus(string status)
        {
            View.statusText.SetRawText(status);
        }
    }
}
```

窗口或 Presenter 中使用：

```csharp
private TurnHud turnHud;

public void BindView(BattleView view)
{
    turnHud?.Dispose();
    turnHud = null;

    if (view != null && view.turnHudView != null)
    {
        turnHud = new TurnHud(view.turnHudView);
    }
}
```

## 加载型子面板

加载型子面板用于运行时加载到窗口内部的内容，例如排行榜页、背包分页、商店分类页。

子面板 View：

```csharp
using Framework;
using UnityEngine;

namespace HotUpdate.UI.Example
{
    /// <summary>
    /// 排行榜子面板 View。
    /// </summary>
    public sealed class RankPanelView : UISubView
    {
        [Header("排行条目根节点")]
        public Transform itemRoot;
    }
}
```

子面板逻辑：

```csharp
using Framework;

namespace HotUpdate.UI.Example
{
    /// <summary>
    /// 排行榜子面板逻辑。
    /// </summary>
    public sealed class RankPanel : UISubPanel<RankPanelView>
    {
        /// <summary>
        /// 初始化子面板，适合绑定固定按钮和缓存引用。
        /// </summary>
        protected override void OnInit()
        {
        }

        /// <summary>
        /// 显示子面板并刷新数据。
        /// </summary>
        /// <param name="userData">展示参数，可为空。</param>
        protected override void OnShow(object userData)
        {
        }

        /// <summary>
        /// 隐藏子面板时清理展示期状态。
        /// </summary>
        protected override void OnHide()
        {
        }

        /// <summary>
        /// 释放子面板长期资源。
        /// </summary>
        protected override void OnDispose()
        {
        }
    }
}
```

窗口中创建 Host。`key` 和 `parent` 在构造时传入，之后 `ShowAsync` 只传业务数据。

```csharp
using Framework;

namespace HotUpdate.UI.Example
{
    /// <summary>
    /// 排行榜窗口逻辑。
    /// </summary>
    public sealed class RankingWindow : UIBase<RankingView>
    {
        /// <summary>排行榜子面板宿主。</summary>
        private AddressableUISubPanelHost<RankPanel> rankPanelHost;

        /// <summary>
        /// 初始化窗口并创建子面板宿主。
        /// </summary>
        protected override void OnInit()
        {
            rankPanelHost = new AddressableUISubPanelHost<RankPanel>(
                "UI/Prefabs/RankPanel",
                View.panelContainer);
        }

        /// <summary>
        /// 打开窗口时显示排行榜子面板。
        /// </summary>
        /// <param name="userData">打开参数，可为空。</param>
        protected override async void OnOpen(object userData)
        {
            await rankPanelHost.ShowAsync(userData);
        }

        /// <summary>
        /// 关闭窗口时释放子面板。
        /// </summary>
        protected override void OnClose()
        {
            rankPanelHost?.Dispose();
        }
    }
}
```

也可以只预加载不显示：

```csharp
await rankPanelHost.LoadAsync();
```

自定义加载策略时继承 `UISubPanelHost<TPanel>`：

```csharp
using Cysharp.Threading.Tasks;
using Framework;
using UnityEngine;

namespace HotUpdate.UI.Example
{
    /// <summary>
    /// 池化 UI 子面板宿主示例。
    /// </summary>
    /// <typeparam name="TPanel">子面板逻辑类型。</typeparam>
    public sealed class PooledUISubPanelHost<TPanel> : UISubPanelHost<TPanel>
        where TPanel : UISubPanelCore, new()
    {
        /// <summary>
        /// 创建池化 UI 子面板宿主。
        /// </summary>
        /// <param name="key">对象池 key。</param>
        /// <param name="parent">挂载父节点。</param>
        public PooledUISubPanelHost(string key, Transform parent) : base(key, parent)
        {
        }

        /// <summary>
        /// 从对象池加载子面板根对象。
        /// </summary>
        /// <param name="key">对象池 key。</param>
        /// <param name="parent">挂载父节点。</param>
        /// <returns>加载成功时返回子面板根对象。</returns>
        protected override UniTask<GameObject> LoadGameObjectAsync(string key, Transform parent)
        {
            throw new System.NotImplementedException();
        }

        /// <summary>
        /// 归还子面板根对象。
        /// </summary>
        /// <param name="instance">需要归还的子面板根对象。</param>
        protected override void ReleaseGameObject(GameObject instance)
        {
            throw new System.NotImplementedException();
        }
    }
}
```

## 循环滚动列表

`Framework.UI.LoopScroll` 提供循环滚动列表 `LoopScrollList`。实例化的行视图数量恒定为「可视项数 + 缓冲」，与数据量无关；滚动时把移出视口的项回收进池再复用并重新绑定，不做 Instantiate/Destroy。适用于排行榜、好友列表、聊天记录等长列表。

布局委托 `ILoopLayout` 策略（仿 RecyclerView LayoutManager），由 `LoopScrollListView` 上的参数选定，支持四种模式：

| 模式 | View 配置 | 布局策略 |
|------|-----------|----------|
| 竖向单列 | `Axis=Vertical`、`CrossAxisCount=1` | `GridLoopLayout` |
| 横向单行 | `Axis=Horizontal`、`CrossAxisCount=1` | `GridLoopLayout` |
| 网格 | `CrossAxisCount≥2`（定尺寸） | `GridLoopLayout` |
| 变长 | `VariableSize=true`（单列/行，每项尺寸不同） | `VariableLoopLayout` |

由三件套组成：

- `LoopScrollListView`（`UISubView`）：持 `ScrollRect/Viewport/Content/CellTemplate` 引用与布局参数（轴向 / 交叉轴数 / 变长开关 / 双轴间距内边距 / 缓冲），挂在 ScrollRect 节点上。
- `ILoopListSource`：数据源（Adapter），只暴露 `Count` 和 `BindCell(cell, index)`，列表不认识任何业务类型；变长模式额外实现 `ILoopVariableSource.GetItemSize(index)`。
- 行视图（`UISubView`）：单项模板，可选实现 `ILoopListClickable`（透出整行按钮供框架绑定）与 `ILoopListCell`（回收清理）。

**Prefab 约定**：`Content` 取左上锚点、pivot 左上；行高 / 列宽从 `CellTemplate` 的 `RectTransform` 自动读取（无需配置数字，变长模式由 `GetItemSize` 提供主轴尺寸）；行模板设为隐藏子节点；空数据提示挂在可选的 `EmptyHint` 节点上，列表按条数自动显隐。

行视图（只透出整行按钮，点击由 `LoopScrollList` 统一绑定，View 内不出现 `Awake`/绑定逻辑）：

```csharp
using Framework;
using Framework.UI;
using UnityEngine;
using UnityEngine.UI;

namespace HotUpdate.UI.Example
{
    /// <summary>
    /// 列表行视图：只持有 Inspector 引用并透出整行点击按钮。
    /// </summary>
    public sealed class ItemCellView : UISubView, ILoopListClickable
    {
        [Header("标题文本")]
        public TextMeshProEx titleText;

        [Header("整行点击按钮")]
        [SerializeField] private Button rowButton;

        /// <summary>透出整行点击按钮，供 LoopScrollList 统一绑定点击并补下标外抛。</summary>
        public Button RowButton => rowButton;
    }
}
```

数据源（绑定时把 `UISubView` 向下转型为具体行视图，热路径内禁分配、禁隐式查找）：

```csharp
using System.Collections.Generic;
using Framework;

namespace HotUpdate.UI.Example
{
    /// <summary>
    /// 列表数据源：把业务列表适配为 LoopScrollList 可消费的数据。
    /// </summary>
    public sealed class ItemListSource : ILoopListSource
    {
        private IReadOnlyList<ItemInfo> _items;

        public int Count => _items?.Count ?? 0;

        /// <summary>替换数据；调用方随后触发列表重铺。</summary>
        public void SetItems(IReadOnlyList<ItemInfo> items) => _items = items;

        /// <summary>把第 index 条数据写入行视图。</summary>
        public void BindCell(UISubView cell, int index)
        {
            var view = (ItemCellView)cell;
            view.titleText.SetRawText(_items[index].Title);
        }
    }
}
```

窗口 / 子面板中接线（控制器是 `UISubModule`，在 `OnInit` 建、`OnDispose` 释放）：

```csharp
private LoopScrollList _list;
private readonly ItemListSource _source = new ItemListSource();

protected override void OnInit()
{
    _list = new LoopScrollList(View.loopList);     // View.loopList 为 LoopScrollListView
    _list.OnItemClicked += idx => { /* 行点击：idx 为数据下标 */ };
}

protected override void OnShow(object userData)
{
    _source.SetItems(items);
    _list.SetSource(_source);                       // 从顶部全量重建
    _list.ScrollToIndex(targetIndex, LoopAlign.Center);  // 可选：定位某项
}

protected override void OnDispose()
{
    _list?.Dispose();
    _list = null;
}
```

API 速查：

| 方法 | 用途 |
|------|------|
| `SetSource(source)` | 设数据源并从起始端全量重建 |
| `Reload()` | 重读条数重建并重绑当前可视项，尽量保留滚动位置（数据内容变更后用它） |
| `RefreshVisible()` | 原地重绑当前可视项（条数不变、内容变，如在线状态刷新） |
| `RefreshItem(index)` | 刷新单项（在可视区内才生效） |
| `ScrollToIndex(index, align, animated)` | 定位到指定项（`LoopAlign.Start/Center/End` 对齐，可动画） |
| `InsertItem(index, animated)` / `RemoveItem(index, animated)` | 单项增删，按锚点钉定保持滚动位置 + 邻项滑动动画 |
| `OnItemClicked`（`event Action<int>`） | 行点击，参数为数据下标 |

注意：

- `InsertItem/RemoveItem` 调用前，数据源 `Count` 必须已反映增删后的条数（否则列表会退回全量重建）。
- 行视图复用，回收前若需停动画 / 反订阅 / 清业务引用，实现 `ILoopListCell.OnRecycled()`，避免复用串数据。
- 控制器订阅了 `ScrollRect.onValueChanged`，必须在 `OnDispose` 调 `Dispose()` 成对释放。
- 变长模式数据源须实现 `ILoopVariableSource`，且 `CrossAxisCount` 须为 1（网格 + 变长不支持）。
- 布局算术在 `GridLoopLayout` / `VariableLoopLayout`（实现 `ILoopLayout`，无 MonoBehaviour），改动后由 `GridLoopLayoutTests` / `VariableLoopLayoutTests`（EditMode）覆盖。

### 重交互行：Presenter 驱动数据源

当某行从「展示 + 整行点击」长成**带多个子控件交互的迷你窗口**（行内多按钮、单行异步如头像加载、单行订阅如该好友在线变化）时，再把逻辑堆进 `BindCell` 会又胖又有状态。这时用 `LoopPresenterSource<TPresenter, TView, TData>` 取代普通 `ILoopListSource`：

- 每个**物理行视图**常驻一个 `LoopCellPresenter`（与视图 1:1 终身绑定，跟随复用、不随回收销毁，滚动稳态零分配）。
- View 仍全哑（只有引用 + 渲染方法 + 透出按钮）；行内交互逻辑全在 Presenter。
- 列表回收行时经 `ILoopListRecycleAware` 通知数据源解绑 Presenter（取消异步、反订阅、清当前数据）。

Presenter 生命周期（命名对齐项目惯例）：

| 钩子 | 时机 | 用途 |
|------|------|------|
| `OnInit()` | 物理视图首次绑定，仅一次 | 接线行内子控件点击（**回调读 `Data` 取当前行，切勿在闭包捕获 data**，否则复用串数据） |
| `OnBind(data)` | 每次重绑 | 渲染数据、挂靠 `BoundToken` 的异步 |
| `OnUnbind()` | 每次回收 | 反订阅等（行内异步已随 `BoundToken` 自动取消） |

```csharp
public sealed class FriendCardPresenter : LoopCellPresenter<FriendCardView, FriendInfo>
{
    protected override void OnInit()                       // 一次性接线，回调读 Data
    {
        View.ChallengeButton.AddClick(() => { if (IsBound) Invite(Data); });
        View.DeleteButton.AddClick(() => { if (IsBound) Delete(Data); });
    }

    protected override void OnBind(FriendInfo data)        // 每次重绑：渲染 + 异步
    {
        View.Bind(data.Nickname, data.Tier, data.Score, data.IsOnline, data.IsSelf);
        LoadAvatarAsync(data.AvatarUrl, BoundToken).Forget();
    }

    protected override void OnUnbind() { /* 反订阅；异步已随 BoundToken 取消 */ }
}

// 数据源只需声明三个类型参数
public sealed class FriendsPresenterSource
    : LoopPresenterSource<FriendCardPresenter, FriendCardView, FriendInfo> { }
```

页面接线与普通数据源一致（`SetItems` / `GetItem` / `SetSource` / `Reload`）。**普通行别用这套**——只有展示 + 整行点击时，`ILoopListSource` + `ILoopListClickable` 更省。

## 窗口生命周期（完整时序）

窗口打开：`OnInit()` → `OnOpen(userData)` → 播放进入动画 → `OnOpenReady()`

窗口关闭：`OnClose()` → 播放退出动画 → `OnCloseComplete()` → SetActive(false)

- `OnInit()`：实例化后仅调用一次，适合绑定按钮、缓存长期引用。
- `OnOpen(userData)`：每次打开调用（动画前），适合读取参数、刷新 UI。
- `OnOpenReady()`：进入动画播放完毕后调用，适合启动引导高亮、轮询等需要动画结束后才执行的逻辑。
- `OnClose()`：关闭时调用（动画前），适合解除展示期引用、停止输入响应。
- `OnCloseComplete()`：退出动画播放完毕后调用，适合释放动画期间仍需保持的引用。
- `OnDestroy()`：窗口销毁时调用，适合释放长期订阅和对象。

```csharp
public sealed class ExampleWindow : UIBase<ExampleView>
{
    protected override void OnOpenReady()
    {
        // 动画结束后才启动引导
        TutorialManager.ShowStep("first_click");
    }

    protected override void OnCloseComplete()
    {
        // 动画结束后释放临时缓存
        _previewCache = null;
    }
}
```

## 导航栈与返回

UIManager 内部维护一个导航栈（LinkedList 实现，支持 O(1) 随机移除），提供类似页面路由的返回能力。

### 栈行为（UIStackBehavior）

注册 UI 时通过 `stackBehavior` 参数指定窗口在导航栈中的行为：

| 枚举值 | 含义 | 典型场景 |
|--------|------|----------|
| `PushToStack`（默认） | 正常入栈，GoBack 时按 LIFO 返回 | 主界面、二级页面 |
| `NoStack` | 不入栈，不参与导航返回 | Toast、HUD、多实例弹窗 |
| `ReplaceTop` | 替换栈顶（关闭旧栈顶再入栈） | 同级 Tab 页互切 |

```csharp
// 弹窗不入栈
GameEntry.UI.RegisterUI<PopupWindow>(
    "UI/Prefabs/PopupWindow",
    UILayer.Popup,
    allowMultiple: true,
    stackBehavior: UIStackBehavior.NoStack,
    blockerMode: UIBlockerMode.DimBlack);

// 主界面正常入栈
GameEntry.UI.RegisterUI<MainHomeWindow>(
    "UI/Prefabs/MainHome",
    UILayer.Normal);
```

### 导航 API

```csharp
// 返回上一个页面（关闭栈顶，带动画）
await GameEntry.UI.GoBackAsync();

// 清空栈，直接跳转到指定 UI
await GameEntry.UI.GoToUIAsync<MainHomeWindow>();

// 查询栈深度
int depth = GameEntry.UI.GetStackDepth();
```

### 注意事项

- `GoBackAsync` 会关闭栈顶窗口并激活前一个窗口（SetActive = true），但不会重新调用前一个窗口的 `OnOpen`。如果需要回到前一个窗口时刷新数据，建议使用事件通知。
- `AllowMultiple = true` 的窗口建议搭配 `UIStackBehavior.NoStack`，避免多个同类实例污染栈。
- 切场景后栈会被清空（`CloseAllUI` 行为），新场景需要重新建立导航入口。

## 遮罩（UIBlocker）

窗口弹出时可自动创建全屏遮罩，拦截下层 UI 的点击事件。通过注册时的 `blockerMode` 参数配置。

### 遮罩模式（UIBlockerMode）

| 枚举值 | 含义 |
|--------|------|
| `None`（默认） | 不创建遮罩 |
| `Transparent` | 全屏透明遮罩，只拦截点击 |
| `DimBlack` | 半透明黑色遮罩（alpha 0.6） |
| `ClickToClose` | 半透明黑色遮罩 + 点击空白区域关闭当前窗口 |

```csharp
// 设置面板弹出时有暗色遮罩
GameEntry.UI.RegisterUI<SettingsWindow>(
    "UI/Prefabs/Settings",
    UILayer.Popup,
    blockerMode: UIBlockerMode.DimBlack);

// 提示框点击空白关闭
GameEntry.UI.RegisterUI<TooltipWindow>(
    "UI/Prefabs/Tooltip",
    UILayer.Popup,
    blockerMode: UIBlockerMode.ClickToClose);
```

遮罩由 UIManager 在打开窗口时自动创建、关闭时自动销毁，业务层无需手动管理。

## 窗口间通信

窗口之间不应直接持有彼此引用。推荐的通信路径：

- 同一 Presenter/Controller 内的窗口联动 → 通过 Presenter 方法调用。
- 跨模块解耦通知 → 通过 `GameEntry.Event` 事件系统。
- 窗口向场景请求数据 → 通过 `GameEntry.Scene.GetContext<T>()` 获取场景上下文。

```csharp
// 发送事件
GameEntry.Event.Fire(new BattleEndedEvent { Winner = playerId });

// 监听事件（OnInit 中订阅）
GameEntry.Event.Subscribe<BattleEndedEvent>(OnBattleEnded);

// OnClose / OnDestroy 中取消
GameEntry.Event.Unsubscribe<BattleEndedEvent>(OnBattleEnded);
```

## 对象池与切场景

UIManager 对每种 UI 类型维护一个 `GameObjectPool`。窗口关闭时默认归还池中（SetActive=false），再次打开时直接复用。

- 池有最大容量限制（默认 100），超出时自动 Destroy 多余实例。
- 切场景时建议调用 `GameEntry.UI.CloseAllUI(drainPools: true)` 同时清空池，避免池内 GameObject 挂在已销毁的 Canvas 下。
- 如果窗口 Prefab 占用较多纹理内存，注册时可搭配 `usePool: false` 参数在 Open 时跳过池化。

## 子模块异步初始化

`UISubModule<TView>` 的构造函数是同步的。如果子模块初始化需要异步操作（加载远程数据、解析图集等），推荐以下模式：

```csharp
public sealed class PlayerCardModule : UISubModule<PlayerCardView>
{
    private bool _isDataReady;

    public PlayerCardModule(PlayerCardView view) : base(view) { }

    protected override async void OnShow(object userData)
    {
        if (!_isDataReady)
        {
            await LoadPlayerDataAsync();
            _isDataReady = true;
        }
        RefreshView();
    }

    private async UniTask LoadPlayerDataAsync()
    {
        // 异步加载数据...
    }
}
```

注意：异步准备逻辑放在 `OnShow` 首次调用时执行，用 flag 防止重复加载，保持 Init/Show 的语义边界清晰。

## 选择规则

- 整个独立窗口：用 `UIView` + `UIBase<TView>`，通过 `UIManager` 注册和打开。
- 窗口 Prefab 内固定存在的子区域：用 `UISubView` + `UISubModule<TView>`，构造函数传入 View。
- 运行时加载到窗口内部的子页面：用 `UISubPanel<TView>` + `UISubPanelHost<TPanel>`。
- 场景对象访问：窗口不要运行时查找，优先通过 `GameEntry.Scene.GetContext<TSceneContext>()` 获取场景上下文。

## 文本和多语言

- UI Prefab 新文本组件优先使用 `TextMeshProEx`。
- 静态挂载翻译使用 `#2_xxx`。
- 代码主动翻译使用 `SetLang("#1_xxx")`。
- 玩家名、版本号、数字、服务器返回内容使用 `SetRawText(...)` 或直接原始赋值。
- 找不到语言 key 时应回退原 key，不能阻断启动流程。

## 常见错误

- 不要把业务逻辑写进 `UIView` 或 `UISubView`（渲染方法可留，业务状态/判断/数据访问/订阅不行，见「View 渲染方法 vs 业务逻辑」）。
- 不要在 View 的 `Awake`/`Start` 里绑 `onClick` 或订阅事件；接线一律在逻辑类 `OnInit`，池化行视图只透出 `ILoopListClickable.RowButton` 由列表统一绑。
- 不要在业务侧手动调用 `UISubPanelCore.Initialize(...)`，加载型子面板由 Host 内部初始化。
- 不要再使用 `OpenUIAsync<TWindow, TView>` 或 `GoToUIAsync<TWindow, TView>`。
- 不要让窗口持有已经关闭的 View；`OnClose()` 中应断开 Presenter 或子模块引用。
- 不要在运行时用 `GetComponentInChildren(...)` 查找跨组件业务引用，稳定引用应通过 Inspector 显式配置。
- 不要把多个窗口逻辑类或多个阶段类合并到同一个文件中。

## 文件组织约定

每个公开类独占一个 `.cs` 文件，文件名与类名一致。UI 和阶段代码按以下结构组织：

```
Assets/Scripts/HotUpdate/UI/{模块名}/
├── {Xxx}View.cs              ← View 类（UIView 子类）
├── {Yyy}View.cs
├── {模块名}View.cs           ← View 基类（可选）
├── {模块名}RuntimeData.cs    ← OpenArgs / 数据模型
├── {模块名}UIRegistry.cs     ← 集中注册
└── Windows/
    ├── {模块名}WindowBase.cs ← 窗口基类（可选）
    ├── {Xxx}Window.cs        ← 窗口逻辑类
    └── {Yyy}Window.cs

Assets/Scripts/HotUpdate/Scene/{模块名}/
├── {模块名}SceneContext.cs   ← 场景上下文
├── Presentation/             ← 表现层组件
│   ├── BoardView.cs
│   └── ...
└── Stages/
    ├── {Xxx}Stage.cs         ← 游戏阶段
    ├── {Yyy}Stage.cs
    └── {Zzz}Coordinator.cs   ← 阶段辅助类
```

示例（Blokus 模块当前结构）：

```
HotUpdate/UI/Blokus/
├── BattleView.cs, MainHomeView.cs, ... (13 个 View)
├── BlokusView.cs (View 基类)
├── BlokusRuntimeData.cs
├── BlokusUIRegistry.cs
└── Windows/
    ├── BlokusWindowBase.cs
    ├── BattleWindow.cs, MainHomeWindow.cs, ... (16 个窗口)

HotUpdate/Scene/Blokus/
├── BlokusBattleSceneContext.cs
├── Presentation/ (14 个表现层组件)
└── Stages/
    ├── MatchingStage.cs
    ├── BattleStage.cs
    ├── SettlementStage.cs
    └── BattleReconnectCoordinator.cs
```

## 验证

修改 Framework UI 代码后执行：

```powershell
dotnet build G:\ClientBase\Framework.csproj --no-restore
```

修改 HotUpdate UI 代码后执行：

```powershell
dotnet build G:\ClientBase\HotUpdate.csproj --no-restore
```
