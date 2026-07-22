using System;
using System.Collections.Generic;
using System.Linq;
using Framework;
using Framework.Core;
using Framework.Foundation;
using HotUpdate.Config.Data;
using HotUpdate.Config.Table;
using UnityEngine;

namespace HotUpdate.Guide
{
    /// <summary>
    /// 热更侧引导组合根：从标准 Ref/Table 构造强类型 Rule/Trigger/Action Catalog，再安装 GuideCatalog。
    /// 业务扩展 TypeId 可在 Install 前注册 Payload Factory；对应 Evaluator/Binder/Executor 仍注册到通用服务。
    /// </summary>
    public static class GuideBootstrap
    {
        private static readonly Dictionary<int, Func<int, object>> CustomRulePayloadFactories =
            new Dictionary<int, Func<int, object>>();
        private static readonly Dictionary<int, Func<int, object>> CustomTriggerPayloadFactories =
            new Dictionary<int, Func<int, object>>();
        private static readonly Dictionary<int, Func<int, object>> CustomActionPayloadFactories =
            new Dictionary<int, Func<int, object>>();
        private static GuideRunner _installedRunner;
        private static GuideRunner _observedRunner;

        public static void RegisterRulePayloadFactory<TPayload>(int typeId, Func<int, TPayload> factory)
            => RegisterFactory(CustomRulePayloadFactories, typeId, factory);

        public static void RegisterTriggerPayloadFactory<TPayload>(int typeId, Func<int, TPayload> factory)
            => RegisterFactory(CustomTriggerPayloadFactories, typeId, factory);

        public static void RegisterActionPayloadFactory<TPayload>(int typeId, Func<int, TPayload> factory)
            => RegisterFactory(CustomActionPayloadFactories, typeId, factory);

        public static void Install()
        {
            GuideRunner runner = GameEntry.Guides;
            if (runner == null)
            {
                Debug.LogError("[Guide] GameEntry.Guides 尚未创建，无法安装引导目录。");
                return;
            }
            if (ReferenceEquals(_installedRunner, runner) && runner.IsInitialized) return;

            Dictionary<int, Func<int, object>> ruleFactories = BuildRuleFactories();
            Dictionary<int, Func<int, object>> triggerFactories = BuildTriggerFactories();
            Dictionary<int, Func<int, object>> actionFactories = BuildActionFactories();
            MergeCustom(ruleFactories, CustomRulePayloadFactories, "Rule");
            MergeCustom(triggerFactories, CustomTriggerPayloadFactories, "Trigger");
            MergeCustom(actionFactories, CustomActionPayloadFactories, "Action");

            RuleCatalog ruleCatalog = BuildRuleCatalog(ruleFactories);
            TriggerCatalog triggerCatalog = BuildTriggerCatalog(triggerFactories);
            ActionCatalog actionCatalog = BuildActionCatalog(actionFactories);
            GuideCatalog guideCatalog = BuildGuideCatalog();

            if (!GameEntry.Rules.IsInitialized)
                GameEntry.Rules.Initialize(ruleCatalog);
            if (!GameEntry.Triggers.IsInitialized)
                GameEntry.Triggers.Initialize(triggerCatalog);
            if (!GameEntry.Actions.IsInitialized)
                GameEntry.Actions.Initialize(actionCatalog);
            if (!runner.IsInitialized)
                runner.Initialize(guideCatalog);
            AttachDiagnostics(runner);
            runner.StartListening();

            _installedRunner = runner;
            Debug.Log($"[Guide] 引导目录已安装，Guide={runner.Catalog.Guides.Length}，Step={runner.Catalog.Steps.Length}。");
        }

        private static void AttachDiagnostics(GuideRunner runner)
        {
            if (ReferenceEquals(_observedRunner, runner)) return;
            runner.GuideCompleted += guideId => Debug.Log($"[Guide] GUIDE_COMPLETED id={guideId}");
            runner.GuideCancelled += (guideId, reason) =>
                Debug.LogWarning($"[Guide] GUIDE_CANCELLED id={guideId} reason={reason}");
            runner.GuideFailed += (guideId, reason) =>
                Debug.LogError($"[Guide] GUIDE_FAILED id={guideId} reason={reason}");
            _observedRunner = runner;
        }

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
                    Payload = value.Kind == RuleNodeKind.Predicate
                        ? CreatePayload(factories, value.TypeId, value.PayloadId, "Rule")
                        : null,
                    Description = value.Description,
                }).ToArray(),
                Edges = edges.OrderBy(value => value.ParentNodeId).ThenBy(value => value.Order)
                    .ThenBy(value => value.ChildNodeId).Select(value => new RuleEdgeDefinition
                    {
                        ParentNodeId = value.ParentNodeId,
                        ChildNodeId = value.ChildNodeId,
                        Order = value.Order,
                    }).ToArray(),
            };
        }

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
                [BuiltinOrchestrationTypeIds.Actions.GuideFocusTarget] = id =>
                {
                    ActionGuideFocusPayloadRef row = Require(focuses, id, "Action GuideFocus Payload");
                    return new GuideFocusTargetActionPayload
                    {
                        TargetId = row.TargetId, Padding = row.Padding, DimAlpha = row.DimAlpha,
                    };
                },
                [BuiltinOrchestrationTypeIds.Actions.GuideClearFocus] = id =>
                {
                    Require(clears, id, "Action GuideClearFocus Payload");
                    return new GuideClearFocusActionPayload();
                },
            };
        }

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

        private static void RegisterFactory<TPayload>(
            IDictionary<int, Func<int, object>> factories,
            int typeId,
            Func<int, TPayload> factory)
        {
            if (typeId <= 0) throw new ArgumentOutOfRangeException(nameof(typeId));
            if (factory == null) throw new ArgumentNullException(nameof(factory));
            if (GameEntry.Guides?.IsInitialized == true)
                throw new InvalidOperationException("Guide Catalog 已初始化，不能再注册 Payload Factory。");
            if (factories.ContainsKey(typeId))
                throw new InvalidOperationException($"Payload Factory TypeId 重复：{typeId}。");
            factories.Add(typeId, id => factory(id));
        }

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

        private static T Require<T>(IReadOnlyDictionary<int, T> values, int id, string name)
        {
            if (values.TryGetValue(id, out T value)) return value;
            throw new KeyNotFoundException($"{name} 不存在：{id}。");
        }
    }
}
