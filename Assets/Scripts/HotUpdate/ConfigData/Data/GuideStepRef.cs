// ==========================================
// 自动生成的配置类: GuideStepRef
// 来源工作表: guide_step_ref
// 生成时间: 2026-07-23 10:33:43
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
    /// GuideStepRef 配置数据。
    /// </summary>
    [Table("guide_step_ref")]
    [Serializable]
    public class GuideStepRef
    {
        /// <summary>
        /// 所属GuideId；List表允许重复
        /// </summary>
        [Column("GuideId")]
        public int GuideId { get; set; }

        /// <summary>
        /// Guide内稳定StepId
        /// </summary>
        [Column("StepId")]
        public int StepId { get; set; }

        /// <summary>
        /// 当前版本排序；不得用作断点
        /// </summary>
        [Column("Order")]
        public int Order { get; set; }

        /// <summary>
        /// 完成该步的TriggerId
        /// </summary>
        [Column("CompleteTriggerId")]
        public int CompleteTriggerId { get; set; }

        /// <summary>
        /// Guide内程序短名
        /// </summary>
        [Column("CodeName")]
        public string CodeName { get; set; }

        /// <summary>
        /// 策划说明
        /// </summary>
        [Column("Description")]
        public string Description { get; set; }

        /// <summary>
        /// 本步超时毫秒；<=0 继承运行器级
        /// </summary>
        [Column("TimeoutMs")]
        public int TimeoutMs { get; set; }

    }

}
