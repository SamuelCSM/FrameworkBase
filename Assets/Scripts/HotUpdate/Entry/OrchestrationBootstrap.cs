using System;
using System.Collections.Generic;
using System.Linq;
using Framework;
using Framework.Core;
using Framework.Foundation;
using HotUpdate.Config.Data;
using HotUpdate.Config.Table;

namespace HotUpdate.Entry
{
    /// <summary>
    /// 全局编排装配根（L3，ADR-008）：从 rule/trigger/action 三组配表构建 <see cref="RuleCatalog"/>、
    /// <see cref="TriggerCatalog"/>、<see cref="ActionCatalog"/> 并在冻结钩子里 Initialize。
    /// <para>
    /// 这三个目录是<b>全框架共享</b>的基础设施，不属于任何单个模块——此前它们寄生在 GuideBootstrap 里，
    /// 且用 <c>GameEntry.OnFreezeOrchestration = ...</c> 单槽赋值，第二个需要注册 Payload 工厂的模块出现时
    /// 会静默覆盖引导的冻结逻辑。现由本类独占钩子，各模块只经
    /// <see cref="RegisterActionPayloadFactory{TPayload}"/> 等接口贡献自己那部分 Payload 工厂。
    /// </para>
    /// </summary>
    public static class OrchestrationBootstrap
    {
        /// <summary>各模块贡献的 Rule Payload 工厂构建器（TypeId → 冻结时求值的工厂）。</summary>
        private static readonly Dictionary<int, Func<Func<int, object>>> RuleFactoryBuilders =
            new Dictionary<int, Func<Func<int, object>>>();
        /// <summary>各模块贡献的 Trigger Payload 工厂构建器。</summary>
        private static readonly Dictionary<int, Func<Func<int, object>>> TriggerFactoryBuilders =
            new Dictionary<int, Func<Func<int, object>>>();
        /// <summary>各模块贡献的 Action Payload 工厂构建器。</summary>
        private static readonly Dictionary<int, Func<Func<int, object>>> ActionFactoryBuilders =
            new Dictionary<int, Func<Func<int, object>>>();
        /// <summary>编排 Catalog 是否已冻结；冻结后不再接受工厂登记，且冻结自身幂等。</summary>
        private static bool _frozen;

        /// <summary>挂上编排冻结钩子。由 <see cref="RuntimeCatalogBootstrap"/> 统一调用；重复登录只是重挂同一委托。</summary>
        public static void Install()
        {
            GameEntry.OnFreezeOrchestration = FreezeOrchestration;
        }

        /// <summary>登记 Rule Payload 工厂（须在编排冻结前）。</summary>
        /// <param name="typeId">该 Payload 对应的 Rule 能力 TypeId，须遵守号段规约。</param>
        /// <param name="factoryBuilder">
        /// 冻结时调用一次，返回"按 PayloadId 取行"的工厂。索引在这一层构建，
        /// 保证读配表发生在配置库确定就绪的冻结时刻，而非模块登记时刻。
        /// </param>
        public static void RegisterRulePayloadFactory<TPayload>(int typeId, Func<Func<int, TPayload>> factoryBuilder)
            => Register(RuleFactoryBuilders, typeId, factoryBuilder, "Rule");

        /// <summary>登记 Trigger Payload 工厂（须在编排冻结前）。语义同 <see cref="RegisterRulePayloadFactory{TPayload}"/>。</summary>
        public static void RegisterTriggerPayloadFactory<TPayload>(int typeId, Func<Func<int, TPayload>> factoryBuilder)
            => Register(TriggerFactoryBuilders, typeId, factoryBuilder, "Trigger");

        /// <summary>登记 Action Payload 工厂（须在编排冻结前）。语义同 <see cref="RegisterRulePayloadFactory{TPayload}"/>。</summary>
        public static void RegisterActionPayloadFactory<TPayload>(int typeId, Func<Func<int, TPayload>> factoryBuilder)
            => Register(ActionFactoryBuilders, typeId, factoryBuilder, "Action");

        /// <summary>
        /// 编排冻结（GameEntry 在各模块 RegisterCapabilities 之后、StartAsync 之前调用）：
        /// 合并框架内置与各模块登记的 Payload 工厂，构建并 Initialize 三个全局目录。
        /// 幂等——重复登录只在首次真正冻结。
        /// </summary>
        private static void FreezeOrchestration()
        {
            if (_frozen) return;
            _frozen = true;

            Dictionary<int, Func<int, object>> ruleFactories = Materialize(RuleFactoryBuilders, BuildBuiltinRuleFactories(), "Rule");
            Dictionary<int, Func<int, object>> triggerFactories = Materialize(TriggerFactoryBuilders, BuildBuiltinTriggerFactories(), "Trigger");
            Dictionary<int, Func<int, object>> actionFactories = Materialize(ActionFactoryBuilders, BuildBuiltinActionFactories(), "Action");

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
        /// 框架内置 Rule TypeId → Payload 工厂（窗口是否打开 / Target 是否存在）。
        /// 工厂按 PayloadId 从对应强类型 payload 表取行，避免用 JSON/万能字符串充当参数。
        /// </summary>
        private static Dictionary<int, Func<int, object>> BuildBuiltinRuleFactories()
        {
            Dictionary<int, RuleUiWindowPayloadRef> windows = PayloadTableIndex.Build(
                GameEntry.RefData.GetConfig<RuleUiWindowPayloadRefTable>().GetAll(), value => value.Id);
            Dictionary<int, RuleUiTargetPayloadRef> targets = PayloadTableIndex.Build(
                GameEntry.RefData.GetConfig<RuleUiTargetPayloadRefTable>().GetAll(), value => value.Id);
            return new Dictionary<int, Func<int, object>>
            {
                [BuiltinOrchestrationTypeIds.Rules.UIWindowIsOpen] = id =>
                {
                    RuleUiWindowPayloadRef row = PayloadTableIndex.Require(windows, id, "Rule UIWindow Payload");
                    return new UIWindowRulePayload { WindowId = row.WindowId };
                },
                [BuiltinOrchestrationTypeIds.Rules.UITargetExists] = id =>
                {
                    RuleUiTargetPayloadRef row = PayloadTableIndex.Require(targets, id, "Rule UITarget Payload");
                    return new UITargetRulePayload { TargetId = row.TargetId };
                },
            };
        }

        /// <summary>框架内置 Trigger TypeId → Payload 工厂（窗口生命周期 / Target 点击 / 延迟）。</summary>
        private static Dictionary<int, Func<int, object>> BuildBuiltinTriggerFactories()
        {
            Dictionary<int, TriggerUiWindowPayloadRef> windows = PayloadTableIndex.Build(
                GameEntry.RefData.GetConfig<TriggerUiWindowPayloadRefTable>().GetAll(), value => value.Id);
            Dictionary<int, TriggerUiTargetClickPayloadRef> targets = PayloadTableIndex.Build(
                GameEntry.RefData.GetConfig<TriggerUiTargetClickPayloadRefTable>().GetAll(), value => value.Id);
            Dictionary<int, TriggerDelayPayloadRef> delays = PayloadTableIndex.Build(
                GameEntry.RefData.GetConfig<TriggerDelayPayloadRefTable>().GetAll(), value => value.Id);
            return new Dictionary<int, Func<int, object>>
            {
                [BuiltinOrchestrationTypeIds.Triggers.UIWindowLifecycle] = id =>
                {
                    TriggerUiWindowPayloadRef row = PayloadTableIndex.Require(windows, id, "Trigger UIWindow Payload");
                    return new UIWindowTriggerPayload { WindowId = row.WindowId, Phase = row.Phase };
                },
                [BuiltinOrchestrationTypeIds.Triggers.UITargetClicked] = id =>
                {
                    TriggerUiTargetClickPayloadRef row = PayloadTableIndex.Require(targets, id, "Trigger UITarget Payload");
                    return new UITargetClickTriggerPayload { TargetId = row.TargetId };
                },
                [BuiltinOrchestrationTypeIds.Triggers.Delay] = id =>
                {
                    TriggerDelayPayloadRef row = PayloadTableIndex.Require(delays, id, "Trigger Delay Payload");
                    return new DelayPayload { Milliseconds = row.Milliseconds, IgnoreTimeScale = row.IgnoreTimeScale };
                },
            };
        }

        /// <summary>框架内置 Action TypeId → Payload 工厂（开关窗口 / 延迟）。引导挖孔等模块能力由各模块自行登记。</summary>
        private static Dictionary<int, Func<int, object>> BuildBuiltinActionFactories()
        {
            Dictionary<int, ActionUiOpenWindowPayloadRef> opens = PayloadTableIndex.Build(
                GameEntry.RefData.GetConfig<ActionUiOpenWindowPayloadRefTable>().GetAll(), value => value.Id);
            Dictionary<int, ActionUiCloseWindowPayloadRef> closes = PayloadTableIndex.Build(
                GameEntry.RefData.GetConfig<ActionUiCloseWindowPayloadRefTable>().GetAll(), value => value.Id);
            Dictionary<int, ActionDelayPayloadRef> delays = PayloadTableIndex.Build(
                GameEntry.RefData.GetConfig<ActionDelayPayloadRefTable>().GetAll(), value => value.Id);
            return new Dictionary<int, Func<int, object>>
            {
                [BuiltinOrchestrationTypeIds.Actions.UIOpenWindow] = id =>
                {
                    ActionUiOpenWindowPayloadRef row = PayloadTableIndex.Require(opens, id, "Action OpenWindow Payload");
                    return new UIOpenWindowActionPayload { WindowId = row.WindowId, UsePool = row.UsePool };
                },
                [BuiltinOrchestrationTypeIds.Actions.UICloseWindow] = id =>
                {
                    ActionUiCloseWindowPayloadRef row = PayloadTableIndex.Require(closes, id, "Action CloseWindow Payload");
                    return new UICloseWindowActionPayload { WindowId = row.WindowId, Destroy = row.Destroy };
                },
                [BuiltinOrchestrationTypeIds.Actions.Delay] = id =>
                {
                    ActionDelayPayloadRef row = PayloadTableIndex.Require(delays, id, "Action Delay Payload");
                    return new DelayPayload { Milliseconds = row.Milliseconds, IgnoreTimeScale = row.IgnoreTimeScale };
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
                    $"[Orchestration] {category} TypeId={typeId} 未注册 Payload Factory" +
                    "（模块是否忘了在自己的 Bootstrap 里登记？）。");
            object payload = factory(payloadId);
            return payload ?? throw new InvalidOperationException(
                $"[Orchestration] {category} TypeId={typeId}, PayloadId={payloadId} Factory 返回 null。");
        }

        /// <summary>登记工厂构建器到指定集合；TypeId 非法/重复或已冻结均抛。</summary>
        private static void Register<TPayload>(
            IDictionary<int, Func<Func<int, object>>> builders,
            int typeId,
            Func<Func<int, TPayload>> factoryBuilder,
            string category)
        {
            if (typeId <= 0) throw new ArgumentOutOfRangeException(nameof(typeId));
            if (factoryBuilder == null) throw new ArgumentNullException(nameof(factoryBuilder));
            if (_frozen)
                throw new InvalidOperationException("编排 Catalog 已冻结，不能再登记 Payload Factory。");
            if (builders.ContainsKey(typeId))
                throw new InvalidOperationException($"{category} Payload Factory TypeId 重复：{typeId}。");
            // 装箱包一层：对外强类型 Func<int,TPayload>，对内统一存 Func<int,object>。
            builders.Add(typeId, () =>
            {
                Func<int, TPayload> factory = factoryBuilder();
                if (factory == null)
                    throw new InvalidOperationException($"{category} TypeId={typeId} 的工厂构建器返回 null。");
                return id => factory(id);
            });
        }

        /// <summary>把各模块登记的工厂构建器求值后并入内置集合；与内置 TypeId 冲突直接抛。</summary>
        private static Dictionary<int, Func<int, object>> Materialize(
            IReadOnlyDictionary<int, Func<Func<int, object>>> builders,
            Dictionary<int, Func<int, object>> builtin,
            string category)
        {
            foreach (KeyValuePair<int, Func<Func<int, object>>> pair in builders)
            {
                if (builtin.ContainsKey(pair.Key))
                    throw new InvalidOperationException(
                        $"{category} Payload Factory TypeId 与框架内置冲突：{pair.Key}。");
                builtin.Add(pair.Key, pair.Value());
            }
            return builtin;
        }
    }

    /// <summary>Payload 配表索引小工具：供各模块的 Bootstrap 在冻结时把 payload 行表转成 Id→行 字典。</summary>
    public static class PayloadTableIndex
    {
        /// <summary>把 payload 行列表转成 Id→行 字典；Id 重复即抛，让配置错误在装配期早暴露。</summary>
        public static Dictionary<int, T> Build<T>(IEnumerable<T> rows, Func<T, int> idSelector)
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
        public static T Require<T>(IReadOnlyDictionary<int, T> values, int id, string name)
        {
            if (values.TryGetValue(id, out T value)) return value;
            throw new KeyNotFoundException($"{name} 不存在：{id}。");
        }
    }
}
