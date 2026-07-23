using System;
using System.Collections.Generic;
using Framework.Foundation;

namespace Framework
{
    /// <summary>引导运行时步骤动作：配置定义 + 所属阶段（由 <see cref="GuideCatalogBinder"/> 绑定并排序）。</summary>
    internal sealed class GuideRuntimeAction
    {
        public GuideStepActionDefinition Definition;
    }

    /// <summary>引导运行时步骤：三个阶段的动作列表各自按 Order 稳定排序。</summary>
    internal sealed class GuideRuntimeStep
    {
        public GuideStepDefinition Definition;
        public readonly List<GuideRuntimeAction> EnterActions = new List<GuideRuntimeAction>();
        public readonly List<GuideRuntimeAction> ExitActions = new List<GuideRuntimeAction>();
        public readonly List<GuideRuntimeAction> CancelActions = new List<GuideRuntimeAction>();
    }

    /// <summary>引导运行时对象：步骤按 Order 排好序，并附带 StepId → 索引映射供断点续播定位。</summary>
    internal sealed class GuideRuntimeGuide
    {
        public GuideDefinition Definition;
        public readonly List<GuideRuntimeStep> Steps = new List<GuideRuntimeStep>();
        public readonly Dictionary<int, int> StepIndexById = new Dictionary<int, int>();
    }

    /// <summary>
    /// 引导目录绑定器：把扁平的 <see cref="GuideCatalog"/> 三张表（引导/步骤/步骤动作）校验并织成运行时对象图。
    /// <para>
    /// 与 <see cref="GuideRunner"/> 分离的理由：目录绑定是<b>一次性装配</b>关注点（跨表引用校验、去重、
    /// 稳定排序），运行器则是<b>长生命周期状态机</b>（会话、订阅、排队、动作执行）。两者混在一个类里会让
    /// 运行器同时承担装配与调度，测试也难以只针对其中一面。
    /// </para>
    /// 所有校验都在此期抛出：引导配置错误必须在启动装配期暴露，而不是等到玩家触发引导时才炸。
    /// </summary>
    internal static class GuideCatalogBinder
    {
        /// <summary>
        /// 校验并绑定目录，返回 GuideId → 运行时引导的映射。
        /// </summary>
        /// <param name="catalog">待绑定的引导目录。</param>
        /// <param name="rules">已冻结的规则服务，用于校验 StartRuleId 存在。</param>
        /// <param name="triggers">已冻结的触发器服务，用于校验 Start/Complete TriggerId 存在。</param>
        /// <param name="actions">已冻结的动作服务，用于校验 ActionId 存在。</param>
        /// <exception cref="InvalidOperationException">任一跨表引用缺失、Id/Key/Order 重复或枚举值非法。</exception>
        public static Dictionary<int, GuideRuntimeGuide> Bind(
            GuideCatalog catalog,
            RuleService rules,
            TriggerService triggers,
            ActionService actions)
        {
            var guides = new Dictionary<int, GuideRuntimeGuide>();
            BuildGuides(guides, catalog, rules, triggers);
            BuildSteps(guides, catalog, triggers);
            BuildActions(guides, catalog, actions);
            BuildStepIndex(guides);
            return guides;
        }

        /// <summary>第一遍：建引导对象，校验 Id/Key 唯一、RepeatMode 合法、Start 规则与触发器存在。</summary>
        private static void BuildGuides(
            Dictionary<int, GuideRuntimeGuide> guides,
            GuideCatalog catalog,
            RuleService rules,
            TriggerService triggers)
        {
            GuideDefinition[] definitions = catalog.Guides ?? Array.Empty<GuideDefinition>();
            var keys = new HashSet<string>(StringComparer.Ordinal);
            for (int i = 0; i < definitions.Length; i++)
            {
                GuideDefinition definition = definitions[i]
                    ?? throw new InvalidOperationException($"Guides[{i}] 为空。");
                if (definition.Id <= 0) throw new InvalidOperationException("Guide Id 必须大于 0。");
                if (string.IsNullOrWhiteSpace(definition.Key))
                    throw new InvalidOperationException($"Guide {definition.Id} Key 不能为空。");
                if (!guides.TryAdd(definition.Id, new GuideRuntimeGuide { Definition = definition }))
                    throw new InvalidOperationException($"Guide Id 重复：{definition.Id}。");
                if (!keys.Add(definition.Key))
                    throw new InvalidOperationException($"Guide Key 重复：{definition.Key}。");
                if (!Enum.IsDefined(typeof(GuideRepeatMode), definition.RepeatMode))
                    throw new InvalidOperationException($"Guide {definition.Id} RepeatMode 非法。");
                if (definition.StartRuleId > 0 && !rules.ContainsRule(definition.StartRuleId))
                    throw new InvalidOperationException($"Guide {definition.Id} StartRuleId 不存在：{definition.StartRuleId}。");
                if (definition.StartTriggerId > 0 && !triggers.ContainsTrigger(definition.StartTriggerId))
                    throw new InvalidOperationException($"Guide {definition.Id} StartTriggerId 不存在：{definition.StartTriggerId}。");
            }
        }

        /// <summary>第二遍：挂步骤，校验归属引导存在、StepId 唯一、完成触发器存在；随后按 Order 稳定排序。</summary>
        private static void BuildSteps(
            Dictionary<int, GuideRuntimeGuide> guides,
            GuideCatalog catalog,
            TriggerService triggers)
        {
            GuideStepDefinition[] definitions = catalog.Steps ?? Array.Empty<GuideStepDefinition>();
            var identities = new HashSet<long>();
            for (int i = 0; i < definitions.Length; i++)
            {
                GuideStepDefinition definition = definitions[i]
                    ?? throw new InvalidOperationException($"Steps[{i}] 为空。");
                if (!guides.TryGetValue(definition.GuideId, out GuideRuntimeGuide guide))
                    throw new InvalidOperationException($"Step 引用了不存在的 GuideId={definition.GuideId}。");
                if (definition.StepId <= 0)
                    throw new InvalidOperationException($"Guide {definition.GuideId} StepId 必须大于 0。");
                if (definition.CompleteTriggerId <= 0 || !triggers.ContainsTrigger(definition.CompleteTriggerId))
                    throw new InvalidOperationException(
                        $"Guide {definition.GuideId} Step {definition.StepId} CompleteTriggerId 不存在：{definition.CompleteTriggerId}。");
                // (GuideId, StepId) 复合唯一：不同引导可以用同一 StepId。
                long identity = ((long)definition.GuideId << 32) | (uint)definition.StepId;
                if (!identities.Add(identity))
                    throw new InvalidOperationException($"Guide {definition.GuideId} StepId 重复：{definition.StepId}。");
                guide.Steps.Add(new GuideRuntimeStep { Definition = definition });
            }

            foreach (GuideRuntimeGuide guide in guides.Values)
                guide.Steps.Sort(CompareSteps);
        }

        /// <summary>第三遍：把步骤动作按阶段挂到对应步骤，校验引用与 Order 唯一；随后各阶段按 Order 稳定排序。</summary>
        private static void BuildActions(
            Dictionary<int, GuideRuntimeGuide> guides,
            GuideCatalog catalog,
            ActionService actions)
        {
            GuideStepActionDefinition[] definitions = catalog.StepActions ?? Array.Empty<GuideStepActionDefinition>();
            var orderKeys = new HashSet<string>(StringComparer.Ordinal);
            for (int i = 0; i < definitions.Length; i++)
            {
                GuideStepActionDefinition definition = definitions[i]
                    ?? throw new InvalidOperationException($"StepActions[{i}] 为空。");
                if (!guides.TryGetValue(definition.GuideId, out GuideRuntimeGuide guide))
                    throw new InvalidOperationException($"StepAction 引用了不存在的 GuideId={definition.GuideId}。");
                GuideRuntimeStep step = null;
                for (int n = 0; n < guide.Steps.Count; n++)
                    if (guide.Steps[n].Definition.StepId == definition.StepId) { step = guide.Steps[n]; break; }
                if (step == null)
                    throw new InvalidOperationException(
                        $"StepAction 引用了不存在的 Step：Guide={definition.GuideId}, Step={definition.StepId}。");
                if (!actions.ContainsAction(definition.ActionId))
                    throw new InvalidOperationException($"StepAction ActionId 不存在：{definition.ActionId}。");
                if (!Enum.IsDefined(typeof(GuideActionPhase), definition.Phase)
                    || !Enum.IsDefined(typeof(GuideActionFailurePolicy), definition.FailurePolicy))
                    throw new InvalidOperationException("StepAction Phase/FailurePolicy 非法。");
                string orderKey = $"{definition.GuideId}:{definition.StepId}:{(int)definition.Phase}:{definition.Order}";
                if (!orderKeys.Add(orderKey))
                    throw new InvalidOperationException($"同一步骤同一阶段 Action Order 重复：{orderKey}。");

                GetActionList(step, definition.Phase).Add(new GuideRuntimeAction { Definition = definition });
            }

            foreach (GuideRuntimeGuide guide in guides.Values)
                for (int i = 0; i < guide.Steps.Count; i++)
                {
                    guide.Steps[i].EnterActions.Sort(CompareActions);
                    guide.Steps[i].ExitActions.Sort(CompareActions);
                    guide.Steps[i].CancelActions.Sort(CompareActions);
                }
        }

        /// <summary>收尾：每条引导至少一个步骤、Order 不得重复，并建立 StepId → 索引映射（断点续播用）。</summary>
        private static void BuildStepIndex(Dictionary<int, GuideRuntimeGuide> guides)
        {
            foreach (GuideRuntimeGuide guide in guides.Values)
            {
                if (guide.Steps.Count == 0)
                    throw new InvalidOperationException($"Guide {guide.Definition.Id} 至少需要一个步骤。");
                var orders = new HashSet<int>();
                for (int i = 0; i < guide.Steps.Count; i++)
                {
                    GuideRuntimeStep step = guide.Steps[i];
                    if (!orders.Add(step.Definition.Order))
                        throw new InvalidOperationException(
                            $"Guide {guide.Definition.Id} Step Order 重复：{step.Definition.Order}。");
                    guide.StepIndexById.Add(step.Definition.StepId, i);
                }
            }
        }

        /// <summary>步骤排序：Order 升序，同 Order 以 StepId 兜底，保证顺序可复现。</summary>
        private static int CompareSteps(GuideRuntimeStep left, GuideRuntimeStep right)
        {
            int order = left.Definition.Order.CompareTo(right.Definition.Order);
            return order != 0 ? order : left.Definition.StepId.CompareTo(right.Definition.StepId);
        }

        /// <summary>动作排序：Order 升序，同 Order 以 ActionId 兜底，保证执行顺序可复现。</summary>
        private static int CompareActions(GuideRuntimeAction left, GuideRuntimeAction right)
        {
            int order = left.Definition.Order.CompareTo(right.Definition.Order);
            return order != 0 ? order : left.Definition.ActionId.CompareTo(right.Definition.ActionId);
        }

        /// <summary>按阶段取该步骤对应的动作列表。</summary>
        public static List<GuideRuntimeAction> GetActionList(GuideRuntimeStep step, GuideActionPhase phase)
        {
            switch (phase)
            {
                case GuideActionPhase.Enter: return step.EnterActions;
                case GuideActionPhase.Exit: return step.ExitActions;
                case GuideActionPhase.Cancel: return step.CancelActions;
                default: throw new InvalidOperationException($"未知 GuideActionPhase：{phase}。");
            }
        }
    }
}
