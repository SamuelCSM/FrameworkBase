// ==========================================
// 自动生成的配置类: RedDotSeenPolicyRef
// 来源工作表: red_dot_seen_policy_ref
// ==========================================

using System;
using System.Collections.Generic;
using SQLite;
using Framework;
using Framework.Data;
using Framework.Foundation;

namespace HotUpdate.Config.Data
{
    /// <summary>RedDotSeenPolicyRef 配置数据。</summary>
    [Table("red_dot_seen_policy_ref")]
    [Serializable]
    public class RedDotSeenPolicyRef
    {
        /// <summary>弱提示 Signal ID。</summary>
        [PrimaryKey]
        [Column("SignalId")]
        public int SignalId { get; set; }

        /// <summary>业务确认时机。</summary>
        [Column("Trigger")]
        public RedDotAcknowledgeTrigger Trigger { get; set; }

        /// <summary>已看记录保存范围。</summary>
        [Column("SaveMode")]
        public RedDotSeenSaveMode SaveMode { get; set; }

        /// <summary>内容版本。</summary>
        [Column("Version")]
        public int Version { get; set; }
    }
}
