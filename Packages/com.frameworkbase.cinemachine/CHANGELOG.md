# Changelog

本扩展包遵循 [语义化版本](https://semver.org/lang/zh-CN/)。

## [0.1.0] - 2026-07-23

### 新增

- 首个骨架版本：把 Cinemachine 虚拟相机优先级切换与 Impulse 震屏接到主干 `ICameraDirector`。
  - `CinemachineCameraDirector`：实现 `ICameraDirector`，`Activate(id)` 抬优先级切换、`Shake` 走 Impulse；
    Cinemachine 调用锁在 `FRAMEWORKBASE_CINEMACHINE` 之后（asmdef `versionDefines` 探测 `com.unity.cinemachine`
    时自动开启），未启用退化为无操作、无 Cinemachine 也能编译。
  - `CinemachineDirectorCamera`：挂在虚拟相机上的自登记组件（按 `cameraId`）。
  - `CinemachineCameraBootstrap`：`RuntimeInitializeOnLoadMethod` 自注入 `Cameras.Director`（仅启用宏时）。
  - 骨架针对 Cinemachine 2.x；3.x 调整见 README。Cinemachine-active 路径需装 Cinemachine 后经真机/CI 验证。
