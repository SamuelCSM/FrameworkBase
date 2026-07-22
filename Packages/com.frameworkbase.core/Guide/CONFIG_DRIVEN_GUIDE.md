# 配置驱动引导系统

本系统面向全局、跨窗口的新手引导：约 80% 常规步骤由配置完成，约 20% 复杂业务通过自定义强类型 Rule / Trigger / Action 扩展。旧的 `GuideFlow + GuideScript(string)` 仍保留，适合局部、一次性、无需全局配表的小流程；新业务不要再用字符串充当全局引导身份。

## 稳定身份

- `GuideId`：全局稳定整数，由 `Guide.xlsx/guide_ref` 生成 `GuideIds.g.cs`。
- `StepId`：只在所属 Guide 内稳定，断点存 `(GuideId, StepId)`。
- `Order`：只控制当前版本的步骤顺序，不参与身份或存档。
- `WindowId / TargetId`：由 `UIWindow.xlsx` 生成 `UIWindowIds.g.cs`；目标通过 `UITargetAnchor` 在窗口激活期间注册。
- 删除 ID 后必须放入对应 retired 表，禁止复用。

因此线上在步骤前插入新步骤、调整顺序不会使旧玩家断点错位；删除了断点步骤时，运行器从该 Guide 第一步安全重启。

## 分层

`Foundation/Orchestration` 是通用编排能力，不依赖“引导”概念：

- `RuleService`：无副作用判断，支持强类型叶子和 `All / Any / Not` 组合。
- `TriggerService`：回答“何时发生”，所有绑定返回 `IDisposable`，支持同步触发安全的 `BindOnce`。
- `ActionService`：回答“做什么”，统一异步、取消和异常隔离。

UI 框架提供通用内置能力：窗口是否打开、Target 是否存在、窗口生命周期、Target 点击、延迟、打开/关闭窗口。引导表现另外提供 Target 挖孔与清除遮罩 Action。玩家等级、背包数量、装备成功等领域事实由业务模块注册。

`GuideRunner` 只负责编排：

1. `StartTrigger` 到达时尝试启动；`StartRule` 决定此刻能否启动。
2. 执行当前 Step 的 Enter Actions。
3. 一次性订阅 `CompleteTrigger`，不在 `Update` 里轮询。
4. 触发后执行 Exit Actions、写入下一稳定 StepId，或标记整条完成。
5. 取消、失败、跳过时执行 Cancel Actions。

同一时刻只运行一条全局引导。运行期间到达的其它开始信号按 `Priority` 暂存，当前引导结束后重新检查其 `StartRule`。

## 配表

所有工作表沿用项目标准三行协议：第 1 行说明、第 2 行字段、第 3 行 C# 类型，第 4 行开始数据。`ConfigPipeline` 统一生成 `xxxRef / xxxRefTable / config.db`。

`UIWindow.xlsx`：

- `ui_window_module_ref`：模块与 WindowId/TargetId 号段。
- `ui_window_ref`：窗口 ID、逻辑类型、Address、层级、栈和遮罩策略。
- `ui_target_ref`：TargetId 到所属窗口的语义目录。
- 两张 retired 表：已删除且永不复用的 ID。

`Guide.xlsx`：

- `rule_ref / rule_node_ref / rule_edge_ref`：规则实例与组合树。
- `trigger_ref / action_ref`：触发器和动作实例。
- `*_payload_ref`：按 TypeId 分开的强类型参数表，避免 JSON/万能字符串参数。
- `guide_ref / guide_step_ref / guide_step_action_ref`：引导、稳定步骤和分阶段动作。
- `guide_retired_ref / guide_step_retired_ref`：退休身份。

`rule_edge_ref`、`guide_step_ref`、`guide_step_action_ref` 等关系表在 `ConfigExportRules.asset` 中声明为 List，生成 `ConfigListBase<T>`，不会因首列重复丢行。

## 业务扩展示例：玩家等级条件

业务先定义强类型参数和 Evaluator：

```csharp
[Serializable]
public sealed class PlayerLevelRuleArgs
{
    public int MinLevel;
}

public sealed class PlayerLevelRule : IRuleEvaluator<PlayerLevelRuleArgs>
{
    public RuleResult Evaluate(PlayerLevelRuleArgs args, RuleContext context)
        => PlayerModel.Level >= args.MinLevel
            ? RuleResult.Passed()
            : RuleResult.Failed();
}
```

在 `GuideBootstrap.Install()` 之前注册 TypeId、Evaluator 与 Payload Factory：

```csharp
const int PlayerLevelRuleTypeId = 11001;

GameEntry.Rules.Register(PlayerLevelRuleTypeId, new PlayerLevelRule());
GuideBootstrap.RegisterRulePayloadFactory(
    PlayerLevelRuleTypeId,
    payloadId =>
    {
        PlayerLevelRulePayloadRef row = GameEntry.RefData
            .GetConfig<PlayerLevelRulePayloadRefTable>()
            .Get(payloadId);
        return new PlayerLevelRuleArgs { MinLevel = row.MinLevel };
    });
```

策划只在 `rule_node_ref` 塞 `TypeId + PayloadId`，参数本身放业务的强类型 payload 表；框架不需要认识玩家等级。

## Code UI 与 Target

Addressable 窗口可由 `UIWindowBootstrap` 按表自动注册。纯代码窗口还需要业务提供 View Factory，但窗口身份必须引用生成常量：

```csharp
GameEntry.UI.RegisterCodeUI<MyWindow>(
    UIWindowIds.MyModule.MyWindow,
    MyViewFactory.Create,
    UILayer.Popup);
```

Prefab UI 在目标节点挂 `UITargetAnchor`；代码 UI 创建 Button 后调用 `Configure(UITargetIds..., rect, button)`。同一 TargetId 可存在于多个窗口实例中，运行器通过窗口 Root Scope 精确解析；无 Scope 且多实例时会明确失败，不会随便选一个。

## 导出与验证

菜单：`Framework/Config/Export All (Excel→代码+config.db)`。

批处理：

```text
Unity.exe -batchmode -quit -projectPath <项目> \
  -executeMethod Framework.Editor.ConfigPipeline.ExportAllForBuilder
```

导出会同时执行窗口号段/退休 ID 校验、Rule 树校验、Guide/Step/Action 跨表校验，并检查生成常量是否可确定性复现。
