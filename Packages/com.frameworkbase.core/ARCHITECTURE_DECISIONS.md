# 架构决策记录（ADR）

重大取舍的决策与理由。改变决策时在此追加新条目说明为什么，不删旧条目。

## ADR-001：暂不按模块拆分 Framework.asmdef（2026-07）

**状态**：已决策（暂不拆分）。

**背景**：Framework 运行时是单一 asmdef（200+ 源文件）。拆分为
Framework.Core / Network / Resource / UI / … 多程序集的收益是增量编译提速与
层间依赖编译期强制；诉求在 P2-B 评估中提出。

**决策**：现阶段**不拆**，理由按权重排序：

1. **解耦成本前置且巨大**：`GameEntry` 静态门面被全部模块引用，模块间存在大量
   横向互调（Analytics↔Sdk 渠道维度、ErrorCenter→Tips/Event、RemoteConfig→HotUpdate
   版本比较、Save→Serialization）。拆程序集必须先把这些互调抽成接口 + 组合根注入，
   改动面覆盖几乎每个文件，而框架当前正处于能力快速补齐期——拆分会冻结迭代数周。
2. **收益当下不成立**：单程序集全量编译在当前规模下仍是秒级；框架由单人/小团队
   维护，无并行改不同模块的合并冲突之痛。
3. **连带成本**：InternalsVisibleTo、MSBuild 验证流程、HybridCLR 程序集配置、
   测试 asmdef 引用、UPM 包结构全部要跟着动，且拆错边界的返工代价高于晚拆。

**已有替代手段**（守住拆分想守的东西）：

- 目录 + 命名空间即模块边界（Framework.Network / Framework.Save / …）；
- `.editorconfig` + 深度校验器 + 测试守规范；
- 新抽象层（Http/Serialization/Storage/Pooling）已按"接口 + Shared 注入点"成型，
  未来拆分时这些天然是程序集边界。

**重新评估的触发条件**（满足任一即重开本决策）：

- 脚本改动后的编译等待成为日常痛点（经验阈值：>15 秒且每日多次）；
- 框架由 ≥3 人并行维护，模块间合并冲突高频出现；
- 需要对外发布裁剪版包（例如只要 Network+Resource 不要 UI）；
- 业务项目要求把某模块热更化（进 HybridCLR 热更程序集组）。

**拆分时的建议切法**（留给未来）：先切叶子（Serialization → Http → Storage，
它们已零横向依赖），再切 Network/Save/Analytics，最后处理 GameEntry 门面
（拆成 per-module 注册 + 服务定位）；每切一刀跑全量门禁。
