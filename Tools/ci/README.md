# CI / 构建门禁

FrameworkBase 主干要求：干净副本可复现、Unity 编译通过、EditMode/PlayMode 全绿、资源门禁通过，并能生成 Android Player 与 iOS Xcode 工程。

## 本地质量门禁

`powershell
.\Tools\ci\run-ci.ps1 -UnityPath "H:\Hub\2022.3.62f3\Editor\Unity.exe"
`

执行顺序：

1. `check-reproducibility.ps1`：关键 ProjectSettings、Addressables、AppConfig、packages-lock 必须存在并已纳入 Git；
2. EditMode 测试；
3. required 资源门禁；
4. 可选包体大小门禁；
5. PlayMode 冒烟。

## GitHub Actions

`.github/workflows/ci.yml` 依次执行：

- 干净副本可复现性预检；
- EditMode + PlayMode 测试；
- required 资源门禁（失败抛异常，不再 `continue-on-error`）；
- Android Player/IL2CPP 构建验证；
- iOS Xcode 工程生成验证。

仓库 Secrets：`UNITY_LICENSE`、`UNITY_EMAIL`、`UNITY_PASSWORD`。

## 热更新发布 CLI

`powershell
Unity.exe -batchmode -nographics -projectPath <工程根> ^
  -executeMethod Framework.Editor.Release.ReleaseBatchEntry.PublishHotUpdate ^
  -releaseEnv dev -buildTarget Android -appVersion 1.0.0 ^
  -resourceVersion 2 -codeVersion 2 -publishResource true -publishCode true ^
  -uploadRoot D:\cdn-root\updates -logFile Logs/release-hotupdate.log
`

## 整包发布 CLI

`powershell
Unity.exe -batchmode -nographics -projectPath <工程根> ^
  -executeMethod Framework.Editor.Release.ReleaseBatchEntry.BuildFullPackage ^
  -releaseEnv prod -buildTarget Android -appVersion 2.0.0 ^
  -buildOutput Builds/Android/Game.aab -uploadRoot D:\cdn-root\updates ^
  -updateUrl https://store.example.net/game -logFile Logs/release-full.log
`

正式发布默认要求 Git 工作区干净；仅调试可显式传 `-allowDirtyRelease`。

## 签名私钥注入

所有环境的远程清单都必须签名。CI 从密钥系统注入以下任一环境变量：

- `FRAMEWORKBASE_MANIFEST_PRIVATE_KEY_XML_BASE64`；
- `FRAMEWORKBASE_MANIFEST_PRIVATE_KEY_PATH`。

私钥不得提交到仓库、AppConfig 或 Player 包体。

## 包体大小门禁

缺少产物目录、产物为空或缺少基线都会失败。只有显式传入 `-buildSizeUpdateBaseline` 才允许创建或更新基线，普通 PR 不会自动接受新基线。

基线引导（首次武装门禁）：

1. 本地或 CI 产出一次 Android 构建产物；
2. Editor 菜单「Framework/发布/更新包体基线」选择产物目录，或 batchmode 传
   `-buildSizeUpdateBaseline -buildSizeDir <产物目录>`；
3. 将生成的 `Tools/ci/build-size-baseline.json` 随 PR 提交评审。

基线入库后，ci.yml 的 android-player job 会自动强制执行包体回归比对；基线缺失期间
CI 只发醒目告警不阻断，属于引导期的显式豁免，不是门禁默认放行。

## 云端正式发布（release.yml）

正式产物只允许由 `.github/workflows/release.yml`（workflow_dispatch）生成：

- 入口固定为 `ReleaseBatchEntry.PublishHotUpdateForBuilder` / `BuildFullPackageForBuilder`，
  失败以 BuildFailedException 上抛，不依赖 batchmode 退出码；
- job 绑定 GitHub Environment（dev/qa/prod）：prod 在 Settings → Environments 配置
  Required reviewers 即形成发布审批；签名私钥用环境 Secret
  `MANIFEST_PRIVATE_KEY_XML_BASE64` 注入，按环境隔离；
- 发布 staging 与 ledger 台账整体作为 workflow artifact 归档 90 天，可回答
  “谁在哪个 Commit 用什么配置发布了哪些产物”。
