using System;
using System.Collections.Generic;
using System.Linq;
using Framework;
using Framework.Core;
using HotUpdate.Config.Data;
using HotUpdate.Config.Table;
using HotUpdate.Entry;

namespace HotUpdate.Guide
{
    /// <summary>
    /// 热更侧引导组合根（L3，ADR-008）：向框架宿主登记 <see cref="GuideModule"/>，构建引导私有的
    /// <see cref="GuideCatalog"/>，并向 <see cref="OrchestrationBootstrap"/> 贡献引导自己的 Action Payload 工厂。
    /// <para>
    /// 全局 Rule/Trigger/Action 三个目录的构建与冻结<b>不在此处</b>——它们是全框架共享的基础设施，
    /// 归 <see cref="OrchestrationBootstrap"/>。本类只关心引导；新增带配置的模块同理只改自己的 Bootstrap。
    /// </para>
    /// </summary>
    public static class GuideBootstrap
    {
        /// <summary>已登记的引导模块实例，用于同一进程内重复登录时的幂等登记。</summary>
        private static GuideModule _module;

        /// <summary>
        /// 向中间层宿主登记引导模块，并登记引导挖孔 Action 的 Payload 工厂。
        /// 幂等：重复登录不重复登记（Payload 工厂重复登记会抛 TypeId 重复）。
        /// </summary>
        public static void Install()
        {
            if (_module != null) return;

            // 工厂构建器在编排冻结时才求值：那一刻配置库确定就绪，payload 索引也只建一次。
            OrchestrationBootstrap.RegisterActionPayloadFactory(
                GuideOrchestrationTypeIds.FocusTargetAction, BuildFocusTargetPayloadFactory);
            OrchestrationBootstrap.RegisterActionPayloadFactory(
                GuideOrchestrationTypeIds.ClearFocusAction, BuildClearFocusPayloadFactory);

            _module = new GuideModule(BuildGuideCatalog);
            GameEntry.Modules.Use(_module);
        }

        /// <summary>构建"按 PayloadId 取挖孔参数"的工厂（TargetId / 留白 / 压暗强度）。</summary>
        private static Func<int, GuideFocusTargetActionPayload> BuildFocusTargetPayloadFactory()
        {
            Dictionary<int, ActionGuideFocusPayloadRef> rows = PayloadTableIndex.Build(
                GameEntry.RefData.GetConfig<ActionGuideFocusPayloadRefTable>().GetAll(), value => value.Id);
            return id =>
            {
                ActionGuideFocusPayloadRef row = PayloadTableIndex.Require(rows, id, "Action GuideFocus Payload");
                return new GuideFocusTargetActionPayload
                {
                    TargetId = row.TargetId, Padding = row.Padding, DimAlpha = row.DimAlpha,
                };
            };
        }

        /// <summary>构建清除遮罩 Action 的工厂：无参，但仍要求存在对应 payload 行以校验 PayloadId 引用完整。</summary>
        private static Func<int, GuideClearFocusActionPayload> BuildClearFocusPayloadFactory()
        {
            Dictionary<int, ActionGuideClearFocusPayloadRef> rows = PayloadTableIndex.Build(
                GameEntry.RefData.GetConfig<ActionGuideClearFocusPayloadRefTable>().GetAll(), value => value.Id);
            return id =>
            {
                PayloadTableIndex.Require(rows, id, "Action GuideClearFocus Payload");
                return new GuideClearFocusActionPayload();
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
                        TimeoutMs = value.TimeoutMs,
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
    }
}
