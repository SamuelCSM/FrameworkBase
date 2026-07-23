// ==========================================
// 自动生成的配置类: TriggerDelayPayloadRef
// 来源工作表: trigger_delay_payload_ref
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
    /// TriggerDelayPayloadRef 配置数据。
    /// </summary>
    [Table("trigger_delay_payload_ref")]
    [Serializable]
    public class TriggerDelayPayloadRef
    {
        /// <summary>
        /// 稳定PayloadId
        /// </summary>
        [PrimaryKey]
        [Column("Id")]
        public int Id { get; set; }

        /// <summary>
        /// 延迟毫秒
        /// </summary>
        [Column("Milliseconds")]
        public int Milliseconds { get; set; }

        /// <summary>
        /// 是否忽略TimeScale
        /// </summary>
        [Column("IgnoreTimeScale")]
        public bool IgnoreTimeScale { get; set; }

    }

}
