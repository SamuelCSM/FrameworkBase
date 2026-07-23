// ==========================================
// 自动生成的配置类: RuleUiWindowPayloadRef
// 来源工作表: rule_ui_window_payload_ref
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
    /// RuleUiWindowPayloadRef 配置数据。
    /// </summary>
    [Table("rule_ui_window_payload_ref")]
    [Serializable]
    public class RuleUiWindowPayloadRef
    {
        /// <summary>
        /// 稳定PayloadId
        /// </summary>
        [PrimaryKey]
        [Column("Id")]
        public int Id { get; set; }

        /// <summary>
        /// 窗口ID
        /// </summary>
        [Column("WindowId")]
        public int WindowId { get; set; }

    }

}
