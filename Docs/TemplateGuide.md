# FrameworkBase 30 分钟接入指南

> 读者：第一次用本框架起项目的工程。目标：照着走一遍，30 分钟内得到一个
> **冷启动 → 登录 → 配表 → 主玩法 → 存档 → 埋点 → 远配 → 热更** 全链路打通、且能进 CI 的空游戏壳。
>
> 本指南以仓库自带的**参考垂直切片**（Clicker 样例）为蓝本——它不是玩具 demo，而是每个子系统
> 都接了真实框架 API 的活体范式。凡文中提到"照样例"，指 `Assets/Scenes/Launch.unity` +
> `Assets/Scripts/HotUpdate/Clicker/`。落地设计见 [`TemplateVerticalSliceDesign.md`](TemplateVerticalSliceDesign.md)，
> 接入中暴露的框架摩擦见 [`TemplateApiFriction.md`](TemplateApiFriction.md)。

---

## 0. 心智模型（先读，2 分钟）

接入前必须建立的四个认知，否则会在初始化顺序上反复踩坑：

1. **双层结构：AOT 壳 + 热更层。**
   - **壳（AOT）**：`Assets/Scenes/Launch.unity` + `AppConfig.asset` + Loading/Login 灰盒预制体。随包编译、不可热更。
   - **热更层**：`Assets/Scripts/HotUpdate/` 下经 HybridCLR 下发的程序集。**所有玩法逻辑放这里**，
     才能演练"改一行玩法 → 发热更 → 客户端冷启动收到"。壳里只放"把玩法接管权交出去"的入口。

2. **`GameEntry` 是组合根，不是玩法容器。** 它只负责框架 Manager 初始化与身份贯通；业务通过两个钩子挂进来：
   - `GameEntry.OnBusinessEntryAsync`（`Func<LoginResult, UniTask>`）：**登录成功、账号身份贯通之后**调用。
     此时读账号存档是安全的（存档已按 uid 分目录）。玩法初始化写在这里。
   - `GameEntry.OnBusinessExit`（`Action<string>`）：登出 / 互踢 / 会话失效 / 退出前，**清身份之前**调用。
     先在这里保存并释放 Timer/UI，避免旧账号的定时器写进新账号目录。

3. **启动是 9 步串行流程（`LaunchFlow`），登录是 2 态 FSM（`AppFlow`：Login ⇄ InGame）。**
   你几乎不用改它们，但要知道：配表（config.db 的 language / 业务表）在 `LaunchFlow` 中段才加载完，
   所以**启动早期的文案无法走配表本地化**——框架自身用 `Language.GetOrDefault(key, 源语言默认值)` 兜底
   （见第 5 步）。

4. **验收看日志哨兵，不看 batchmode 退出码。** Unity batchmode 退出码不可靠，全套自动化都以
   ASCII 日志哨兵判定（`LAUNCH_OK`、`CLICKER_PLAY_CHECK_OK` 等）。你的自动化也应沿用这一约定。

---

## 1. 前置（3 分钟）

- Unity **2022.3.62f3**（版本以 `ProjectSettings/ProjectVersion.txt` 为准，CI 也读这里）。
- 依赖：UniTask、Addressables、TextMeshPro、HybridCLR（工程已内置）。
- 克隆工程后**先关闭 Unity 编辑器**再跑任何 batchmode 脚本（否则 `Temp/UnityLockfile` 会挡住）。

先跑一次基线自查，确认环境干净：

```powershell
.\Tools\ci\run-ci.ps1 -SkipPlayMode      # EditMode 全绿 + 资源门禁 exit=0
```

---

## 2. 起壳：AppConfig + 零噪声启动（5 分钟）

1. 菜单 **Framework → App Config → Create AppConfig Asset**，在 `Assets/Resources/` 下生成 `AppConfig.asset`。
2. 按开发档填最小字段（其余留默认）：

   | 字段 | 开发档取值 | 含义 |
   |---|---|---|
   | `AppEnv` | `dev` | prod 会启用更严格的热更/传输安全门禁 |
   | `EnableHotUpdate` | `false` | 先离线跑通；无热更程序集时**必须** false，否则 Step 8 卡重试循环 |
   | `UseNetworkLogin` | `false` | 先用 Mock 登录跑通链路，第 4 步再切真实 HTTP |
   | `AutoGuestLogin` | `true` | 跳过登录界面直接进游戏，便于早期联调 |
   | `UpdateServerUrl` | 留默认或置空 | 空则跳过版本检查 |

3. 打开 `Assets/Scenes/Launch.unity`，确认挂了 `GameEntry` + `UIBootstrap`，并给 Loading/Login 预制体字段赋值（照样例场景）。
4. **Play。验收标准：Console 零 Error / 零 Warning，`LaunchFlow` 9 步日志完整，`[HotfixEntry] 启动` 哨兵出现。**
   任何"预期噪声"都算不合格——范式工程不允许预期 Error。

> ⚠️ 冷启动会静默恢复上次登录会话（记住登录）。反复调登录界面时用菜单
> **Template → Clear Persisted Login Session** 一键回到未登录态。

---

## 3. 配一张业务表（5 分钟）

改一个数值要经 Excel → 代码生成 → config.db 导出 → 运行时读取，全链路有校验、可进 CI。

1. 照样例 `ClickerConfig.xlsx` 建一张表。**第 2 行必须是类型行**（`int`/`string`/`bool`…）——漏写类型行会生成
   `public 1 Id` 这种非法代码（这是被拷问表记录过的真实坑）。
2. 菜单 **Framework → Config → Export All (Excel→代码+config.db)**：一步完成代码生成 + config.db 导出
   （单入口，可 batchmode，不再是两个交互窗口分开点）。
3. 运行时读取：

   ```csharp
   var row = GameEntry.RefData.GetConfig<ClickerConfigTable>().GetByKey("click_gain");
   ```

**验收：** 改 Excel 数值 → 重导出 → 游戏内数值变化；故意填非法值 → 导出期 `DataValidator` 拦截。
若"从改表到看到效果"超过 3 步 / 2 分钟，记进 [`TemplateApiFriction.md`](TemplateApiFriction.md)。

---

## 4. 写玩法（热更层，5 分钟）

1. 在 `Assets/Scripts/HotUpdate/` 下建你的模块（照 `Clicker/`）。
2. 写一个幂等的 `Install()`，把业务钩子挂到 `GameEntry`：

   ```csharp
   public static void Install()   // 幂等：热更/离线两种加载方式都可能调到，重复调用无副作用
   {
       GameEntry.OnBusinessEntryAsync = EnterGameAsync;   // 登录后、身份贯通后：读存档、开主窗口
       GameEntry.OnBusinessExit      = ExitGame;          // 清身份前：存档、释放 Timer/UI
   }
   ```

3. **关键接线**：热更模式下 `RuntimeInitializeOnLoad` 不触发，所以要在 `HotfixEntry.Start()` 里
   **显式调 `Install()`**（照样例）。这是被拷问表记录过的坑：不显式调，冷启动能进游戏但没有业务接管。
4. 子系统按需接：金币走 `SaveManager`（按 uid 隔离）、变化经 `EventManager` 广播 UI 刷新、
   点击打 `AnalyticsManager` 埋点、双倍开关读 `RemoteConfig`、秒收益走 `TimerManager`、
   二级窗口走 `UIManager` 的 Normal/Popup 层根。

**验收：** 玩法数值自检 + 账号级存档往返；点击后 UI 必须变化（Event→UI 闭环）；全程零 Error/零 Warning。

---

## 5. 接登录与本地化（5 分钟）

**登录**（切真实 HTTP 链路）：

1. `AppConfig`：`UseNetworkLogin = true`，`AuthServerUrl` 填 HTTP 认证服务地址（留空则回退 Mock）。
   框架参考实现是 `HttpAuthBackend`（中立 JSON 契约），可用 `GameEntry.Auth.SetBackend(...)` 注入自定义后端。
2. 登录成功 → 会话经 `SecureStorage` 持久化 → 冷启动静默重登 → 登出清凭据。存档按 `LoginResult.UserId` 分目录，
   A/B 账号天然隔离。
3. 主界面显示玩家 ID（验证组合根身份贯通），照样例 `ClickerMainView` 的 `UID {userId}` 标签。

**本地化文案**（大厂标准：屏幕文本不写死）：

- UI 静态文本用 `#2` 前缀 key，挂 `TextMeshProEx` 自动翻译；程序控制文案用 `Language.Get("#1_...")`。
- **language 表独立成片**（ADR-006）：`Assets/RefData_Excel/Language.xlsx` 导出到 `language.db`
  （与 `config.db` 分开），启动第一条 Loading 文案前就提前就绪——改文案只热更小片不动大库。
  框架启动/登录流程的全部 key（`#1_launch_*` / `#1_login_*`）已随模板带首批中英文案。
- **框架自身的启动/登录流程**用 `Language.GetOrDefault("#1_launch_xxx", "源语言默认值")`：
  配表命中该 key 即翻译，缺行时用内联默认值兜底——**可翻译**，不像写死字符串那样锁死语言。
- 开发期开 `PseudoLocalizer.Enabled`：所有走 `Language` 的文案被变形成 `⟦Ẃéĺćóḿé·~⟧`；
  **没被 `⟦⟧` 界标包住的屏幕文本 = 写死没走本地化**，一眼揪出。
- 上线前跑菜单 **Framework → Localization → Check Font Coverage (language 表)** 查缺字，
  避免真机豆腐块（此检查已进 CI 资源门禁，`-StrictFonts` 可把缺字从告警升级为阻断）。

---

## 6. 进 CI（3 分钟）

本地跑与 GitHub CI 同一套门禁：

```powershell
.\Tools\ci\run-ci.ps1                    # 编译 + EditMode + 资源门禁 + PlayMode 冒烟
.\Tools\ci\run-ci.ps1 -SkipPlayMode      # 快速自查（PR 门禁同款：EditMode + 门禁）
.\Tools\ci\run-ci.ps1 -TemplateSlice     # 附加模板切片 Play 验收（nightly/手动；Clicker + 登录切片端到端）
```

模板切片验收（`Tools/ci/template-slice-check.ps1`）driven 两个无人值守 Play 验收器
（`Game.Editor.ClickerPlayCheck.Run`、`Game.Editor.LoginSlicePlayCheck.Run`），
以哨兵 `CLICKER_PLAY_CHECK_OK` / `LOGIN_SLICE_CHECK_OK` 判定。

**验收（可证伪）：** run-ci 双绿；故意破坏一处接线（如改坏主界面按钮名）→ CI 必须变红并定位到失败项。
若绿得太轻松，先怀疑验收器没真在断言。

---

## 7. "接入完成"清单

- [ ] Play 冷启动零 Error / 零 Warning，`LaunchFlow` 9 步 + `[HotfixEntry] 启动` 齐全。
- [ ] 改一张 Excel 数值能在 3 步内反映到游戏里；非法值被导出期拦截。
- [ ] 登录后主界面显示玩家 ID；A→登出→B→切回 A，存档独立且能恢复。
- [ ] 玩法点击 → UI 刷新闭环；二级窗口开/关不穿透、主界面仍可交互。
- [ ] 开 `PseudoLocalizer` 后无裸露（未 `⟦⟧` 包裹）中文；字体覆盖检查无缺字。
- [ ] `run-ci -SkipPlayMode` 全绿；破坏一处接线能让 CI 变红。

---

## 8. 常见陷阱速查（血泪来自各切片拷问表）

| 症状 | 根因 | 处置 |
|---|---|---|
| 冷启动进游戏但无业务接管 | 热更模式 `RuntimeInitializeOnLoad` 不触发 | 在 `HotfixEntry.Start()` 显式调 `Install()`（幂等） |
| Step 8 卡在重试循环 | 无热更程序集却 `EnableHotUpdate=true` | 纯框架/单机项目置 `EnableHotUpdate=false` |
| 生成 `public 1 Id` 非法代码 | Excel 漏写第 2 行类型行 | 补类型行；通用校验测试会兜底 |
| 业务在登录事件里读档写进 guest 目录 | `PlayerLoginSuccess` 早于身份贯通 | 改用 `OnBusinessEntryAsync`（身份贯通后） |
| 切号后旧账号 Timer 写进新目录 | 登出清身份前无业务释放点 | 在 `OnBusinessExit` 先保存/释放再清身份 |
| 自动验收"永远绿" | 找不到按钮仍推进 | 断言状态真变化，Warning 也判失败 |
| batchmode 门禁误判编译成功 | 退出码不可靠 | 只认日志 ASCII 哨兵 |
| 发布报 `DirectoryNotFoundException` | uploadRoot 全路径 > 260 | 发布机用短 uploadRoot（框架加固 backlog） |
| 真机文案显示豆腐块 | 字库未覆盖该语言字符 | 上线前跑字体覆盖检查；`-StrictFonts` 进 CI |

---

## 9. 延伸阅读

- 落地方案与切片计划：[`TemplateVerticalSliceDesign.md`](TemplateVerticalSliceDesign.md)
- 接入摩擦与框架 backlog：[`TemplateApiFriction.md`](TemplateApiFriction.md)
- 内容发行事务（热更原子性）：[`ContentReleaseTransactionDesign.md`](ContentReleaseTransactionDesign.md)
- 发布系统目标形态：[`ReleaseSystemTargetDesign.md`](ReleaseSystemTargetDesign.md)
- 网络设备联调验收：[`NetworkDeviceAcceptance.md`](NetworkDeviceAcceptance.md)
