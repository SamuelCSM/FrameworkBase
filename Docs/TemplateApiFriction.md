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

## 未决项

- 切片 D **真机联调**：`EnableHotUpdate=1` 下经 HybridCLR 运行时加载 v2 dll 冷启动跑出新玩法数值（编辑器不实际热加载）；"改一格 Excel（二进制 xlsx，需再走一次 Unity 导出）"并入该批次。发布→回滚链路、资源+代码联合发布、v1→v2 内容跳变已在本机自动化验证。
- 切片 D 真机验证 v1→v2→回滚后，再决定业务入口钩子是否需要版本/重复注册治理。
- 切片 E 用 A→登出→B→A 的同进程切号验收，确认业务退出顺序和账号存档隔离没有遗漏。
- 切片 F 增加临时消费者工程门禁，验证只安装 UPM 包时不携带 Clicker 代码与测试。
