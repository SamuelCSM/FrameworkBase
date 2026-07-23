// ==========================================
// 自动生成的配置类: GuideRef
// 来源工作表: guide_ref
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
    /// GuideRef 配置数据。
    /// </summary>
    [Table("guide_ref")]
    [Serializable]
    public class GuideRef
    {
        /// <summary>
        /// 稳定GuideId
        /// </summary>
        [PrimaryKey]
        [Column("Id")]
        public int Id { get; set; }

        /// <summary>
        /// 程序短名
        /// </summary>
        [Column("CodeName")]
        public string CodeName { get; set; }

        /// <summary>
        /// 开始条件RuleId；0表示无
        /// </summary>
        [Column("StartRuleId")]
        public int StartRuleId { get; set; }

        /// <summary>
        /// 开始时机TriggerId；0表示仅业务主动启动
        /// </summary>
        [Column("StartTriggerId")]
        public int StartTriggerId { get; set; }

        /// <summary>
        /// 并发候选优先级
        /// </summary>
        [Column("Priority")]
        public int Priority { get; set; }

        /// <summary>
        /// Once或Always
        /// </summary>
        [Column("RepeatMode")]
        public GuideRepeatMode RepeatMode { get; set; }

        /// <summary>
        /// 策划说明
        /// </summary>
        [Column("Description")]
        public string Description { get; set; }

    }

}
