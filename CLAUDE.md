# FrameworkBase 项目约定

面向在本仓库工作的协作者/AI 的**操作性**约定。项目定位、能力清单、目录结构见 [README.md](README.md)；
架构决策的来龙去脉见 [ARCHITECTURE_DECISIONS.md](Packages/com.frameworkbase.core/ARCHITECTURE_DECISIONS.md)（ADR-001~008）。
本文件只写「动手前必须知道、且别处没写清楚」的规则。

---

## 一、注释规范（强制）

### 1. 每个方法都必须有 XML 文档注释

包括：公开/内部/私有方法、构造函数、属性（有逻辑的）、事件、接口成员。
无例外——即使方法名已经"自解释"，也要写清**它在什么阶段被调用、前置条件、副作用**。

```csharp
/// <summary>Phase 2：编排冻结后初始化引导运行器、接线诊断与遮罩兜底、开始监听。</summary>
public override UniTask StartAsync()
```

要求：

- 中文书写，与现有代码一致。
- 写"为什么/何时"，不是把方法名翻译一遍。
  反例：`/// <summary>启动异步。</summary>`
- 有参数语义约束、可能抛异常、返回值有特殊含义时，补 `<param>` / `<returns>` / `<exception>`。
- 跨类型引用用 `<see cref="X"/>`，便于 IDE 跳转。
- 类型（class/struct/interface/enum）同样必须有 `<summary>`，并注明它在架构中的层级/职责
  （如"中间层模块（ADR-008）"）。

### 2. 关键代码必须有行内注释

以下位置必须写 `//` 说明**意图与原因**，而不是复述代码：

- 执行顺序/时序有硬约束的地方（"必须在编排 Catalog 冻结前注册"）。
- 线程、异步、生命周期边界（UniTask 切换、取消令牌传递、Unity 主线程要求）。
- 非显然的分支、边界条件、魔数、容错兜底与降级路径。
- 绕过常规做法的临时方案：注明原因与还债条件，格式 `// TODO(还债条件): 原因`。
- 与 ADR 相关的决策点：引用编号，如 `// 见 ADR-008：L3 只消费已冻结的编排服务`。

不要为显而易见的代码加注释（`i++; // i 加一`）——噪声同样是缺陷。

### 3. 修改代码时同步修改注释

注释与实现不符视为 bug。删除逻辑时一并删除其注释，不留孤儿说明。

---

## 二、架构铁律

### 三层启动模型（ADR-008），层间依赖单向、asmdef 编译期强制

```
L3  热更业务入口   Assets/Scripts/HotUpdate（Clicker 为参考切片，起正式项目时删）
      │  维护模块清单：host.Use(new RedDotModule()).Use(new GuideModule())
L2  中间层自带业务 Packages/.../Modules（Framework.Modules，红点/引导等）
      │  各模块实现 IFrameworkModule，两阶段：RegisterCapabilities → StartAsync
L1  框架能力       Framework → Framework.Foundation → Framework.Kernel
      └─ L1 绝不引用 Framework.Modules
```

- **框架主干不得出现任何业务概念**（背包/货币/任务/签到…）。开箱即用的自带业务放 L2，
  项目专属业务放 L3。
- **判断分层看概念职责，不看"能不能编译进去"**。`Foundation` 只放任何项目都可能复用的中立基础设施
  （序列化/HTTP/存储/编排原语/纯数据结构）；有业务语义的一律不进（ADR-008 要点 3）。
- **纯逻辑与 Unity API 分离**：核心逻辑写成不依赖 `UnityEngine` 的纯类，便于 EditMode 测试；
  Unity 侧只做适配与表现。
- **L2 模块之间默认不互相直接类引用**，需协作走 L1 的编排/事件做中介。
- **消费者包/壳工程的东西不得反向进入框架包**：`Assets/Tests/EditMode` 是壳工程专属测试，
  框架测试写在 `Packages/com.frameworkbase.core/Tests/`。
- 新增/调整 asmdef 引用关系前，先读对应 ADR；边界硬化优先「改可见性」而非「拆 asmdef」（ADR-003 补遗）。

### 号段与命名约定

| 项 | 规则 |
|---|---|
| 协议主号 | 001 为框架保留段（心跳等系统协议）；业务从 002 起 |
| 广播消息枚举 | 框架系统段 9000-9999、登录段 10000-10999；业务在自己程序集从 20000 起，转 `int` 走 `EventManager` |
| 单例访问 | 见 ADR-003：`.Instance` / `.Shared` 各有适用场景，别随手新增门面 |
| 私有字段 | `_camelCase`；常量 `PascalCase`（由 [.editorconfig](.editorconfig) 以 warning 强制） |

### 配表分片（ADR-006）

「片 = 一致性域」。新增配表先确定归属哪一片，走 `ConfigShardCatalog` 注册；跨片引用没有外键保护，
自己在导出期校验。

---

## 三、构建与验证

- **Unity 版本必须是 2022.3.62f3**（见 `ProjectSettings/ProjectVersion.txt`），不要顺手升级。
- **提交/推送前跑本地 CI**（需先关闭 Unity 编辑器，batchmode 要独占工程）：

  ```powershell
  .\Tools\ci\run-ci.ps1                # 编译 + EditMode + PlayMode + 资源门禁（约 8 分钟）
  .\Tools\ci\run-ci.ps1 -SkipPlayMode  # 快速自查
  ```

  `.githooks/pre-push` 只在推 **master** 时自动拦截跑 CI；推个人分支不触发。
- **Unity 编辑器占着锁（`Temp/UnityLockfile` 存在）时**，batchmode 跑不了。此时验证编译走 MSBuild
  编译对应 `.csproj`；新增文件需**手工补 `.meta` 并加进 csproj**，否则 Unity 侧看不到。
- **batchmode 退出码不可靠**：不要只看 exit code，要看日志里的哨兵行 / 轮询结果文件
  （`Artifacts/` 下的 `editmode-results.xml` 才是失败根因所在，console 输出会骗人）。
- CI 在 **Linux 容器**里跑：写测试时注意跨平台——`FileShare.None` 在 Linux 是劝告锁，不生效；
  要制造"目录创建失败"用同名文件占位。

---

## 四、协作纪律

- **一项一提交**，commit message 用中文，格式 `type(scope): 说明`（见 `git log`）。
  不把无关改动混进一个提交。
- **默认在个人分支工作**（当前 `dev_samuel`），master 受保护（见 [Docs/BranchProtection.md](Docs/BranchProtection.md)）。
  未经明确要求不要提交或推送。
- **改架构先落 ADR**：影响分层、依赖方向、公共契约的改动，先在 `ARCHITECTURE_DECISIONS.md` 追加一条
  （背景/决策/放弃了什么），再动代码。方案不得降级成代码注释。
- **断言"某能力缺失"前先 grep**。本仓库体量大且文档滞后，历史上多次把已实现的能力
  （ServerTime / GameObjectPool / 启动打点）误判为缺失。
- 改 `proto/*.proto` 后必须跑 `gen-proto.bat` 重新生成双端代码。
- 正式发布前的热更安全项（`AppEnv=prod`、签名密钥对、公钥填入 `AppConfig`）见 README「使用须知」，
  私钥**不得入库**。
