// ==========================================
// 自动生成的配置类: GuideStepActionRef
// 来源工作表: guide_step_action_ref
// 生成时间: 2026-07-22 11:30:15
// ==========================================

using System;
using System.Collections.Generic;
using SQLite;
using Framework;
using Framework.Data;
using Framework.Foundation;

namespace HotUpdate.Config.Data
{
    /// <summary>
    /// GuideStepActionRef 配置数据。
    /// </summary>
    [Table("guide_step_action_ref")]
    [Serializable]
    public class GuideStepActionRef
    {
        /// <summary>
        /// 所属GuideId
        /// </summary>
        [Column("GuideId")]
        public int GuideId { get; set; }

        /// <summary>
        /// Guide内StepId
        /// </summary>
        [Column("StepId")]
        public int StepId { get; set; }

        /// <summary>
        /// Enter/Exit/Cancel
        /// </summary>
        [Column("Phase")]
        public GuideActionPhase Phase { get; set; }

        /// <summary>
        /// 通用Action实例ID
        /// </summary>
        [Column("ActionId")]
        public int ActionId { get; set; }

        /// <summary>
        /// 阶段内稳定顺序
        /// </summary>
        [Column("Order")]
        public int Order { get; set; }

        /// <summary>
        /// 失败时AbortGuide或Continue
        /// </summary>
        [Column("FailurePolicy")]
        public GuideActionFailurePolicy FailurePolicy { get; set; }

        /// <summary>
        /// 策划说明
        /// </summary>
        [Column("Description")]
        public string Description { get; set; }

    }

}
