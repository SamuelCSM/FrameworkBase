# 模板垂直切片 API 拷问表

> 用途：记录参考样例在真实接入时暴露的框架摩擦。只有能被具体链路复现的问题才进入框架 backlog；
> 样例自身问题留在壳工程解决，避免把业务便利性无边界地下沉进核心包。

| 切片 | 接入摩擦 | 证据/风险 | 处置 | 归属 |
|---|---|---|---|---|
| A | 启动壳允许缺失 UIBootstrap/Loading 引用后以预期噪声继续 | 范式工程无法区分真实错误与已知噪声 | 建灰盒启动壳和零 Error/Warning Play 验收 | 壳工程 |
| A | 子节点 EventSystem 重复执行 DontDestroyOnLoad | Play 验收暴露重复持久化警告 | 修正 UIBootstrap 生命周期 | 框架 |
| B | 代码生成与数据库导出分散在两个交互窗口 | 改一张表需要多步手工操作，无法进 CI | 沉淀 ConfigPipeline 菜单+batchmode 单入口 | 框架工具 |
| B | 配表文档漏写类型行 | 照文档生成 `public 1 Id` 非法代码 | 修正文档并保留通用校验测试 | 框架工具 |
| B | Clicker 表语义测试曾放在核心包 | UPM 消费者会携带业务概念和壳路径依赖 | 迁到 Assets/Tests/EditMode | 壳工程 |
| C | PlayerLoginSuccess 早于账号身份贯通 | 业务在事件中读档会写入 guest 目录 | 增加身份贯通后的 OnBusinessEntryAsync | 框架 |
| C | 登出清身份前没有业务释放点 | 旧账号 Timer 可能在切号后写入新账号目录 | 增加同步 OnBusinessExit，先保存/释放再清身份 | 框架 |
| C | UI 驱动找不到按钮仍可推进 | 自动验收可能误绿 | 必须验证 Click/Shop/Buy/Close 的状态变化，Warning 也失败 | 壳工程 |
| C | 代码生成 UI 只使用 UI 层根，未进入地址化 UI 导航栈 | 不能宣称已覆盖 GoBack/对象池/动画 | 收窄切片 C 文档，不为凑覆盖扩玩法 | 设计边界 |
| D | 热更模式 RuntimeInitializeOnLoad 不触发，业务钩子会缺席 | 冷启动进游戏但无 Clicker 接管 | HotfixEntry.Start() 显式调 ClickerBootstrap.Install（幂等） | 壳工程 |
| D | AtomicPublishArtifacts 对超长 uploadRoot 不安全 | 全路径>260 时报 DirectoryNotFoundException 而非明确错误，发布机易踩 | 暂以短 uploadRoot 规避；建议发布落盘走长路径感知 API 或前置校验并给明确报错 | 框架（backlog） |
| D | Addressables 内容构建生成的 Windows/ 目录 .meta 未被忽略 | 每次资源发布后残留未跟踪 Windows.meta，易误提交构建产物 | .gitignore 补 Windows.meta | 框架 |
| E | 框架启动/登录流程文案硬编码中文，绕过本地化 | 接入方想出英文 Loading/登录提示必须改框架源码；伪本地化下这些文本无 ⟦⟧ 界标，正是"写死没走本地化"的证据 | 新增 `Language.GetOrDefault(key, 源语言默认值)`，LaunchFlow 9 处 + LoginFlow 3 处路由过去：可翻译、缺 key 不把 key 吐给玩家、配表未加载时兜底安全 | 框架（已修） |
| E | 主界面需显示玩家 ID 才能肉眼验证身份贯通 | 无 uid 展示无法确认组合根按账号分目录（存档隔离） | `ClickerMainView` 增 `UID {userId}` 标签，切号验收断言 uid 变化 | 壳工程 |
| F | 模板切片挂 run-ci 后紧跟前一个 batchmode，`Temp/UnityLockfile` 残留数秒被误判占用 | 串行门禁间的正常交接被误伤为"编辑器占用"抛错 | `template-slice-check.ps1` 查锁加 60s 等待窗口，真被长期占用仍明确阻断 | 框架工具 |
| F | >10min 的链式 CI 命令被工具超时截断 | 全量 run-ci 输出被切断，无法判定结果 | 改 detached `Start-Process` + 重定向输出文件轮询 | 工作流 |
| 通用 | 字体覆盖只做缺字检测，未做子集化裁字库 | 全字库进包体积偏大；真机才暴露最终字形集 | 检测侧已进 CI 门禁（`FontCoverageChecker` + `-StrictFonts`）；子集化（按 language 表实际字符裁 TMP 字库减包体）需真机/构建期验证 | 框架（backlog，真机批次） |

## 未决项

**已闭环（切片 E/F）：**

- 切片 E 的 A→登出→B→A 同进程切号验收已落地：业务退出顺序、账号存档按 uid 隔离、互踢清凭据均以 `LoginSlicePlayCheck` 哨兵证伪（`uidA/uidB/isolation/restore/credsClear*` 全绿，`httpHits=3` 走真实 HTTP）。
- 切片 F 的模板切片验收已挂入 run-ci（`-TemplateSlice`，nightly/手动），Clicker + 登录两个 Play 验收器端到端进 CI，可正/反证伪。

**仍未决（需真机 / 构建期 / 额外门禁，均不在本机自动化闭环内）：**

- 切片 D **真机联调**：`EnableHotUpdate=1` 下经 HybridCLR 运行时加载 v2 dll 冷启动跑出新玩法数值（编辑器不实际热加载）；"改一格 Excel（二进制 xlsx，需再走一次 Unity 导出）"并入该批次。发布→回滚链路、资源+代码联合发布、v1→v2 内容跳变已在本机自动化验证。
- 切片 D 真机验证 v1→v2→回滚后，再决定业务入口钩子是否需要版本/重复注册治理。
- **字体子集化**（上表"通用"行）：真机/构建期按 language 表字符裁 TMP 字库减包体；当前仅检测缺字（已进门禁）。
- ~~**启动早期文案的 frame-1 本地化**~~：**已由 ADR-006 配表分片消解**（2026-07-16，922fc6e..eaf8dac）——language 表独立成 language.db 片，LaunchFlow 在第一条 Loading 文案前 `EnsureShardReadyAsync` 提前提取小片，首装早期文案即走配表；未引入独立 bootstrap 字符串表（避免第二套本地化机制），`GetOrDefault` 兜底降级为异常保险。首批 14 条 `#1_launch_*/#1_login_*` 中英文案已随模板入 `Language.xlsx`。
- **消费者工程包边界门禁**：Clicker 代码在 `Assets/Scripts/HotUpdate/`、语义测试在 `Assets/Tests/`（构造上已不在 UPM 包内），但尚无自动门禁验证"只安装 `com.frameworkbase.core` 包时不携带任何 Clicker 代码/测试"。建议加一个最小消费者工程 CI 步骤兜住此边界。
