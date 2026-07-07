# Addressables 分组打包规范

## 目录约定（唯一规则，记住这一条就够）

```
Assets/ResourcesOut/[组名]/任意/层级/文件.ext
  → 分组 = 一级目录名（自动创建）
  → 地址 = 组名/任意/层级/文件（无扩展名）
  → label = remote（热更下载按它聚合）
```

新增一级目录 = 新增分组，无需改代码。导入时 `AddressableAutoRegistrar` 自动注册；
批量变更后执行 **Framework → Register Assets (Sync ResourcesOut)** 全量对齐。

## 分组划分原则

- **按更新频率拆组**，不是按资源类型：高频改动的活动资源独立成组，
  稳定的基础 UI 另成组——改一个资源只让玩家重下它所在的组。
- **场景必须独立成组**（Addressables 硬性限制，混包直接构建失败）。
- **共享资源显式注册**：被多个组引用的字体/图集/通用贴图，放进独立共享目录
  （如 `ResourcesOut/Shared/`）。不注册的共享依赖会被**每个引用组各拷贝一份**
  ——包体膨胀 + 运行时双份内存，这是 Addressables 第一大坑。
- **本地组白名单**：只有 `Framework` 组随包内置（Local 路径），其余一律 Remote。
  需要新增随包组时，同步更新校验阈值的 `LocalGroups` 白名单。

## 深度校验器

入口：菜单 **Framework → Validate Addressables**；
构建玩家包时 `AddressablesBuildCheck` 自动执行（Error 终止构建）；
整包发布 `PrepareFullPackage` 构建 Addressables 前同样拦截。

| 规则 | 级别 | 说明 / 修复 |
|---|---|---|
| GroupMissingSchema | Error | 组缺 BundledAssetGroupSchema，条目不参与打包——删组或补 Schema |
| LocalGroupWrongPath / RemoteGroupWrongPath | Error | 本地/远端路径错配：热更资源被焊死进包或本地资源误传 CDN——改组 Schema 的 Build/Load Path |
| SceneMixedWithAssets | Error | 场景与普通资产混包——场景移到独立组 |
| AddressMismatch | Warning | 地址不符合目录推导——执行 Register Assets (Sync) 自动修正 |
| MissingRemoteLabel | Warning | 远端条目缺 remote label，启动预下载统计不到它——Sync 会自动补 |
| DuplicateImplicitDependency | Warning | 隐式依赖被多组重复打包（按体积降序报告）——移入共享目录显式注册 |
| AssetOverBudget | Warning | 单资产源体积 > 32MB——压缩或拆分 |
| GroupOverBudget | Warning | 组源资产总体积 > 256MB——按功能/更新频率拆组 |
| UnregisteredManagedAsset | Warning | ResourcesOut 内有资产未注册（同步漂移）——执行 Sync |
| EmptyGroup | Warning | 空组是配置垃圾——删除 |

体积为**源资产磁盘体积**（构建前代理指标，bundle 压缩后更小）；阈值在
`AddressablesValidator.DefaultThresholds()` 调整。

## 架构说明（扩展规则时读）

校验器三层分离，规则可单测：

- `AddressablesValidation.cs` —— 纯规则引擎（模型 + 规则，不碰 AssetDatabase），
  测试见 `Tests/EditMode/AddressablesValidationTests.cs`；
- `AddressablesValidator.cs` —— 采集层（Settings/AssetDatabase → 模型）+ 报告输出；
- `AddressablesBuildCheck.cs` —— 构建门禁（IPreprocessBuildWithReport，order -90）。

新增规则：模型缺字段先补模型（采集层同步填充），规则写进
`AddressablesValidationRules.Validate`，补一条单测，最后把规则登记进上表。
