# Addressables 分组与打包指南

这份文档说明本框架内 Addressables 的目录约定、分组原则、同步流程、发布模式与常见校验问题。

## 核心目录约定

资源只要放到这个目录下：

```text
Assets/ResourcesOut/[分组名]/任意/层级/文件.ext
```

框架会自动得到：

```text
分组 = 第一级目录名
地址 = 从分组名开始、不带扩展名的路径
label = remote
```

约定摘要：

- 分组 = 一级目录名，由工具自动创建。
- 地址 = 组名/任意/层级/文件，不带扩展名。
- label = `remote`，热更下载按它聚合。
- 新增一级目录 = 新增分组，无需改代码。
- 导入时 `AddressableAutoRegistrar` 自动注册。
- 批量变更后执行 `Framework -> Register Assets (Sync ResourcesOut)` 全量对齐。

例子：

```text
Assets/ResourcesOut/UI/Login/LoginWindow.prefab
```

会变成：

```text
分组: UI
地址: UI/Login/LoginWindow
label: remote
```

代码加载时写地址，不写文件扩展名：

```csharp
await GameEntry.Resource.LoadAssetAsync<GameObject>("UI/Login/LoginWindow");
```

## 日常操作流程

1. 把要热更的资源放进 `Assets/ResourcesOut/` 下面。
2. 用第一级目录决定分组，比如 `UI`、`ActivitySpring`、`Shared`、`Scenes`。
3. 回 Unity 等它导入。自动注册正常时，不需要手动勾 Addressable。
4. 大批量搬资源、改目录、删资源后，执行菜单：

```text
Framework -> Register Assets (Sync ResourcesOut)
```

5. 打包或发布前，执行菜单检查：

```text
Framework -> Validate Addressables
```

常规流程做到这 5 步即可覆盖大部分资源导入、同步和发布前检查场景。

## 什么是分组

Addressables 会把资源打成 bundle。分组就是告诉 Unity：哪些资源大致放到同一批 bundle 里管理。

在本框架里，分组不是你在 Addressables 窗口里手动维护出来的，而是由目录自动推导出来的：

```text
Assets/ResourcesOut/UI/xxx.prefab              -> UI 组
Assets/ResourcesOut/ActivitySpring/xxx.png     -> ActivitySpring 组
Assets/ResourcesOut/Shared/xxx.mat             -> Shared 组
```

新增一个一级目录，就等于新增一个分组。

## 分组划分原则

最简单的原则：按“更新频率”和“生命周期”拆，不按文件类型拆。

核心原则：

- 按更新频率拆组，不按资源类型拆组；高频改动的活动资源独立成组，稳定的基础 UI 另成组。
- 场景必须独立成组；场景和普通资源混在一起，Addressables 构建阶段容易直接失败。
- 共享资源显式注册；被多个组引用的字体、图集、通用贴图放进独立共享目录，例如 `ResourcesOut/Shared/`。
- 未注册的共享依赖会被每个引用组各拷贝一份，导致包体膨胀和运行时双份内存。
- 本地组白名单默认只有 `Framework`，它走 Local 路径随包内置；其余自动分组一律 Remote。
- 需要新增随包组时，同步更新校验阈值的 `LocalGroups` 白名单。

推荐这样拆：

```text
Assets/ResourcesOut/UI/...
Assets/ResourcesOut/Shared/...
Assets/ResourcesOut/Scenes/...
Assets/ResourcesOut/ActivitySpring/...
Assets/ResourcesOut/ActivitySummer/...
Assets/ResourcesOut/AudioCommon/...
```

不推荐这样拆：

```text
Assets/ResourcesOut/Prefabs/...
Assets/ResourcesOut/Textures/...
Assets/ResourcesOut/Materials/...
Assets/ResourcesOut/Audio/...
```

原因是玩家更新时关心的是“这次改了哪个功能”，不是“这次改的是 prefab 还是 png”。如果你把所有贴图都塞进 `Textures` 组，改一个活动贴图，可能导致玩家重新下载一大组完全无关的贴图。

## 常见资源应该放哪里

通用 UI：

```text
Assets/ResourcesOut/UI/Common/Button.prefab
Assets/ResourcesOut/UI/Login/LoginWindow.prefab
```

活动资源：

```text
Assets/ResourcesOut/ActivitySpring/Window.prefab
Assets/ResourcesOut/ActivitySpring/Textures/bg.png
```

多个组都会用到的资源：

```text
Assets/ResourcesOut/Shared/Fonts/MainFont.asset
Assets/ResourcesOut/Shared/Atlases/CommonAtlas.spriteatlas
Assets/ResourcesOut/Shared/Materials/UIMask.mat
```

场景：

```text
Assets/ResourcesOut/Scenes/Login/Login.unity
Assets/ResourcesOut/Scenes/Battle/Battle.unity
```

如果某个场景特别大、更新频率和其它场景不同，可以单独拆：

```text
Assets/ResourcesOut/SceneBattle/Battle.unity
Assets/ResourcesOut/SceneHome/Home.unity
```

注意：场景不要和普通 prefab、图片、材质混在同一个组里。Addressables 构建时对场景混包很敏感，框架校验器也会把它拦下来。

## 共享资源目录 Shared

这是 Addressables 最容易踩的坑。

假设两个活动都引用同一张通用贴图：

```text
ActivitySpring/Window.prefab -> Assets/Art/Common/title.png
ActivitySummer/Window.prefab -> Assets/Art/Common/title.png
```

如果 `title.png` 没有自己注册成 Addressable，它会变成两个活动组的“隐式依赖”，最后可能被两个 bundle 各打一份。

后果：

```text
包体变大
内存可能出现两份同名资源
引用比较可能不符合预期
```

修复方式：把共享资源也放进 `ResourcesOut`，给它一个明确分组。

```text
Assets/ResourcesOut/Shared/title.png
```

然后让两个 prefab 都引用这份资源。校验器里的 `DuplicateImplicitDependency` 就是在帮你抓这个问题。

## 本地组和远端组

本框架默认只有一个本地组：

```text
Framework
```

它走 Local 路径，表示随包内置。

其它通过 `ResourcesOut/[分组名]` 自动创建的组，默认都走 Remote 路径，表示可以热更下载。

也就是说：

```text
Framework -> 首包内置
UI / Shared / ActivitySpring / Scenes -> 默认远端热更
```

如果你想新增“必须随包内置”的业务组，不要只在 Addressables 窗口里改路径，还要同步更新校验器白名单，否则构建检查会认为它配错了。

## 两种发布模式

框架里有两个 Addressables Profile：

```text
HotUpdateRemote
FullPackageLocal
```

`HotUpdateRemote`：日常热更模式。远端组使用 Remote 路径，资源可上传 CDN。

`FullPackageLocal`：整包模式。把原本远端的资源临时转成随包加载，用来做“首包内置完整资源”的发布。

日常理解可以很简单：

```text
开发和热更发布 -> HotUpdateRemote
完整新包发布   -> FullPackageLocal
```

整包发布工具会自动切 Profile、构建 Addressables，并在失败时尽量回滚。你手动操作时，记得检查当前 Active Profile，别把开发环境停在错误的 Profile 上。

## 一个新资源从导入到加载的完整例子

目标：新增登录窗口 prefab。

1. 放文件：

```text
Assets/ResourcesOut/UI/Login/LoginWindow.prefab
```

2. 等 Unity 导入。自动注册成功时，Console 会看到类似：

```text
[AutoRegister] 注册 [UI] UI/Login/LoginWindow
```

3. 如果没看到，或你是批量复制进来的，执行：

```text
Framework -> Register Assets (Sync ResourcesOut)
```

4. 校验：

```text
Framework -> Validate Addressables
```

5. 代码里用地址加载：

```csharp
await GameEntry.Resource.LoadAssetAsync<GameObject>("UI/Login/LoginWindow");
```

## 新活动怎么建组

活动名叫 SpringFestival，建议直接建独立组：

```text
Assets/ResourcesOut/ActivitySpringFestival/Window.prefab
Assets/ResourcesOut/ActivitySpringFestival/Textures/bg.png
Assets/ResourcesOut/ActivitySpringFestival/Audio/open.ogg
```

如果活动下线，整组资源可以一起删除或停止发布。以后活动只改自己的资源，玩家也只需要下载这个活动对应的更新。

如果活动用到了通用按钮、字体、材质，把这些放到：

```text
Assets/ResourcesOut/Shared/...
```

不要复制一份到每个活动目录里。

## 文件名和地址规则

资源地址不带扩展名。

```text
Assets/ResourcesOut/UI/Login/LoginWindow.prefab
地址: UI/Login/LoginWindow
```

`config.db.bytes` 这种双扩展名，只会去掉最后一个扩展名：

```text
Assets/ResourcesOut/RefData/config.db.bytes
地址: RefData/config.db
```

所以配置库热更默认地址是：

```text
RefData/config.db
```

## 支持自动注册的文件类型

当前自动注册这些扩展名：

```text
.prefab .unity .mat .png .jpg .jpeg
.wav .mp3 .ogg .anim .controller
.asset .fbx .obj .bytes
```

如果新增了别的资源类型，先确认它是否真的需要通过 Addressables 加载。需要的话，再把扩展名补进 `AddressablesSetup.cs` 的 `SupportedExt`。

## 打包前检查

入口：

```text
Framework -> Validate Addressables
```

结果分两类：

```text
Error   必须修，不修会阻止构建
Warning 建议修，不一定阻止构建，但通常意味着包体、下载、内存或维护风险
```

常见问题：

| 规则 | 级别 | 问题说明 | 修复方式 |
|---|---|---|---|
| GroupMissingSchema | Error | 这个组没有 bundle 打包配置 | 删除异常组，或补 BundledAssetGroupSchema |
| LocalGroupWrongPath | Error | 本地组路径配错了 | `Framework` 组应使用 Local Build/Load Path |
| RemoteGroupWrongPath | Error | 热更组路径配错了 | 除白名单本地组外，都应使用 Remote Build/Load Path |
| SceneMixedWithAssets | Error | 场景和普通资源混在同一组 | 把场景移到独立一级目录 |
| AddressMismatch | Warning | 地址不是目录推导出来的标准地址 | 执行 Register Assets (Sync ResourcesOut) |
| MissingRemoteLabel | Warning | 远端资源没打 `remote` label | 执行 Register Assets (Sync ResourcesOut) |
| DuplicateImplicitDependency | Warning | 同一个依赖被多个组重复打包 | 把它放进 `ResourcesOut/Shared/` 显式注册 |
| AssetOverBudget | Warning | 单个源文件太大 | 压缩、拆分，或确认确实需要这么大 |
| GroupOverBudget | Warning | 单组太大，更新粒度过粗 | 按功能或更新频率继续拆组 |
| UnregisteredManagedAsset | Warning | `ResourcesOut` 里有资源没注册 | 执行 Register Assets (Sync ResourcesOut) |
| EmptyGroup | Warning | 空组没有意义 | 删除空组 |
| RemoteBundleNamingNoContentHash | Warning | 远端组 Bundle 命名不含内容哈希（NoHash / FileNameHash），内容热更后 CDN 可能供旧字节 | 组 Schema 的 Bundle Naming 改 Append Hash 或 Use Hash of AssetBundle |
| RemoteBundleUncompressed | Warning | 远端组 Bundle 未压缩，下载体积翻数倍 | 组 Schema 的 Compression 改 LZ4 |
| CoarsePatchGranularity | Warning | 远端组 PackTogether 且条目过多，改一个资源要重下整包 | 按更新频率拆组，或改 Pack Separately |

## 常用修复操作

遇到地址、label、漏注册、删除后残留，优先跑：

```text
Framework -> Register Assets (Sync ResourcesOut)
```

它是幂等的，可以反复执行。它会扫描 `Assets/ResourcesOut`，让 Addressables Settings 和磁盘目录重新对齐。

## 不建议做的事

不要把业务资源直接放到 `Assets/ResourcesOut` 根目录。根目录下没有一级分组名，自动注册会跳过。

不要手动把一堆资源拖进 Addressables 窗口随便建组。这样会绕开目录约定，后续同步和校验都会更难维护。

不要按资源类型粗暴建 `Textures`、`Prefabs`、`Materials` 大组。活动或功能更新时会牵连太多无关资源。

不要让多个活动各复制一份通用字体、图集、材质。共享资源应放进 `Shared`。

不要把场景和普通资源放进同一组。

## 什么时候需要手动打开 Addressables 窗口

大多数日常工作不需要打开。只有这些情况建议打开看：

```text
想确认某个资源是否已注册
想看某个组的 BuildPath / LoadPath
想删除历史遗留的异常空组
想排查 Unity Addressables 自身构建报错
```

正常增删资源，优先靠目录和菜单同步。

## 架构说明（扩展规则时读）

校验器三层分离，规则可单测：

```text
AddressablesValidation.cs       纯规则引擎，不碰 AssetDatabase
AddressablesValidator.cs        从 Settings / AssetDatabase 采集模型并输出报告
AddressablesBuildCheck.cs       构建门禁，玩家包构建前自动执行
```

新增规则时：

1. 模型缺字段就先补 `AddressablesValidationModel`。
2. 采集层在 `AddressablesValidator.cs` 填字段。
3. 规则写进 `AddressablesValidationRules.Validate`。
4. 在 `Tests/EditMode/AddressablesValidationTests.cs` 补单测。
5. 把规则补进上面的规则表。
