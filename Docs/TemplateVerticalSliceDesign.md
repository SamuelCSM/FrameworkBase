# 模板样例(参考垂直切片)落地方案

> 状态：切片 A～C 已落地并完成收口；切片 D～G 待实施
> 关联:`Docs/ContentReleaseTransactionDesign.md`、`Docs/ReleaseSystemTargetDesign.md`、`Tools/ci/release-rehearsal.ps1`

## 1. 目标与定位

**一句话**:做一个"参考垂直切片"(Reference Vertical Slice),把
`冷启动 → 登录 → 热更 → 配表 → 主场景 → 最小玩法循环 → 存档 → 埋点 → 远配`
端到端串通,作为框架的**集成验收测试** + **业务接入范式**。

三重身份:

| 身份 | 验收标准 |
|---|---|
| 集成验收 | 挂进 run-ci,与 EditMode/PlayMode 测试并列成为"集成绿" |
| 接入范式 | 新人照文档 30 分钟起一个接全框架的空游戏壳 |
| API 拷问器 | 每个切片记录"接入别扭点",反哺框架 backlog |

**非目标**:不做好玩的游戏、不做美术资源、不重构现有发布/配表流程(除非切片实际卡住)。

## 2. 形态决策

**结论:不新建工程,直接在本壳工程内做,双层结构。**

- **AOT 层(壳)**:`Assets/Scenes/Launch.unity` + 预制体/配置,补全现有裸场景。
- **热更层(业务样例)**:扩展 `Assets/Scripts/HotUpdate/`(经 HybridCLR 下发的程序集),
  `HotfixEntry.Start()` 之后的全部玩法逻辑放这里——**这是关键**:样例玩法必须放热更侧,
  才能真实演练"改一行玩法代码 → 发热更 → 客户端冷启动收到"的闭环。

理由:
1. 热更程序集、Addressables 配置、HybridCLR 生成物都必须在壳工程 Assets 下,包内 `Samples~` 机制承载不了;
2. 本仓库本来就是"框架包 + 验证壳"结构,`release-rehearsal.ps1` 已依赖此壳,垂直切片是它的自然延伸;
3. 现有 `Assets/_Sample/FrameworkSmoke.cs`(离线冒烟)保留不动,与垂直切片分工:冒烟管"地基能起",切片管"全链路能通"。

## 3. 样例玩法选型

**最小放置/点击循环(Clicker)**，只覆盖已经进入主链路、且能形成有效断言的子系统：

- 主界面显示金币数,点击按钮 +N(N 来自**配表**);
- 每秒自动 +M(M 来自配表,走 **TimerManager**);
- 金币变化经 **EventManager** 广播,UI 订阅刷新;
- 一个"双倍收益"开关由**远程配置**驱动;
- 金币数走 **SaveManager** 持久化,重启恢复;
- 点击/升级事件打**埋点**(AnalyticsManager);
- 一个"商店"二级窗口验证 **UIManager** 提供的 Normal/Popup 层根、遮罩防穿透和弹窗回调。

当前刻意不把 Audio/Tips/Pooling/完整 UI 导航栈塞进 Clicker：它们不在关键交易链路上，
只为凑覆盖数会增加样例维护成本。后续只有真实项目接入再次暴露缺口时才扩样例。

不选跑酷/战斗等:玩法复杂度不产生额外验证价值,只产生维护成本。

## 4. 切片计划(一项一提交,每片过验收再进下一片)

### 切片 A:启动壳补零噪声(AOT 层)

现状 `_Sample/README.md` 自认两条"预期噪声"(UIBootstrap 未注入 / _loadingViewPrefab 未赋值)。
垂直切片的第一步就是消灭它们——**范式工程不允许"预期 Error"**。

- 做:`Launch.unity` 挂全 GameEntry + UIBootstrap;制作最小 LoadingWindow / LoginWindow 预制体(纯 UGUI 灰盒,无美术);建 `AppConfig.asset`(dev 档:`EnableHotUpdate=0`、`UseNetworkLogin=0` 先离线跑通)。
- 验收:Play 后 Console **零 Error/零 Warning**,LaunchFlow 9 步日志完整,`[HotfixEntry] 启动` 哨兵出现。

### 切片 B:业务配表垂直链路

- 做:新建一张真实业务表 `ClickerConfig.xlsx`(字段:点击收益、秒收益、双倍开关默认值…),
  走完整 Excel → CodeGenerator 生成 `ClickerConfigTable` → 导出 config.db → 运行时 `GameEntry.RefData.GetConfig<T>()` 读取。
- 验收:
  1. 改 Excel 数值 → 重导出 → 游戏内数值变化;
  2. 故意填一个非法值,DataValidator 在导出期拦截(验证校验链路);
  3. 记录"从改表到看到效果"实际耗时与步数 → 若超过 3 步/2 分钟,记入 API 拷问表。

### 切片 C:最小玩法循环(热更层)

- 做:`Assets/Scripts/HotUpdate/` 下新增 `Clicker/` 模块：
  登录身份贯通 → `GameEntry.OnBusinessEntryAsync` → 初始化账号存档 → 打开主窗口；
  Save(金币)、Analytics(点击事件)、RemoteConfig(双倍开关读取)、Event/Timer 和 UI 分层逐项接上。
  登出/互踢/SDK 会话失效/应用退出统一先走 `GameEntry.OnBusinessExit`，在身份清空前保存并释放 Timer/UI。
- 验收:
  1. 玩法数值自检与账号级 Save 存/读往返通过；
  2. Click 后 CoinLabel 必须变化（Event → UI 刷新闭环）；
  3. 商店必须真实打开，Buy 后主状态变化，Close 后弹窗消失且主界面仍可交互；
  4. 全程零 Error / 零 Warning，任何按钮或哨兵缺失均失败；
  5. 远配真实下发、冷启动恢复与代码/配表联合升级并入切片 D 验收。

### 切片 D:热更真链路(全流程演练)

- 做:`EnableHotUpdate=1`,接现有 Release Center:
  1. 发布 v1(资源+代码+配置)→ 本地更新服务器(复用 release-rehearsal 基础设施);
  2. 改一行玩法代码(如点击收益 ×2 文案)+ 改一格 Excel → 发布 v2;
  3. 客户端冷启动 → LaunchFlow 拉到 v2 → 事务确认 → 玩法呈现新逻辑新数值;
  4. 演练回滚:promote 回 v1,客户端再启动回到旧逻辑。
- 验收:上述四步全程无手工干预文件;`ContentRelease` 事务在中断注入(启动中杀进程)后仍能前滚/回滚自愈(复用既有测试思路做一次手工演练即可)。

### 切片 E:真实登录链路

- 做:`UseNetworkLogin=1`,接已建的 HTTP 认证后端(ae4a458 链路):登录 → 会话持久化 → 冷启动静默恢复 → 登出清凭据;主界面显示玩家 ID(验证组合根身份贯通:存档隔离按 uid 分目录)。
- 验收:A 账号攒金币 → 登出 → B 账号登录金币独立 → 切回 A 恢复(存档隔离闭环);互踢后回登录页且凭据已清。

### 切片 F:CI 集成绿

- 做:新增 `Tools/ci/template-slice-check.ps1` 挂入 run-ci:
  batchmode 启动 Launch 场景(PlayMode 测试或 `-executeMethod` 脚本驱动),
  等待日志 ASCII 哨兵序列:`LAUNCH_OK → CONFIG_OK → GAMEPLAY_TICK_OK → SAVE_RESTORE_OK`。
  ⚠️ 沿用既有约定:**不信 batchmode 退出码,轮询日志哨兵判定**;Linux 容器注意文件锁差异(见跨平台测试备忘)。
- 验收:run-ci 双绿(既有测试 + 模板切片);故意破坏一处接线(如删预制体引用)CI 能红。

### 切片 G:接入文档 + 拷问表结项

- 做:`Docs/TemplateGuide.md`:"30 分钟接入"分步文档(以模板为蓝本起新项目的 checklist);
  整理各切片积累的 **API 拷问表**(别扭点/初始化顺序陷阱/啰嗦接线),逐条转框架 backlog 或当场修。
- 验收:找一个"新人视角"(或自己冷读)按文档走一遍,卡点即文档缺陷。

## 5. 风险与预案

| 风险 | 预案 |
|---|---|
| HybridCLR/Addressables 构建慢拖累 CI | 切片 F 允许"模板检查"只在 nightly/手动触发;PR 门禁先只挂 EditMode |
| 灰盒 UI 预制体制作繁琐 | 全部代码生成 UGUI 或最简 prefab,禁美术投入 |
| 登录后端在 CI 不可用 | 切片 E 的 CI 版本用 Mock 登录;真实后端仅本地演练(与既有"需真机/联调项"归类一致) |
| 玩法样例膨胀 | 硬约束:任何新玩法点必须对应"尚未被覆盖的子系统",否则不加 |

## 6. 里程碑与顺序依赖

```
A(壳零噪声) → B(配表) → C(玩法循环) → D(热更真链路) → E(真实登录) → F(CI) → G(文档)
                    └──── B/C 期间即可并行开始记录 API 拷问表 ────┘
```

A→C 全离线可做,不依赖服务器;D 依赖 release-rehearsal 基础设施;E 依赖认证后端。
每切片一个提交(中文说明),过验收 + CI 绿再进下一片,与既有工作流一致。
