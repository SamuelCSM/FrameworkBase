// ==========================================
// 自动生成的配置类: ActionGuideFocusPayloadRef
// 来源工作表: action_guide_focus_payload_ref
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
    /// ActionGuideFocusPayloadRef 配置数据。
    /// </summary>
    [Table("action_guide_focus_payload_ref")]
    [Serializable]
    public class ActionGuideFocusPayloadRef
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

        /// <summary>
        /// 挖孔外扩像素
        /// </summary>
        [Column("Padding")]
        public float Padding { get; set; }

        /// <summary>
        /// 遮罩透明度0~1
        /// </summary>
        [Column("DimAlpha")]
        public float DimAlpha { get; set; }

    }

}
