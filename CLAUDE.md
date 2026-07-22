# FrameworkBase 开发约定

项目定位、能力清单与目录结构见 [README.md](README.md)；架构决策的背景与取舍见
[ARCHITECTURE_DECISIONS.md](Packages/com.frameworkbase.core/ARCHITECTURE_DECISIONS.md)（ADR-001~008）。
本文件只列动手前必须遵守的规则。

---

## 一、注释规范

### 1. 每个方法都必须有 XML 文档注释

覆盖范围：公开/内部/私有方法、构造函数、含逻辑的属性、事件、接口成员。
即使方法名已自解释，也须写明**调用时机、前置条件、副作用**。

```csharp
/// <summary>Phase 2：编排冻结后初始化引导运行器、接线诊断与遮罩兜底、开始监听。</summary>
public override UniTask StartAsync()
```

- 中文书写。
- 写"为什么/何时"，不复述方法名。反例：`/// <summary>启动异步。</summary>`
- 参数有语义约束、可能抛异常、返回值有特殊含义时，补 `<param>` / `<returns>` / `<exception>`。
- 跨类型引用用 `<see cref="X"/>`。
- 类型（class/struct/interface/enum）须有 `<summary>`，并注明其架构层级与职责。

### 2. 关键代码必须有行内注释

以下位置须用 `//` 说明意图与原因，而非复述代码：

- 执行顺序、时序有硬约束处。
- 线程、异步、生命周期边界（UniTask 切换、取消令牌传递、主线程要求）。
- 非显然的分支、边界条件、魔数、容错兜底与降级路径。
- 偏离常规的临时方案：格式 `// TODO(还债条件): 原因`。
- 涉及架构决策处引用编号，如 `// 见 ADR-008：L3 只消费已冻结的编排服务`。

显而易见的代码不加注释；冗余注释同样视为缺陷。

### 3. 注释与实现须同步

注释与实现不符视为 bug。删除逻辑时一并删除其注释。

---

## 二、架构约束

### 三层启动模型（ADR-008）

层间依赖单向，由 asmdef 编译期强制。

```
L3  热更业务入口   Assets/Scripts/HotUpdate
      │  维护模块清单：host.Use(new RedDotModule()).Use(new GuideModule())
L2  中间层自带业务 Packages/.../Modules（Framework.Modules，红点/引导等）
      │  各模块实现 IFrameworkModule，两阶段：RegisterCapabilities → StartAsync
L1  框架能力       Framework → Framework.Foundation → Framework.Kernel
      └─ L1 不得引用 Framework.Modules
```

- 框架主干不得出现业务概念（背包/货币/任务等）。开箱即用的自带业务归 L2，项目专属业务归 L3。
- 分层判据是概念职责，而非能否编译通过。`Foundation` 只放中立基础设施
  （序列化/HTTP/存储/编排原语/纯数据结构），有业务语义者不得进入。
- 核心逻辑写为不依赖 `UnityEngine` 的纯类以支持 EditMode 测试，Unity 侧只做适配与表现。
- L2 模块之间不得直接类引用，需协作时经 L1 的编排/事件中介。
- 壳工程内容不得反向进入框架包：框架测试写在 `Packages/com.frameworkbase.core/Tests/`，
  `Assets/Tests/EditMode/` 仅供壳工程与参考样例。
- 调整 asmdef 引用关系前先读对应 ADR；边界硬化优先改可见性而非拆 asmdef（ADR-003 补遗）。

### 号段与命名

| 项 | 规则 |
|---|---|
| 协议主号 | 001 为框架保留段（心跳等系统协议），业务从 002 起 |
| 广播消息枚举 | 框架系统段 9000-9999、登录段 10000-10999；业务在自身程序集从 20000 起，转 `int` 走 `EventManager` |
| 单例访问 | 见 ADR-003 的 `.Instance` / `.Shared` 规约，不新增门面 |
| 字段命名 | 私有字段 `_camelCase`、常量 `PascalCase`，由 [.editorconfig](.editorconfig) 以 warning 强制 |

### 配表分片（ADR-006）

片 = 一致性域。新增配表须先确定归属片并在 `ConfigShardCatalog` 注册。跨片引用无外键保护，
须在导出期自行校验。

---

## 三、构建与验证

- Unity 版本固定 2022.3.62f3（见 `ProjectSettings/ProjectVersion.txt`），不得擅自升级。
- 提交前跑本地 CI，须先关闭 Unity 编辑器（batchmode 需独占工程）：

  ```powershell
  .\Tools\ci\run-ci.ps1                # 编译 + EditMode + PlayMode + 资源门禁，约 8 分钟
  .\Tools\ci\run-ci.ps1 -SkipPlayMode  # 快速自查
  ```

  `.githooks/pre-push` 仅在推送 master 时触发本地 CI，个人分支不触发。
- 编辑器占锁（`Temp/UnityLockfile` 存在）时 batchmode 不可用，改用 MSBuild 编译对应 `.csproj` 验证；
  此路径下新增文件须手工补 `.meta` 并加入 csproj，否则 Unity 侧不可见。
- batchmode 退出码不可靠，须以日志哨兵行与结果文件为准；测试失败根因见 `Artifacts/` 下的
  `editmode-results.xml`，而非 console 输出。
- 远端 CI 运行于 Linux 容器，测试须跨平台：`FileShare.None` 在 Linux 为劝告锁不生效，
  构造"目录创建失败"应改用同名文件占位。

---

## 四、协作纪律

- 一项一提交，commit message 用中文，格式 `type(scope): 说明`，不混入无关改动。
- 日常在个人分支开发，master 受保护，规则见 [Docs/BranchProtection.md](Docs/BranchProtection.md)。
- 影响分层、依赖方向或公共契约的改动，先在 `ARCHITECTURE_DECISIONS.md` 追加 ADR
  （背景/决策/放弃了什么），再动代码；方案不得降级为代码注释。
- 判定某能力缺失前须先全仓检索确认，仓库体量大且文档滞后于实现。
- 修改 `proto/*.proto` 后须跑 `gen-proto.bat` 重新生成双端代码。
- 热更安全项（`AppEnv=prod`、签名密钥对、公钥填入 `AppConfig`）见 README「使用须知」，私钥不得入库。
