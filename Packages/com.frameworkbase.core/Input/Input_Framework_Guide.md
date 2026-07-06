# 输入框架使用手册

本文档说明当前项目 `Framework.Input` 的推荐用法。简要规则写在仓库根目录 `AGENTS.md`，具体 API 与接入示例以本文档为准。

## 设计目标

输入框架把 **设备采样**、**输入门禁**、**业务意图** 三层分离：

```
设备层（Legacy 鼠标/触控/滚轮）
    ↓
Framework.Input（PointerSnapshot / PinchPanFrame / InputGate）
    ↓
业务域（BattleInputIntent、Login 按钮、相机手势等）
```

当前实现基于 **Legacy Input Manager**，通过 `IPointerInputSource` / `IPinchPanGestureSource` 接口预留后续迁移 **Unity New Input System** 的空间。

## 全局入口

`GameEntry` 初始化后可通过以下静态属性访问：

| 成员 | 说明 |
|------|------|
| `GameEntry.Input` | 全局输入管理器 |
| `GameEntry.Input.PrimaryPointer` | 当前主指针快照（单指点击/拖拽） |
| `GameEntry.Input.PinchPan` | 本帧双指/滚轮手势 |
| `GameEntry.Input.Gate` | 输入门禁（UI 命中、全局屏蔽） |
| `GameEntry.Input.Blocks` | 输入屏蔽栈 |
| `GameEntry.Input.IsMultiPointerGestureActive` | 是否处于多指手势态 |

`InputManager` 在 `GameEntry.InitializeManagers()` 中创建，并通过 `SetBootstrap(UIBootstrap)` 注入 `EventSystem`，用于判断指针是否位于 UGUI 上。

## 指针快照 PointerSnapshot

统一鼠标与触控语义，业务层只读该结构，不直接调用 `Input.GetMouseButton`：

```csharp
PointerSnapshot pointer = GameEntry.Input.PrimaryPointer;

if (pointer.WasPressedThisFrame) { /* 本帧刚按下 */ }
if (pointer.IsPressed)           { /* 当前仍按住 */ }
if (pointer.WasReleasedThisFrame){ /* 本帧刚抬起 */ }

Vector2 screenPos = pointer.Position;
int pointerId = pointer.PointerId; // 鼠标=0，触控=fingerId
```

### 采样规则

- **PC**：使用鼠标左键作为主指针。
- **移动端**：存在 Touch 时优先读 Touch，避免与模拟鼠标重复计数。
- **主指针稳定性**：触控模式下，从按下到抬起期间主 pointerId 保持不变。

## 输入门禁 InputGate

场景 3D 交互前应检查门禁，避免点击穿透 UI：

```csharp
if (!GameEntry.Input.Gate.AllowScenePointer(pointer.PointerId))
{
    return;
}
```

| 方法 | 用途 |
|------|------|
| `AllowScenePointer(pointerId)` | 场景单指交互（选块、落子等） |
| `AllowCameraGesture()` | 相机缩放/平移（不受 UI 命中限制，仍受全局屏蔽影响） |
| `IsGloballyBlocked` | 是否被 `InputBlockStack` 冻结 |
| `IsPointerOverUi(pointerId)` | 指针是否在 UGUI 上 |

## 输入屏蔽 InputBlockStack

用于 Loading、登录鉴权、落子提交等需要冻结场景输入的阶段。

### 推荐：作用域写法

```csharp
using Framework.Input;

using (InputBlockScope.Begin("MyModal"))
{
    // 此代码块内：场景指针、相机手势均被 Gate 拦截
    await DoSomethingAsync();
}
// 离开 using 后自动 Pop
```

### 手动 Push / Dispose

```csharp
InputBlockHandle handle = GameEntry.Input.Blocks.Push("Committing");
try
{
    await SubmitAsync();
}
finally
{
    handle?.Dispose();
}
```

多层 Push 会叠加计数，必须对应 Pop / Dispose，顺序不限（内部按句柄移除）。

## 双指缩放 / 平移 PinchPanFrame

| 平台 | 缩放 | 平移 |
|------|------|------|
| 移动端 | 双指捏合 | 双指中点拖动 |
| PC | 滚轮 | 中键拖拽 |

消费示例（业务项目的场景相机手势控制器按此模式消费）：

```csharp
PinchPanFrame frame = GameEntry.Input.PinchPan;
if (!frame.IsActive || !GameEntry.Input.Gate.AllowCameraGesture())
{
    return;
}

if (Mathf.Abs(frame.ZoomFactor - 1f) > 0.0001f)
{
    cameraRig.ApplyPinchZoomFactor(frame.ZoomFactor);
}

if (frame.PanDelta.sqrMagnitude > 0.01f)
{
    cameraRig.ApplyScreenPanDelta(frame.PanDelta);
}
```

双指手势期间，`IsMultiPointerGestureActive == true`，场景单指交互应让路（业务侧单指输入控制器须判断此标记并 early return）。

## 已接入的业务场景

| 场景 | 屏蔽原因 | 接入位置 |
|------|----------|----------|
| 启动 Loading | `LaunchLoading` | `LaunchFlow.RunStepsAsync` |
| 登录鉴权 | `LoginAuthenticating` / `LoginRequest` | `LoginFlow` |
| 业务关键提交（示例） | 自定义屏蔽层（如 `XxxCommitting`） | 业务表现层 |
| 场景 3D 输入（示例） | 走 `PrimaryPointer` + `Gate` | 业务输入控制器 |
| 场景相机手势（示例） | 走 `PinchPan` | 业务相机手势控制器 |

## 新业务接入指南

### 场景 3D 交互（HotUpdate）

1. 在 `Tick` 中读取 `GameEntry.Input.PrimaryPointer`。
2. 按下时调用 `Gate.AllowScenePointer(pointerId)`。
3. 多指手势期间检查 `IsMultiPointerGestureActive` 并跳过单指逻辑。
4. 将原始输入转换为领域 Intent，再驱动 Controller / 状态机。

```csharp
public void Tick(float deltaTime)
{
    InputManager input = GameEntry.Input;
    if (input == null || input.IsMultiPointerGestureActive)
    {
        return;
    }

    PointerSnapshot pointer = input.PrimaryPointer;
    if (pointer.WasPressedThisFrame && !input.Gate.AllowScenePointer(pointer.PointerId))
    {
        return;
    }

    // ... 转换为业务 Intent
}
```

### UI 窗口（Framework / HotUpdate）

- **UGUI 按钮、拖拽**：继续走 `EventSystem` + `UIEventListener` / `UIExtensions`，不要重复实现点击。
- **全屏 Loading / 模态**：用 `InputBlockScope.Begin("Reason")` 冻结背后场景输入。
- **异步操作期间**：在 `try/finally` 或 `using` 中保证屏蔽一定释放。

### 需要冻结输入的异步流程

```csharp
using (InputBlockScope.Begin("DownloadContent"))
{
    await GameEntry.Resource.LoadAsync(...);
}
```

## 业务侧常见可调参数（模式参考）

- 拖拽触发阈值：PC 与移动端应分开配置（业务输入 View 上暴露，参考值 30px / 44px）。
- 手势缩放范围：业务相机配置上暴露 min/max 缩放。
- 相机复位：业务相机 Rig 提供重置玩家缩放/平移、恢复基准取景的方法。

## 注意事项

### 命名空间冲突

项目存在 `Framework.Input` 命名空间，与 `UnityEngine.Input` 同名。在 `Framework.Input` 程序集内访问 Unity 旧输入 API 时，必须写 **`UnityEngine.Input`**，不能写 `Input`。

### UGUI 与场景输入

- 场景输入必须尊重 `InputGate`，避免 HUD 按钮点击同时触发棋盘。
- 相机手势 intentionally 不检查 UI 命中，以便在棋盘区域上方捏合缩放。

### 生命周期

- `InputManager` 随 `GameEntry` 存活，`DontDestroyOnLoad` 场景切换不销毁。
- 业务侧 `Push` 的屏蔽层必须在离开阶段 / `Dispose` / `finally` 中释放，避免永久锁死输入。

## Legacy vs New Input System（后续迁移）

| 维度 | Legacy（当前） | New Input System（规划） |
|------|----------------|--------------------------|
| 设备抽象 | 手写 `LegacyPointerInputSource` | 替换为 `NewInputSystemPointerSource` |
| 多指/触控 ID | 自行维护 | 内置 Pointer API |
| UGUI | `StandaloneInputModule` | `InputSystemUIInputModule` |
| 重绑定 | 不支持 | `InputAction` + 可视化配置 |
| 业务改动 | — | **无需修改** `InputController` / Intent 层 |

迁移时只需在 `InputManager.OnInit` 中替换 Source 实现，并更新 `UIBootstrap` 的 Input Module。

## 目录结构

```
Packages/com.frameworkbase.core/Input/
├── InputManager.cs              # 全局管理器
├── InputGate.cs                 # 输入门禁
├── InputBlockStack.cs           # 屏蔽栈
├── InputBlockScope.cs           # using 作用域辅助
├── PointerSnapshot.cs           # 指针快照
├── PinchPanFrame.cs             # 手势帧
├── LegacyPointerInputSource.cs  # Legacy 指针采样
├── LegacyPinchPanGestureSource.cs
├── IPointerInputSource.cs       # 可替换接口
└── IPinchPanGestureSource.cs
```

## 常见问题

**Q: 为什么 Launch Loading 期间还能点到后面的东西？**  
A: 确认 `LaunchFlow` 使用了 `InputBlockScope.Begin("LaunchLoading")`，且 `GameEntry.Input` 已在 `Awake` 中初始化。

**Q: 移动端点击 UI 仍触发棋盘？**  
A: 检查 `UIBootstrap` 是否显式配置了全局唯一 `EventSystem`；`InputManager.SetBootstrap` 必须在 `InitializeManagers` 中调用。

**Q: 双指缩放时误触选块？**  
A: 单指逻辑需判断 `GameEntry.Input.IsMultiPointerGestureActive` 并 early return。

**Q: 业务关键提交期间仍有输入？**  
A: 业务表现层应在提交阶段 Push 自己的屏蔽层（如 `XxxCommitting`）；若仍有输入，检查是否绕过了 `InputGate`。
