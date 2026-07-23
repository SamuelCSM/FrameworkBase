// ==========================================
// 自动生成的配置类: ActionGuideClearFocusPayloadRef
// 来源工作表: action_guide_clear_focus_payload_ref
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
    /// ActionGuideClearFocusPayloadRef 配置数据。
    /// </summary>
    [Table("action_guide_clear_focus_payload_ref")]
    [Serializable]
    public class ActionGuideClearFocusPayloadRef
    {
        /// <summary>
        /// 稳定PayloadId
        /// </summary>
        [PrimaryKey]
        [Column("Id")]
        public int Id { get; set; }

    }

}
