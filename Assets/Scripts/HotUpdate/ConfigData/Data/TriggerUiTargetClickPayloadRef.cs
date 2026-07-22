// ==========================================
// 自动生成的配置类: TriggerUiTargetClickPayloadRef
// 来源工作表: trigger_ui_target_click_payload_ref
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
    /// TriggerUiTargetClickPayloadRef 配置数据。
    /// </summary>
    [Table("trigger_ui_target_click_payload_ref")]
    [Serializable]
    public class TriggerUiTargetClickPayloadRef
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
