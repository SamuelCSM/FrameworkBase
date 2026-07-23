// ==========================================
// 自动生成的配置类: RedDotSeenPolicyRef
// 来源工作表: red_dot_seen_policy_ref
// 生成时间: 2026-07-23 10:57:15
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
    /// RedDotSeenPolicyRef 配置数据。
    /// </summary>
    [Table("red_dot_seen_policy_ref")]
    [Serializable]
    public class RedDotSeenPolicyRef
    {
        /// <summary>
        /// 弱提示Signal ID；业务状态红点不要填本表
        /// </summary>
        [PrimaryKey]
        [Column("SignalId")]
        public int SignalId { get; set; }

        /// <summary>
        /// 确认时机
        /// </summary>
        [Column("Trigger")]
        public RedDotAcknowledgeTrigger Trigger { get; set; }

        /// <summary>
        /// 已看记录保存范围
        /// </summary>
        [Column("SaveMode")]
        public RedDotSeenSaveMode SaveMode { get; set; }

        /// <summary>
        /// 内容版本；希望重新出现时递增
        /// </summary>
        [Column("Version")]
        public int Version { get; set; }

    }

}
