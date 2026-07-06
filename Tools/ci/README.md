# CI / 构建门禁

FrameworkBase 的质量门禁只有一条纪律：**主干任何提交必须"编译通过 + EditMode 测试全绿"**。
同一套门禁提供两个执行入口，行为等价：

| 入口 | 触发方式 | 适用场景 |
|---|---|---|
| `Tools/ci/run-ci.ps1` | 手动 | 提交前本机自查（需先关闭 Unity 编辑器） |
| `.github/workflows/ci.yml` | push / PR 自动 | GitHub 远端门禁 |

## 本地执行

**最简：双击仓库根的 `run-ci.bat`**（自动调用本脚本，结束停留显示结果）。

或 PowerShell：

```powershell
# 工程根目录（先关闭 Unity 编辑器，batchmode 需要独占工程）
.\Tools\ci\run-ci.ps1
# Unity 不在常见 Hub 路径时：
.\Tools\ci\run-ci.ps1 -UnityPath "H:\Hub\2022.3.62f3\Editor\Unity.exe"
```

产物写入 `Logs/ci/`（已 gitignore）：`editmode-results.xml`（NUnit 格式）与 `editmode.log`。
退出码 0 = 通过。

## GitHub Actions 首次启用

工作流用 [GameCI](https://game.ci/docs/github/getting-started) 在容器内跑 Unity，需要一次性配置许可：

1. 本机生成激活请求文件：
   `Unity.exe -batchmode -createManualActivationFile -quit` → 得到 `Unity_v2022.3.62f3.alf`
2. 到 <https://license.unity3d.com/manual> 上传 `.alf`，下载 `.ulf` 许可文件；
3. 仓库 Settings → Secrets and variables → Actions 添加三个 Secret：
   - `UNITY_LICENSE`：`.ulf` 文件的完整文本内容
   - `UNITY_EMAIL` / `UNITY_PASSWORD`：Unity 账号
4. push 触发即可；测试结果作为 artifact 上传，PR 上有 `EditMode Test Results` 检查项。

## 构建机出包（batchmode）

Player 构建入口见 `Framework.Editor.BuildEntry`（[BuildEntry.cs](../../Packages/com.frameworkbase.core/Editor/BuildEntry.cs)）：

```bat
Unity.exe -batchmode -nographics -projectPath <工程根> -buildTarget StandaloneWindows64 ^
  -executeMethod Framework.Editor.BuildEntry.BuildPlayer ^
  -outputPath Builds/Windows/Game.exe -logFile Logs/build.log
```

- `-development`：可选，Development Build + ScriptDebugging；
- 方法内部自行 `EditorApplication.Exit(0/1)`，**不要**再传 `-quit`；
- 打包前置校验（如热更安全检查 `HotUpdateSecurityBuildCheck`：prod + 明文 HTTP 直接构建失败）
  经 `IPreprocessBuildWithReport` 自动生效。

## 已知边界

- 整包全流程（Addressables 构建 → RefData 导出 → StreamingAssets 同步 → BuildPlayer）目前仍在
  `FullPackagePublisherWindow` 的 Editor 窗口管线里，尚未全部抽成 batchmode 入口；
  `BuildEntry.BuildPlayer` 覆盖标准 Player 构建。全流程 CLI 化在框架路线的后续项中。
- CI 的 EditMode 测试不含真机行为（IL2CPP / 移动平台），出包验证仍需构建机跑对应平台。
