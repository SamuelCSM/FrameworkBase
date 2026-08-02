# FrameworkBase 全链路演示向导

> 读者：想在 30 分钟内判断"这套框架到底做到了哪一步"的人——评审、协作者、或者三个月后的你自己。
>
> 本文档不讲怎么用（那是 [`TemplateGuide.md`](TemplateGuide.md)），只讲**怎么把已实现的能力当场跑给人看**，
> 以及每一段跑出来的东西**证明了什么、没证明什么**。

---

## 0. 一句话说清演示的是什么

一个挂机点击（Clicker）示例，跑在**真服务端**上，玩法代码**真的可以热更**。

它不是三个独立 demo 拼在一起，而是一条链：客户端登录拿到身份 → 用这个身份绑定 TCP 长连接 →
服务端按会话绑定回答"你是谁"（客户端自报不作数）→ 同一个已发布的客户端二进制，
通过下发新的热更程序集改变玩法数值。

**一条命令跑完全部：**

```bash
.\Tools\demo\run-demo.ps1
```

结束时打印 `FULL_DEMO_OK`（或 `FULL_DEMO_FAIL` + 未通过项）。全程无人值守，退出时自动还原一切改动。

前置：关闭 Unity 编辑器（batchmode 需独占工程）；`ServerBase` 仓库与本仓库**放在同级目录**
（否则传 `-ServerRepo <路径>`）；已装 .NET 10 SDK 与 Unity 2022.3.62f3。

耗时：`-SkipHotUpdate` 约 6 分钟；完整约 30 分钟（热更段要构建一次 IL2CPP Player 并跑三次）。

---

## 1. 五段各证明了什么

演示脚本分五段，每段落一行 ASCII 哨兵。下表是**读结果的地图**——看懂它比看懂脚本重要。

### 段 1　服务端自证（不需要 Unity）

跑 ServerBase 自带的两个探针，直接用裸 TCP/HTTP 说话：

- `probe-heartbeat.ps1` → `HEARTBEAT_PROBE_OK`：心跳帧 001_001 回包、`SeqId=0`、序号回显、携带服务端时间戳。
- `probe-session.ps1` → `SESSION_PROBE_OK`：HTTP 拿令牌 → TCP `SessionBind` → 业务请求放行。

**证明**：服务端的协议契约独立成立，不依赖客户端实现。
**用途**：客户端出问题时，先跑这两个探针就能把责任面一分为二。

### 段 2　客户端真连服务端

客户端在 Unity batchmode 里真跑一遍：游客登录 → 建立 TCP 长连接 → `SessionBind` 握手 →
校时/RTT → Echo 往返 → 请求服务端权威档案（`020_001`）。

哨兵形如：

```
GAME_SERVER_SESSION_CHECK_OK bind=True sync=True echo=True profile=True userId=g-xxxx offset=-7ms rtt=17ms
```

**证明**：四件事同时成立——握手绑定、服务端校时真的改写了客户端时钟、业务帧双向通、
**服务端按会话绑定回答身份**。

最后一项是这段的重点。`020_001 GetClickerProfile` 的请求体是**空的**——客户端不能自报"我是谁"，
`userId` 由服务端从 `SessionBind` 建立的绑定里取。这是服务端权威的最小形态；将来金币、等级
上收到服务端，走的就是这条链路。

**没证明**：没有服务端权威玩法。当前金币/等级仍是客户端本地权威，服务端只回答身份。

### 段 3　冷启动令牌重绑

再跑一次同样的检查，断言 `userId` 与上次**相同**。

**证明**：客户端持久化了会话令牌并在冷启动时命中重绑，没有每次启动都新建一个游客。

### 段 4　服务端重启后客户端自愈

杀掉服务端再起来（令牌与会话都是进程内内存态，重启即全部失效），再跑一次。

**证明**：重绑被拒之后的降级路径是通的——客户端静默降级到游客登录，**无人工干预**重新走完
握手/校时/业务全链路，不报错、不卡在登录页。这是最容易被忽略、也最容易在上线后炸的一条。

**没证明，且当前做不到**：身份不跨服务端重启延续。`deviceId → userId` 映射由
`IGuestIdentityStore` 提供，当前**只有内存实现**，重启即丢，必然换一个新游客 id。
脚本把这一项单列为观察项打印、**不作判据**——拿它当判据等于断言一个架构还没兑现的性质。
要跨重启保号，需实现持久化的 `IGuestIdentityStore`（ADR-S005 已规定生产必须替换内存存储，
服务端的 P0 启动硬栏也正因此拒绝以内存态进 Production）。

### 段 5　热更闭环

调用 `Tools/ci/hotupdate-runtime-rehearsal.ps1`：

1. 构建**一次** Windows IL2CPP Development Player，此后**不再重建**；
2. 发布 v1 → 跑 Player → 记下 `ClickGain`；
3. 真改一行玩法代码（`ClickGain` ×2）→ 发布 v2 → **同一个 Player** 再跑 → 断言数值翻倍；
4. 一键回滚 → 再跑 → 断言数值复原。

实测输出：

```
v1 ClickGain = 1
v2 ClickGain = 2（期望 2）
回滚后 ClickGain = 1（期望 1）
HOTUPDATE_RUNTIME_REHEARSAL_OK
```

**证明**：Player 二进制全程未重建，三次测量起点一致，数值只随下发的热更程序集变化——
"改代码真的能在真机运行时生效"，而不只是"发布产物长得对"。

**为什么必须是 IL2CPP**：HybridCLR 只在 IL2CPP 下生效。编辑器是 Mono、直接用已编译程序集，
**从不实际热加载**，所以在编辑器里怎么测都测不到这件事。本框架有三个真机级 bug 就是这样漏到最后的
（见 [ADR-010](../Packages/com.frameworkbase.core/ARCHITECTURE_DECISIONS.md)）。

---

## 2. 演示时值得当场指出来的三件事

如果是讲给别人听，这三点比"跑通了"更能说明成熟度。

**其一，服务端也是分层的，加业务不碰框架。**
服务端与客户端同构：L1 框架能力（Kernel/Protocol/Gateway）、L2 自带业务、L3 项目业务。
`samples/Server.SampleGame` 刻意放在 `src/` **之外**，组合根只多一行 `Use(new ClickerProfileModule(time))`,
框架项目零改动。号段在装配期强制：框架模块只能占 001 主号，业务模块只能占 ≥002，越界直接抛。
见 ServerBase 的 ADR-S014。

**其二，失败路径都是失败关闭的。**
热更清单验签不过、资源版本降级、磁盘空间查不到——一律中止，不"尽力而为"。
段 4 演示的正是失败之后的**恢复**路径，而不只是成功路径。

**其三，边界是写下来的，不是含糊过去的。**
两处已知限制被明确记录而非掩盖：

- **引擎模块不做整体免裁剪**。热更侧调用一个 AOT 侧无人引用的引擎 API 仍会 `MissingMethodException`。
  这是权衡：`stripEngineCode` 的原生裁剪由托管使用分析驱动，整体保留等于变相关掉它。
  处置方式（按类型粒度补工程 link.xml）写在 ADR-010 里。
- **指针回滚救不回已经装到新版本的客户端**。防降级准入是单调的，这是防重放的安全属性而非缺陷。
  回滚的实际作用域是"尚未取到坏版本的客户端"；要救已中招的，须以更高版本号重发旧代码。

---

## 3. 分段单跑

排查或只想看某一段时，底层脚本可以单独调用：

```bash
.\Tools\demo\run-demo.ps1 -SkipHotUpdate
```

```bash
.\Tools\ci\hotupdate-runtime-rehearsal.ps1
```

```bash
.\Tools\ci\run-ci.ps1
```

服务端侧探针在 ServerBase 仓库（需服务端已启动）：

```bash
powershell -File Tools\probe-session.ps1
```

想演示完保留服务端继续手工把玩，加 `-KeepServerRunning`。

---

## 4. 演示脚本对工作区做了什么

全部改动都在 `finally` 里还原，中途强杀也只需 `git checkout` 三个文件。

| 改动 | 何时 | 还原 |
| --- | --- | --- |
| `AppConfig.asset` 的 `UseNetworkLogin` 置 1 | 段 2 开始 | 退出时按备份还原 |
| 启动 ServerBase 进程 | 段 1 / 段 4 | 退出时结束（`-KeepServerRunning` 除外） |
| 热更段的 AppConfig / `ClickerModel.cs` / `ReleaseProfiles/dev.json` / Player 本地状态 | 段 5 | 由热更演练脚本自己的 `finally` 还原 |

`AppConfig.asset` 的入库状态是**离线默认**（`UseNetworkLogin=0`、`EnableHotUpdate=0`），
所以别人 clone 下来不起服务端也能直接 Play。联网与热更都由演示脚本临时切换。

日志落在 `Logs/demo/`（服务端 stdout、每次客户端 batchmode 日志、热更段完整输出），
出问题时按段号找对应文件即可。
