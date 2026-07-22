using System;
using System.Collections.Generic;
using System.Linq;
using Framework;
using Framework.Core;
using Framework.Foundation;
using HotUpdate.Config.Data;
using HotUpdate.Config.Table;

namespace HotUpdate.Guide
{
    /// <summary>
    /// 热更侧引导组合根（L3，ADR-008）：向框架宿主登记 <see cref="GuideModule"/>，并提供全局
    /// Rule/Trigger/Action 编排目录的冻结实现（从标准 Ref/Table 构造强类型 Catalog）。引导运行器的生命周期、
    /// 引导表现 Action 的注册与 GuideCatalog 的初始化都在 <see cref="GuideModule"/> 内完成。
    /// 业务扩展 TypeId 可在冻结前注册 Payload Factory；对应 Evaluator/Binder/Executor 仍注册到通用服务。
    /// </summary>
    public static class GuideBootstrap
    {
        /// <summary>业务自定义 Rule TypeId 的 Payload 工厂；冻结时合并进框架内置集合。</summary>
        private static readonly Dictionary<int, Func<int, object>> CustomRulePayloadFactories =
            new Dictionary<int, Func<int, object>>();
        /// <summary>业务自定义 Trigger TypeId 的 Payload 工厂；冻结时合并进框架内置集合。</summary>
        private static readonly Dictionary<int, Func<int, object>> CustomTriggerPayloadFactories =
            new Dictionary<int, Func<int, object>>();
        /// <summary>业务自定义 Action TypeId 的 Payload 工厂；冻结时合并进框架内置集合。</summary>
        private static readonly Dictionary<int, Func<int, object>> CustomActionPayloadFactories =
            new Dictionary<int, Func<int, object>>();
        /// <summary>已登记的引导模块实例，用于同一进程内重复登录时的幂等登记。</summary>
        private static GuideModule _module;
        /// <summary>编排 Catalog 是否已冻结；冻结后不再接受 Payload Factory 注册，且冻结自身幂等。</summary>
        private static bool _frozen;

        /// <summary>登记业务自定义 Rule TypeId 的强类型 Payload 工厂（须在编排冻结前调用）。</summary>
        public static void RegisterRulePayloadFactory<TPayload>(int typeId, Func<int, TPayload> factory)
            => RegisterFactory(CustomRulePayloadFactories, typeId, factory);

        /// <summary>登记业务自定义 Trigger TypeId 的强类型 Payload 工厂（须在编排冻结前调用）。</summary>
        public static void RegisterTriggerPayloadFactory<TPayload>(int typeId, Func<int, TPayload> factory)
            => RegisterFactory(CustomTriggerPayloadFactories, typeId, factory);

        /// <summary>登记业务自定义 Action TypeId 的强类型 Payload 工厂（须在编排冻结前调用）。</summary>
        public static void RegisterActionPayloadFactory<TPayload>(int typeId, Func<int, TPayload> factory)
            => RegisterFactory(CustomActionPayloadFactories, typeId, factory);

        /// <summary>向中间层宿主登记引导模块并挂上编排冻结钩子。幂等：重复登录只覆盖钩子、不重复登记模块。</summary>
        public static void Install()
        {
            // 冻结钩子每次登录都重挂（覆盖同一委托，无副作用）；模块只登记一次。
            GameEntry.OnFreezeOrchestration = FreezeOrchestration;
            if (_module != null) return;
            _module = new GuideModule(BuildGuideCatalog);
            GameEntry.Modules.Use(_module);
        }

        /// <summary>
        /// 编排冻结（GameEntry 在各模块 RegisterCapabilities 之后、StartAsync 之前调用）：
        /// 构建并 Initialize 全局 Rule/Trigger/Action 目录。幂等——重复登录只在首次真正冻结。
        /// </summary>
        private static void FreezeOrchestration()
        {
            if (_frozen) return;
            _frozen = true;

            // 内置 Payload 工厂 + 业务自定义工厂合并，供 Catalog 构建时按 TypeId 造强类型参数。
            Dictionary<int, Func<int, object>> ruleFactories = BuildRuleFactories();
            Dictionary<int, Func<int, object>> triggerFactories = BuildTriggerFactories();
            Dictionary<int, Func<int, object>> actionFactories = BuildActionFactories();
            MergeCustom(ruleFactories, CustomRulePayloadFactories, "Rule");
            MergeCustom(triggerFactories, CustomTriggerPayloadFactories, "Trigger");
            MergeCustom(actionFactories, CustomActionPayloadFactories, "Action");

            // 三个全局编排服务各自只初始化一次（Initialize 后不可重复），冻结即不可再变。
            if (!GameEntry.Rules.IsInitialized)
                GameEntry.Rules.Initialize(BuildRuleCatalog(ruleFactories));
            if (!GameEntry.Triggers.IsInitialized)
                GameEntry.Triggers.Initialize(BuildTriggerCatalog(triggerFactories));
            if (!GameEntry.Actions.IsInitialized)
                GameEntry.Actions.Initialize(BuildActionCatalog(actionFactories));
        }

        /// <summary>
        /// 从 rule_ref/rule_node_ref/rule_edge_ref 三张表构建强类型 <see cref="RuleCatalog"/>：
        /// Predicate 叶子节点按 TypeId 用工厂造强类型 Payload，组合节点（All/Any/Not）Payload 为 null。
        /// </summary>
        private static RuleCatalog BuildRuleCatalog(IReadOnlyDictionary<int, Func<int, object>> factories)
        {
            List<RuleRef> rules = GameEntry.RefData.GetConfig<RuleRefTable>().GetAll();
            List<RuleNodeRef> nodes = GameEntry.RefData.GetConfig<RuleNodeRefTable>().GetAll();
            List<RuleEdgeRef> edges = GameEntry.RefData.GetConfig<RuleEdgeRefTable>().GetAll();
            return new RuleCatalog
            {
                Rules = rules.OrderBy(value => value.Id).Select(value => new RuleDefinition
                {
                    Id = value.Id, Key = value.CodeName, RootNodeId = value.RootNodeId,
                    Description = value.Description,
                }).ToArray(),
                Nodes = nodes.OrderBy(value => value.Id).Select(value => new RuleNodeDefinition
                {
                    Id = value.Id,
                    RuleId = value.RuleId,
                    Kind = value.Kind,
                    TypeId = value.TypeId,
                    // 只有 Predicate 叶子携带强类型 Payload；组合节点忽略 TypeId/Payload。
                    Payload = value.Kind == RuleNodeKind.Predicate
                        ? CreatePayload(factories, value.TypeId, value.PayloadId, "Rule")
                        : null,
                    Description = value.Description,
                }).ToArray(),
                // 边按 Parent→Order→Child 稳定排序，保证短路求值顺序可复现。
                Edges = edges.OrderBy(value => value.ParentNodeId).ThenBy(value => value.Order)
                    .ThenBy(value => value.ChildNodeId).Select(value => new RuleEdgeDefinition
                    {
                        ParentNodeId = value.ParentNodeId,
                        ChildNodeId = value.ChildNodeId,
                        Order = value.Order,
                    }).ToArray(),
            };
        }

        /// <summary>从 trigger_ref 表构建 <see cref="TriggerCatalog"/>，每行按 TypeId 用工厂造强类型 Payload。</summary>
        private static TriggerCatalog BuildTriggerCatalog(IReadOnlyDictionary<int, Func<int, object>> factories)
        {
            List<TriggerRef> rows = GameEntry.RefData.GetConfig<TriggerRefTable>().GetAll();
            return new TriggerCatalog
            {
                Triggers = rows.OrderBy(value => value.Id).Select(value => new TriggerDefinition
                {
                    Id = value.Id,
                    Key = value.CodeName,
                    TypeId = value.TypeId,
                    Payload = CreatePayload(factories, value.TypeId, value.PayloadId, "Trigger"),
                    Description = value.Description,
                }).ToArray(),
            };
        }

        /// <summary>从 action_ref 表构建 <see cref="ActionCatalog"/>，每行按 TypeId 用工厂造强类型 Payload。</summary>
        private static ActionCatalog BuildActionCatalog(IReadOnlyDictionary<int, Func<int, object>> factories)
        {
            List<HotUpdate.Config.Data.ActionRef> rows = GameEntry.RefData.GetConfig<ActionRefTable>().GetAll();
            return new ActionCatalog
            {
                Actions = rows.OrderBy(value => value.Id).Select(value => new ActionDefinition
                {
                    Id = value.Id,
                    Key = value.CodeName,
                    TypeId = value.TypeId,
                    Payload = CreatePayload(factories, value.TypeId, value.PayloadId, "Action"),
                    Description = value.Description,
                }).ToArray(),
            };
        }

        /// <summary>
        /// 从 guide_ref/guide_step_ref/guide_step_action_ref 三张表构建引导私有 <see cref="GuideCatalog"/>：
        /// 步骤按 (GuideId, Order, StepId)、动作按 (GuideId, StepId, Phase, Order) 稳定排序，保证展示顺序可复现。
        /// </summary>
        private static GuideCatalog BuildGuideCatalog()
        {
            List<GuideRef> guides = GameEntry.RefData.GetConfig<GuideRefTable>().GetAll();
            List<GuideStepRef> steps = GameEntry.RefData.GetConfig<GuideStepRefTable>().GetAll();
            List<GuideStepActionRef> actions = GameEntry.RefData.GetConfig<GuideStepActionRefTable>().GetAll();
            return new GuideCatalog
            {
                Guides = guides.OrderBy(value => value.Id).Select(value => new GuideDefinition
                {
                    Id = value.Id,
                    Key = value.CodeName,
                    StartRuleId = value.StartRuleId,
                    StartTriggerId = value.StartTriggerId,
                    Priority = value.Priority,
                    RepeatMode = value.RepeatMode,
                    Description = value.Description,
                }).ToArray(),
                Steps = steps.OrderBy(value => value.GuideId).ThenBy(value => value.Order)
                    .ThenBy(value => value.StepId).Select(value => new GuideStepDefinition
                    {
                        GuideId = value.GuideId,
                        StepId = value.StepId,
                        Order = value.Order,
                        CompleteTriggerId = value.CompleteTriggerId,
                        Key = value.CodeName,
                        Description = value.Description,
                    }).ToArray(),
                StepActions = actions.OrderBy(value => value.GuideId).ThenBy(value => value.StepId)
                    .ThenBy(value => value.Phase).ThenBy(value => value.Order)
                    .Select(value => new GuideStepActionDefinition
                    {
                        GuideId = value.GuideId,
                        StepId = value.StepId,
                        Phase = value.Phase,
                        ActionId = value.ActionId,
                        Order = value.Order,
                        FailurePolicy = value.FailurePolicy,
                        Description = value.Description,
                    }).ToArray(),
            };
        }

        /// <summary>
        /// 构建框架内置 Rule TypeId → Payload 工厂映射（窗口是否打开 / Target 是否存在），
        /// 工厂按 PayloadId 从对应强类型 payload 表取行，避免用 JSON/万能字符串充当参数。
        /// </summary>
        private static Dictionary<int, Func<int, object>> BuildRuleFactories()
        {
            Dictionary<int, RuleUiWindowPayloadRef> windows = ById(
                GameEntry.RefData.GetConfig<RuleUiWindowPayloadRefTable>().GetAll(), value => value.Id);
            Dictionary<int, RuleUiTargetPayloadRef> targets = ById(
                GameEntry.RefData.GetConfig<RuleUiTargetPayloadRefTable>().GetAll(), value => value.Id);
            return new Dictionary<int, Func<int, object>>
            {
                [BuiltinOrchestrationTypeIds.Rules.UIWindowIsOpen] = id =>
                {
                    RuleUiWindowPayloadRef row = Require(windows, id, "Rule UIWindow Payload");
                    return new UIWindowRulePayload { WindowId = row.WindowId };
                },
                [BuiltinOrchestrationTypeIds.Rules.UITargetExists] = id =>
                {
                    RuleUiTargetPayloadRef row = Require(targets, id, "Rule UITarget Payload");
                    return new UITargetRulePayload { TargetId = row.TargetId };
                },
            };
        }

        /// <summary>构建框架内置 Trigger TypeId → Payload 工厂映射（窗口生命周期 / Target 点击 / 延迟）。</summary>
        private static Dictionary<int, Func<int, object>> BuildTriggerFactories()
        {
            Dictionary<int, TriggerUiWindowPayloadRef> windows = ById(
                GameEntry.RefData.GetConfig<TriggerUiWindowPayloadRefTable>().GetAll(), value => value.Id);
            Dictionary<int, TriggerUiTargetClickPayloadRef> targets = ById(
                GameEntry.RefData.GetConfig<TriggerUiTargetClickPayloadRefTable>().GetAll(), value => value.Id);
            Dictionary<int, TriggerDelayPayloadRef> delays = ById(
                GameEntry.RefData.GetConfig<TriggerDelayPayloadRefTable>().GetAll(), value => value.Id);
            return new Dictionary<int, Func<int, object>>
            {
                [BuiltinOrchestrationTypeIds.Triggers.UIWindowLifecycle] = id =>
                {
                    TriggerUiWindowPayloadRef row = Require(windows, id, "Trigger UIWindow Payload");
                    return new UIWindowTriggerPayload { WindowId = row.WindowId, Phase = row.Phase };
                },
                [BuiltinOrchestrationTypeIds.Triggers.UITargetClicked] = id =>
                {
                    TriggerUiTargetClickPayloadRef row = Require(targets, id, "Trigger UITarget Payload");
                    return new UITargetClickTriggerPayload { TargetId = row.TargetId };
                },
                [BuiltinOrchestrationTypeIds.Triggers.Delay] = id =>
                {
                    TriggerDelayPayloadRef row = Require(delays, id, "Trigger Delay Payload");
                    return new DelayPayload { Milliseconds = row.Milliseconds, IgnoreTimeScale = row.IgnoreTimeScale };
                },
            };
        }

        /// <summary>构建框架内置 Action TypeId → Payload 工厂映射（开关窗口 / 延迟 / 引导挖孔与清除遮罩）。</summary>
        private static Dictionary<int, Func<int, object>> BuildActionFactories()
        {
            Dictionary<int, ActionUiOpenWindowPayloadRef> opens = ById(
                GameEntry.RefData.GetConfig<ActionUiOpenWindowPayloadRefTable>().GetAll(), value => value.Id);
            Dictionary<int, ActionUiCloseWindowPayloadRef> closes = ById(
                GameEntry.RefData.GetConfig<ActionUiCloseWindowPayloadRefTable>().GetAll(), value => value.Id);
            Dictionary<int, ActionDelayPayloadRef> delays = ById(
                GameEntry.RefData.GetConfig<ActionDelayPayloadRefTable>().GetAll(), value => value.Id);
            Dictionary<int, ActionGuideFocusPayloadRef> focuses = ById(
                GameEntry.RefData.GetConfig<ActionGuideFocusPayloadRefTable>().GetAll(), value => value.Id);
            Dictionary<int, ActionGuideClearFocusPayloadRef> clears = ById(
                GameEntry.RefData.GetConfig<ActionGuideClearFocusPayloadRefTable>().GetAll(), value => value.Id);
            return new Dictionary<int, Func<int, object>>
            {
                [BuiltinOrchestrationTypeIds.Actions.UIOpenWindow] = id =>
                {
                    ActionUiOpenWindowPayloadRef row = Require(opens, id, "Action OpenWindow Payload");
                    return new UIOpenWindowActionPayload { WindowId = row.WindowId, UsePool = row.UsePool };
                },
                [BuiltinOrchestrationTypeIds.Actions.UICloseWindow] = id =>
                {
                    ActionUiCloseWindowPayloadRef row = Require(closes, id, "Action CloseWindow Payload");
                    return new UICloseWindowActionPayload { WindowId = row.WindowId, Destroy = row.Destroy };
                },
                [BuiltinOrchestrationTypeIds.Actions.Delay] = id =>
                {
                    ActionDelayPayloadRef row = Require(delays, id, "Action Delay Payload");
                    return new DelayPayload { Milliseconds = row.Milliseconds, IgnoreTimeScale = row.IgnoreTimeScale };
                },
                [GuideOrchestrationTypeIds.FocusTargetAction] = id =>
                {
                    ActionGuideFocusPayloadRef row = Require(focuses, id, "Action GuideFocus Payload");
                    return new GuideFocusTargetActionPayload
                    {
                        TargetId = row.TargetId, Padding = row.Padding, DimAlpha = row.DimAlpha,
                    };
                },
                [GuideOrchestrationTypeIds.ClearFocusAction] = id =>
                {
                    // 清除遮罩无参：仍要求存在对应 payload 行以校验 PayloadId 引用完整。
                    Require(clears, id, "Action GuideClearFocus Payload");
                    return new GuideClearFocusActionPayload();
                },
            };
        }

        /// <summary>
        /// 按 TypeId 找到 Payload 工厂并用 PayloadId 造强类型参数。未注册工厂或工厂返回 null 均属配置错误，直接抛。
        /// </summary>
        private static object CreatePayload(
            IReadOnlyDictionary<int, Func<int, object>> factories,
            int typeId,
            int payloadId,
            string category)
        {
            if (!factories.TryGetValue(typeId, out Func<int, object> factory))
                throw new InvalidOperationException(
                    $"[Guide] {category} TypeId={typeId} 未注册 Payload Factory。");
            object payload = factory(payloadId);
            return payload ?? throw new InvalidOperationException(
                $"[Guide] {category} TypeId={typeId}, PayloadId={payloadId} Factory 返回 null。");
        }

        /// <summary>
        /// 登记业务自定义 TypeId 的 Payload 工厂到指定集合；须在编排冻结前调用。TypeId 非法/重复或已冻结均抛。
        /// </summary>
        private static void RegisterFactory<TPayload>(
            IDictionary<int, Func<int, object>> factories,
            int typeId,
            Func<int, TPayload> factory)
        {
            if (typeId <= 0) throw new ArgumentOutOfRangeException(nameof(typeId));
            if (factory == null) throw new ArgumentNullException(nameof(factory));
            if (_frozen)
                throw new InvalidOperationException("编排 Catalog 已冻结，不能再注册 Payload Factory。");
            if (factories.ContainsKey(typeId))
                throw new InvalidOperationException($"Payload Factory TypeId 重复：{typeId}。");
            // 装箱包一层：对外强类型 Func<int,TPayload>，对内统一存 Func<int,object>。
            factories.Add(typeId, id => factory(id));
        }

        /// <summary>把业务自定义 Payload 工厂并入内置集合；与框架内置 TypeId 冲突直接抛。</summary>
        private static void MergeCustom(
            IDictionary<int, Func<int, object>> destination,
            IReadOnlyDictionary<int, Func<int, object>> custom,
            string category)
        {
            foreach (KeyValuePair<int, Func<int, object>> pair in custom)
            {
                if (destination.ContainsKey(pair.Key))
                    throw new InvalidOperationException($"{category} Payload Factory TypeId 与框架内置冲突：{pair.Key}。");
                destination.Add(pair.Key, pair.Value);
            }
        }

        /// <summary>把 payload 行列表转成 Id→行 字典；Id 重复即抛，让配置错误在装配期早暴露。</summary>
        private static Dictionary<int, T> ById<T>(IEnumerable<T> rows, Func<T, int> idSelector)
        {
            var result = new Dictionary<int, T>();
            foreach (T row in rows)
            {
                int id = idSelector(row);
                if (!result.TryAdd(id, row)) throw new InvalidOperationException($"PayloadId 重复：{id}。");
            }
            return result;
        }

        /// <summary>按 Id 取 payload 行，缺失即抛（配置引用了不存在的 PayloadId）。</summary>
        private static T Require<T>(IReadOnlyDictionary<int, T> values, int id, string name)
        {
            if (values.TryGetValue(id, out T value)) return value;
            throw new KeyNotFoundException($"{name} 不存在：{id}。");
        }
    }
}
