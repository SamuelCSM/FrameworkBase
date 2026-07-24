# FrameworkBase Camera — Cinemachine

把 Unity **Cinemachine** 的虚拟相机切换与 Impulse 震屏，接到 FrameworkBase 主干的
`ICameraDirector` 缝上。核心框架**零 Cinemachine 依赖**——这是可选扩展包，装了才生效。

> 定位与 Bugly 崩溃后端扩展包一致：**主干只定义接口（`ICameraDirector` / `Cameras`），
> 具体相机方案进独立扩展包**。不接本包时主干用 `NullCameraDirector` 无操作兜底，业务代码照常编译运行。

## 骨架说明

本包是**参考骨架**，不含 Cinemachine 包本身。所有 Cinemachine 调用锁在编译宏
`FRAMEWORKBASE_CINEMACHINE` 之后，未启用时退化为无操作，因此**没装 Cinemachine 也能编译通过**。

## 启用步骤

1. **装 Cinemachine**：Package Manager 安装 `com.unity.cinemachine`。
   - 装上后，本包 asmdef 的 `versionDefines` 会**自动**定义 `FRAMEWORKBASE_CINEMACHINE`，无需手动加宏。
2. **场景搭建**：
   - 每台 Cinemachine 虚拟相机挂上 `CinemachineDirectorCamera` 组件，填 `cameraId`（业务用它切换）。
   - 需要震屏时，给该导演注入一个 `CinemachineImpulseSource`：`((CinemachineCameraDirector)Cameras.Director).SetImpulseSource(src)`。
3. **业务调用**（无需感知 Cinemachine）：
   ```csharp
   Cameras.Director.Activate("battle");   // 切到名为 battle 的镜头，Brain 按其 blend 过渡
   Cameras.Director.Shake(2f, 0.3f);      // 震屏
   ```
   自注册入口 `CinemachineCameraBootstrap` 已在场景加载前把 `CinemachineCameraDirector` 注入
   `Cameras.Director`，业务零接线。

## Cinemachine 2.x / 3.x

骨架针对 **Cinemachine 2.x**（程序集 `Cinemachine`、类型 `CinemachineVirtualCamera` /
`CinemachineImpulseSource`）。若工程用 **3.x**（程序集 `Unity.Cinemachine`、类型 `CinemachineCamera`），
需两处调整：

- `Runtime/Framework.Integrations.CinemachineCamera.asmdef` 的 `references`：`"Cinemachine"` → `"Unity.Cinemachine"`。
- `CinemachineCameraDirector.cs` 里的 `using Cinemachine;`、`CinemachineVirtualCamera`、`.Priority`
  按 3.x API 改（3.x 优先级为 `Priority.Value`，虚拟相机类型为 `CinemachineCamera`）。

## 未验证边界

骨架的 Cinemachine-active 路径（优先级切换、Impulse）在提交时**未经真机/CI 编译验证**（当时工程未装
Cinemachine）。落地时请：装 Cinemachine → 让 `FRAMEWORKBASE_CINEMACHINE` 生效 → 跑一次编译与真机切换/震屏冒烟。
无 Cinemachine 的无操作路径已随核心 CI 编译验证。
