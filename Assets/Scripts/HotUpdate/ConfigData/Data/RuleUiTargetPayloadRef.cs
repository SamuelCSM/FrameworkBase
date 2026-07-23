// ==========================================
// 自动生成的配置类: RuleUiTargetPayloadRef
// 来源工作表: rule_ui_target_payload_ref
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
    /// RuleUiTargetPayloadRef 配置数据。
    /// </summary>
    [Table("rule_ui_target_payload_ref")]
    [Serializable]
    public class RuleUiTargetPayloadRef
    {
        /// <summary>
        /// 稳定PayloadId
        /// </summary>
        [PrimaryKey]
        [Column("Id")]
        public int Id { get; set; }

        /// <summary>
        /// 语义TargetId
        /// </summary>
        [Column("TargetId")]
        public int TargetId { get; set; }

    }

}
