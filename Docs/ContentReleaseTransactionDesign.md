# 内容发行事务设计（Content Release Transaction）

> 阶段 0 审计结论 + 阶段 1~4 实施设计。本文对应"资源更新失败误判为成功 / 代码-Catalog-配置无统一事务 /
> 发布工作流无真实部署目标 / CI 缺 PR 门禁"四个 P0 的闭环方案。

## 一、审计结论（2026-07，基于当前真实代码）

### 1.1 已确认的缺陷链

| 缺陷 | 位置 | 事实 |
| --- | --- | --- |
| Catalog 失败被吞成"无更新" | `Resource/ResourceManager.cs` `CheckAndUpdateCatalogsAsync` | 检查失败、更新失败、抛异常均 `return 0`；`UpdateCatalogs` 句柄状态未检查 |
| 下载尺寸失败被吞成 0 | 同上 `GetDownloadSizeAsync` | 通用异常 `return 0`，与"无需下载"不可区分 |
| 失败链最终提交版本 | `Core/LaunchFlowUpdateExecutor.cs` + `Core/LaunchFlow.cs` | catalogCount 只打日志；`totalBytes<=0` 即 `Success=true`；Step9 `CommitHotUpdate` 把 ResourceVersion 提到服务端版本 |
| Catalog 不在事务内 | `Core/LaunchFlow.cs` Step4 注释 | 注释以"哈希寻址幂等"为由豁免 Catalog 回滚——不成立（无法保证 Catalog 与代码/配置兼容） |
| 配置备份提前删除 | `ConfigData/ConfigManager.cs` `InstallDatabaseBytes` | 安装成功立即 `DeleteFileQuietly(backupPath)`；后续 Hotfix 启动失败时代码槽回滚而配置停留新版 |
| 配置结果单 bool | 同上 + `UpdateDatabaseFromAddressablesAsync` | "无配置更新/下载失败/校验失败/替换失败"全部坍缩为 false 或 true |
| Publish 空目标静默跳过 | `Editor/Release/ReleasePublishingSteps.cs` `AtomicPublishArtifacts` | UploadRoot 为空时打日志返回，流水线仍显示成功（dev/qa `AllowPlayerPrefsOverride=true` 时门禁放行空 UploadRoot） |
| release.yml 无部署目标 | `.github/workflows/release.yml` | `customParameters` 未传 `-uploadRoot`；所有 Profile `UploadRoot=""` |
| CI 无 PR 门禁 | `.github/workflows/ci.yml` | 触发器仅 `push: [master]` + `workflow_dispatch` |

### 1.2 已具备且必须复用的机制（不重复造轮子）

- 代码槽事务：`HotUpdateSlotManager` 已有 staging→原子提交→Pending→确认/回滚→Crash-loop 出厂回退全链路，
  且状态文件 `install-state.json` 有 SchemaVersion + 原子写。**内容事务对齐同一模式，不替代它。**
- 统一身份：已签名清单 `UpdateInfo.ManifestId` 在发布时即被赋值为 `ReleaseContext.ReleaseId`
  （`HotUpdateReleaseSteps.GenerateManifest` / `FullPackageReleaseSteps`），`current.json` 指针同样携带
  `ReleaseId`。**客户端事务直接绑定 `ManifestId`，协议零改动。**
- 回滚/晋级已失败关闭：`ExecuteRollback` / `ExecutePromote` 在 UploadRoot 为空时已抛异常。缺口只在
  Publish 主链路的 `AtomicPublishArtifacts`。
- 测试注入模式：`HotUpdateSlotManager.TestRootDirectoryOverride`（`UNITY_INCLUDE_TESTS` 条件编译）。
  新增的事务/快照组件沿用同一注入方式。

### 1.3 Addressables Catalog 可控性结论（1.28.2）

事实依据：
- 远端 Catalog 以 `catalog_*.json` + `.hash` 缓存于 `{persistentDataPath}/com.unity.addressables/`；
  `CheckForCatalogUpdates` 比较远端 `.hash` 与本地缓存，`UpdateCatalogs` 下载新 catalog 覆盖缓存并
  在进程内重载 locator。
- 下一次进程启动 `Addressables.InitializeAsync` 直接加载**缓存中的最新 catalog**——这就是"旧代码槽 +
  新 Catalog"错配的来源。

方案决策：**采用"Catalog 缓存快照 + 启动期恢复"作为第一阶段可执行回滚**（对应任务书第四章第 11 点的
两阶段拆分），理由：
1. 快照/恢复是纯文件操作，可脱离 Addressables 静态 API 单测（注入缓存目录根）；
2. 恢复发生在 `Addressables.InitializeAsync` 之前，Addressables 感知不到，无兼容风险；
3. "服务端版本化 Catalog + LoadContentCatalogAsync 按 ReleaseId 显式加载"作为第二阶段目标保留在本文
   §5，需服务端布局配合，不阻塞本次 P0 闭环。

回滚语义：
- `UpdateCatalogs` 执行前：把 Addressables 缓存目录做完整快照（LKG Catalog Snapshot）；
- 启动确认成功：删除旧快照，当前缓存成为新 LKG；
- 启动确认前失败/进程被杀：下次启动在 Addressables 初始化前检测到未确认的 Pending Release，
  用快照覆写缓存目录 → 旧 Catalog 生效，与回滚后的旧代码槽、恢复后的旧配置一致。

## 二、统一事务模型

### 2.1 状态与身份

```
ContentReleaseState（persistent/FrameworkBase/ContentRelease/release-state.json，原子写 + SchemaVersion）
├── ActiveRelease        // 当前已确认生效的内容集
├── PendingRelease       // 已安装未确认的内容集（进程重启后据此判定回滚）
└── LastKnownGoodRelease // 最近一次确认成功的内容集（回滚目标）

每个 Release 记录：
ReleaseId（= 清单 ManifestId；清单缺失时由 app+res+code 版本派生确定性 ID）
AppVersion / ResourceVersion / CodeVersion
CodeSlotId（关联 HotUpdateSlotManager 槽）
CatalogSnapshotId（关联 Catalog 快照目录）
ConfigDbSha256（配置库内容摘要；无配置时为空）
InstalledAtUnixSeconds / Confirmed / FailedLaunchCount
```

### 2.2 统一确认点（LaunchFlow Step9 之后，唯一）

只有以下全部成功才 `Pending → Active + LKG`：
Catalog 就绪 → 资源下载完成 → 配置库就绪 → AOT 元数据 → 热更程序集 → `HotfixEntry.Start()==true`。
确认动作按序执行：`HotUpdateSlotManager.ConfirmPendingSlot()` → 配置 `.bak` 删除 → Catalog 快照晋级 →
`VersionManager.CommitHotUpdate`（以事务记录为事实源）→ 事务状态落盘。

### 2.3 失败路径（全部失败关闭）

| 失败点 | 动作 |
| --- | --- |
| Catalog 检查/更新失败、句柄异常、取消 | 中止启动；不提交 ResourceVersion；无副作用（Catalog 未动或下次启动恢复快照） |
| 下载尺寸查询失败 / 资源下载失败 | 中止启动；不提交 ResourceVersion |
| 配置下载/校验/替换失败 | 中止启动（清单声明配置为必需时）；`.bak` 未删，可恢复 |
| AOT/程序集/HotfixEntry 失败 | 代码槽回滚（既有）+ 配置 `.bak` 恢复 + Catalog 快照恢复（下次启动） |
| 确认前进程被杀 | 下次启动 `PrepareForLaunch` 检测 Pending 未确认 → 全量回滚（代码槽既有 + 配置 + Catalog） |
| 已确认版本连续 Crash-loop | 既有出厂回退扩展为内容级：清代码槽 + 清 Catalog 缓存（回到包内 catalog）+ 恢复出厂配置基线 |
| 事务状态文件损坏 | 视为无 Pending（失败安全），仅日志告警，禁止半信半疑地执行回滚 |

## 三、发布模式与 Store 抽象

- 五种模式：`BuildOnly`（只产 staging 工件）/ `Publish` / `Promote` / `Rollback` / `VerifyOnly`。
  **Publish/Promote/Rollback 部署目标为空 → 稳定错误码失败**（`RELEASE_E_STORE_NOT_CONFIGURED`）；
  仅 BuildOnly 允许无部署目标。
- `IReleaseArtifactStore` 进主干：上传不可变对象 / 存在性 / 读取 / 摘要 / 条件写 current.json /
  枚举 release / 不可变冲突检查 / 清理 staging / 诊断。`LocalFileSystemReleaseStore` 为主干唯一实现；
  S3/OSS/COS/Azure 留扩展包（`com.frameworkbase.release.*`），主干零厂商依赖。
- `release.yml`：模式显式化 + 经 Environment Secret 注入 `-uploadRoot` 等部署配置；
  `ci.yml`：增加 `pull_request` 触发；`Tools/ci/check-workflows.ps1` 静态门禁防退化。

## 四、测试策略

- 纯逻辑层（状态机/结果模型/快照管理/Store）：EditMode 直接单测，文件系统用临时目录注入。
- Addressables 依赖：`IAddressablesCatalogService` 适配层（只封装底层 API，无业务），测试注入假实现。
- 故障注入：覆盖任务书第四章第 14 点 14 项 + 第三章第 10 点 9 项。
- 发布端到端：基于 LocalFileSystemReleaseStore 走真实状态机演练 Publish→Rollback→Promote→客户端消费。

## 五、第二阶段目标（本次不实施，防止方案降级为注释）

服务端在 `releases/{app}/{releaseId}/` 内发布版本化 Catalog，客户端按指针 ReleaseId 经
`Addressables.LoadContentCatalogAsync` 显式加载，Pending Catalog 与 LKG Catalog 物理分离。
依赖服务端布局扩展与 Addressables 初始化定制，需单独真机验证后再推进。
