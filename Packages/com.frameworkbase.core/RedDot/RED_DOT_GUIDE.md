# 配置驱动红点系统

## 定位

全局红点使用 `GameEntry.RedDots`（`RedDotService`）：稳定整数 ID 寻址、配置驱动 DAG、多父节点聚合、Provider 完整快照、账号已看版本和 UI 订阅。

旧 `RedDotTree` 继续保留，适合独立玩法内无需配置、多父节点和持久化的局部严格树。

红点服务只是业务状态到 UI 的投影，不是业务事实来源。奖励、邮件、任务等重要状态必须仍以对应业务 Model/服务端数据为准。

## Module 为什么是 ModuleId

模块表示节点的归属和维护责任，不表示拓扑主入口：

- `SystemUnread` 通过 ModuleId 归 Mail 模块，运行时完整名称自动派生为 `Mail.SystemUnread`；
- `Mail` 通过 ModuleId 归 Main 模块，完整名称自动派生为 `Main.Mail`；
- 二者通过 `red_dot_edge_ref` 建立依赖；
- 一个模块可以有多个入口，一个入口也可以依赖多个模块的 Signal。

配置通过 `red_dot_module_ref` 分配稳定 `ModuleId` 与节点 ID 号段。框架不硬编码业务模块枚举；导入后会在业务侧生成 `RedDotModuleId` 枚举和分模块 `RedDotIds` 常量，程序侧不会使用裸数字。

## 配置与导入

源文件：`Assets/RefData_Excel/RedDot.xlsx`。

修改后执行 `Framework/Config/Export All (Excel→代码+config.db)`：标准 ConfigData 管线生成 Ref/Table 类并导出
首包与热更配置库；红点专用步骤同时执行跨表校验并刷新 ID 常量。也可单独执行
`Tools/Framework/Red Dot/Import Configuration` 做拓扑校验和 ID 生成，但它不会替代配置数据库导出。

产物：

- `Assets/Scripts/HotUpdate/ConfigData/Data/RedDot*Ref.cs`：标准强类型配置行；
- `Assets/Scripts/HotUpdate/ConfigData/Table/RedDot*RefTable.cs`：标准配置加载器；
- `Assets/Scripts/HotUpdate/Generated/RedDotIds.g.cs`：确定性 ID 常量；
- `Assets/ResourcesOut/RefData/config.db.bytes`：Addressables 配置热更库；
- 五张表为 `ClientOnly`，不会导出到服务端。

`GameEntry.Awake` 只创建尚未初始化的 `RedDotService`。配置数据库在 `HotfixEntry.Start()` 之前完成安装；
`RedDotBootstrap` 随后读取五张 RefTable、组装并校验目录。离线整包在首次业务会话进入前执行相同装配。
拓扑、说明和已有 ID 之间的边关系可只发布 `config.db.bytes`；新增被业务代码引用的 ID 时，同时发布新的
`RedDotIds.g.cs` 所在 `HotUpdate.dll.bytes`。配置、资源和代码由现有内容事务保证同版本提交与回滚。

工作簿第三行是真实类型声明：普通字段使用 `int`/`string`，枚举列使用
`RedDotNodeKind`、`RedDotAggregation`、`RedDotAcknowledgeTrigger`、`RedDotSeenSaveMode`。
单元格下拉只是策划输入约束，编译器还会校验类型行和值，二者职责不同。

### red_dot_module_ref

定义模块 ID、`CodeName`、说明与互不重叠的节点 ID 号段。

### red_dot_node_ref

定义有效节点。策划只填写模块内短 `CodeName`，例如 `UpgradeAvailable`；不要填写
`Clicker.UpgradeAvailable`，模块前缀由 `ModuleId` 自动派生。`CodeName` 不表达层级关系。

`Type=Signal` 时 `Aggregation=None`；`Type=Aggregate` 时必须显式配置：

- `Any`：任一直接子节点大于 0，结果为 1；
- `SumChildren`：直接子节点求和，DAG 汇合可能重复；
- `MaxChildren`：直接子节点取最大；
- `SumUniqueSignals`：按底层唯一 Signal 去重求和。

### red_dot_edge_ref

无主键 `List` 关系表，一行一条直接边：`ParentId` 的结果依赖 `ChildId`。同一 ParentId/ChildId
可以出现在多行中，不需要 EdgeKey。三级关系写两条直接边，不保存 `100001_110001_120001` 完整路径；
同一 `ChildId` 可配置多个 `ParentId`。

### red_dot_seen_policy_ref

只填写“看过即消失”的弱提示。没有该行的 Signal 为业务状态驱动，`Acknowledge` 不会清除。

- `Trigger`：Enter / Expose / Click / Manual；
- `SaveMode`：Session / LocalAccount / ServerAccount；
- `Version`：同版本看过后保持隐藏，希望内容重新提示时递增。

当前框架已实现 Session 与 LocalAccount；ServerAccount 提供运行态导入/导出能力，具体后端同步由业务接入。

### red_dot_retired_ref

保存已退出使用的 ID，阻止复用。退休 ID 不进入运行时，不出现在搜索器中，旧 Prefab 引用会被构建门禁拦截。

## Provider 用法

业务模块实现 `IRedDotProvider`，拥有固定 Signal 集合。它首先是一份可随时重建的完整快照，保证登录、
切号、重连或整包数据覆盖后可以校准所有 Signal：

```csharp
public sealed class MailRedDotProvider : IRedDotProvider
{
    public string Owner => "Mail";
    public IReadOnlyCollection<int> OwnedSignalIds => new[]
    {
        RedDotIds.Mail.SystemUnread,
        RedDotIds.Mail.FriendUnread,
    };

    public bool IsReady => _model.IsInitialized;

    public void Collect(RedDotUpdateBuffer buffer)
    {
        buffer.Set(RedDotIds.Mail.SystemUnread, _model.UnreadSystemCount);
        buffer.Set(RedDotIds.Mail.FriendUnread, _model.UnreadFriendCount);
    }
}
```

登录/重连时提交完整模块快照；Coordinator 是账号会话对象，退出时必须 Dispose：

```csharp
_coordinator = new RedDotCoordinator(GameEntry.RedDots);
_coordinator.Register(new MailRedDotProvider(model));
_coordinator.Register(new TaskRedDotProvider(taskModel));
_coordinator.RebuildAll();

// 业务会话退出
_coordinator.Dispose();
```

完整快照中，Provider 拥有但没有写入的 Signal 自动归零，避免旧账号/旧快照红点残留。

### 响应式精确更新

正常运行不应让高频通用事件反复 `Refresh(owner)` 完整快照。需要精确监听 Model 领域事件的 Provider 可额外实现
`IReactiveRedDotProvider`。一次 `Bind` 可以注册任意数量的监听，返回的 `IDisposable` 是整组监听的统一释放句柄：

```csharp
public sealed class MailRedDotProvider : IRedDotProvider, IReactiveRedDotProvider
{
    // Owner / OwnedSignalIds / IsReady / Collect 与上文相同

    public IDisposable Bind(IRedDotWriter writer)
    {
        var bindings = new RedDotBindingGroup();
        bindings.Add(GameEntry.Event.Subscribe(
            MailEvents.SystemUnreadChanged,
            () => writer.SetCount(
                RedDotIds.Mail.SystemUnread,
                _model.UnreadSystemCount)));
        bindings.Add(GameEntry.Event.Subscribe(
            MailEvents.FriendUnreadChanged,
            () => writer.SetCount(
                RedDotIds.Mail.FriendUnread,
                _model.UnreadFriendCount)));
        return bindings;
    }
}
```

Coordinator 会自动调用 `Bind`，并提供只允许写 `OwnedSignalIds` 的模块作用域 `IRedDotWriter`；越权写其他模块
Signal 会立即抛错。Dispose Coordinator 时会先统一解绑，再把该会话 Provider 标记为 NotReady 并清零其 Signal。

一个事件影响多个已知 Signal 时使用 `writer.BeginBatch()`；模块完整数据被替换、不确定具体变化项时，在 Provider
监听中调用 `writer.RefreshSnapshot()` 重新收集自身完整快照。不需要事件监听的低频模块只实现
`IRedDotProvider`，由组合根按需调用 `coordinator.Refresh(owner)` 即可。

简单接线也可以由模块投影层直接增量校准绝对值：

```csharp
GameEntry.RedDots.SetCount(RedDotIds.Mail.SystemUnread, model.UnreadSystemCount);
```

但直接 `GameEntry.RedDots.SetCount` 不带 Provider 所有权校验，大型模块优先使用响应式 Provider 获得作用域写权限。
完整业务数据优先使用绝对值；只有事件绝对可靠时才使用 `AddCount`。

## 弱提示确认

`RedDotBadge` 只展示，不自动清除。页面在真实业务时机显式确认：

```csharp
GameEntry.RedDots.Acknowledge(
    RedDotIds.Activity.NewPage,
    RedDotAcknowledgeTrigger.Expose);
```

业务状态红点（奖励未领取、邮件未读等）不配置 SeenPolicy，必须在业务状态改变后重新 `SetCount`。

账号登录后，GameEntry 会在业务入口前加载 LocalAccount 已看版本；登出时在 SaveManager 切回 guest 前保存，并统一清空 Signal、Session Seen 和 Provider Ready 状态。红点计数与 Aggregate 结果不会落盘。

## UI

`RedDotBadge` 挂常驻按钮/页签，`Badge Root` 必须是独立子对象，直接配置稳定 ID：

- Inspector 可手输/粘贴 ID；
- “搜索”支持 ID、Key、描述和 ModuleId；
- 自动回显 Key、说明、类型和聚合方式；
- `DotOnly` 与 `Number` 是 UI 表现，不进入逻辑配置；
- 旧 `_path` 会保留在 `_legacyPath`，同 Key 时 Inspector 可一键迁移。

代码构建 UI 可调用：

```csharp
badge.Configure(redDotId, badgeRoot, countText, RedDotBadge.DisplayMode.Number, 99);
```

## 点击红点跳转到来源

`GetActivePath(id)` 从入口节点沿"有值"的子边逐层深入，返回一条到最深亮起 Signal 的路径（含入口本身）。
每层在多个亮起子节点中按 FinalCount 降序、ID 升序确定性择一，同一状态下路径稳定可复现；入口未点亮时返回
空列表。UI 可据此把玩家从大入口一路带到真正点亮红点的叶子功能：

```csharp
var path = GameEntry.RedDots.GetActivePath(RedDotIds.Main.Root);
// path[0] 是入口，path[^1] 是最深亮起 Signal；据此驱动逐级页面跳转
```

诊断命令：`reddot path <ID|Key>` 直接打印这条路径。

## 帧末合并（性能）

默认每次 `SetCount` 立即结算并通知（历史行为）。运行时由 `GameEntry` 在目录初始化后自动开启帧末合并：
批处理之外的写入只标脏，`LateUpdate` 帧末统一 `FlushPending()` 一次，把一帧内多个来源对同一子树的写入
合并为一次聚合与 UI 通知，避免重复计算与多次刷新。读接口（`GetCount`/`Snapshot`/`GetActivePath` 等）会
按需先行结算，保证"读到自己的写入"，因此业务读值时机不受影响。

- `SetFrameCoalescing(bool)`：手动开关（关闭时立即结算已累积的脏）；
- `FlushPending()`：帧驱动调用，返回是否发生结算；
- `HasPendingUpdates`：是否存在已标脏未结算的红点。

需要显式合并一组写入时仍用 `BeginBatch()`；帧末合并是对"零散写入"的兜底，二者可叠加。增量结算与通知
在稳定态零 GC 分配。

## 校验与排查

导入、Player Build 和 `CiGate` 会校验：

- 工作表字段及真实类型声明、枚举值；
- 模块/节点 ID、Key、号段与退休 ID；
- Signal/Aggregate 规则；
- 父子存在性、重复边、自依赖和环；
- SeenPolicy 类型、版本和触发方式；
- Prefab/Build Scene 的未知 ID、退休 ID、LegacyPath 与 Badge Root。
- `RedDotIds.g.cs` 是否与当前源表完全一致。

查看配置拓扑：`Tools/Framework/Red Dot/Topology`。

运行时命令：

```text
reddot
reddot 110001
reddot Clicker.UpgradeAvailable
reddot explain 100001
reddot path 100001
```

`explain` 会列出使 Aggregate 亮起的有效底层 Signal、Raw/Effective 值、Provider 和 Ready 状态；
`path` 打印从入口深入到最深亮起 Signal 的一条路径，对应 `GetActivePath`。

## 明确限制

- 红点拓扑在账号会话中不可变；配置更新下次初始化生效；
- 红点拓扑随 `config.db.bytes` 发布，但不在已运行的账号会话中替换，下次初始化生效；
- 新服务不提供含糊的全局 `TotalCount`，需要全局入口时显式配置 Aggregate；
- 新服务不提供 DAG 语义危险的 `ClearSubtree`；
- 仅主线程访问，且不能在订阅回调内反向修改红点；
- 不在配置表编写业务条件表达式，复杂条件归 Provider/Model；
- `ServerAccount` 已看同步的协议、重试与冲突策略由业务服务端实现。
