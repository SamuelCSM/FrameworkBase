// ==========================================
// 自动生成的配置类: RedDotRetiredRef
// 来源工作表: red_dot_retired_ref
// ==========================================

using System;
using System.Collections.Generic;
using SQLite;
using Framework;
using Framework.Data;
using Framework.Foundation;

namespace HotUpdate.Config.Data
{
    /// <summary>RedDotRetiredRef 配置数据。</summary>
    [Table("red_dot_retired_ref")]
    [Serializable]
    public class RedDotRetiredRef
    {
        /// <summary>永久禁止复用的红点 ID。</summary>
        [PrimaryKey]
        [Column("Id")]
        public int Id { get; set; }

        /// <summary>退休前完整名称。</summary>
        [Column("FormerKey")]
        public string FormerKey { get; set; }

        /// <summary>退休客户端版本。</summary>
        [Column("RetiredVersion")]
        public string RetiredVersion { get; set; }

        /// <summary>退休原因。</summary>
        [Column("Reason")]
        public string Reason { get; set; }
    }
}
