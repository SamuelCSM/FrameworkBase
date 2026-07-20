// ==========================================
// 自动生成的配置类: RedDotNodeRef
// 来源工作表: red_dot_node_ref
// ==========================================

using System;
using System.Collections.Generic;
using SQLite;
using Framework;
using Framework.Data;
using Framework.Foundation;

namespace HotUpdate.Config.Data
{
    /// <summary>RedDotNodeRef 配置数据。</summary>
    [Table("red_dot_node_ref")]
    [Serializable]
    public class RedDotNodeRef
    {
        /// <summary>稳定红点 ID。</summary>
        [PrimaryKey]
        [Column("Id")]
        public int Id { get; set; }

        /// <summary>归属模块 ID。</summary>
        [Column("ModuleId")]
        public int ModuleId { get; set; }

        /// <summary>模块内程序短名；无需手写模块前缀或层级路径。</summary>
        [Column("CodeName")]
        public string CodeName { get; set; }

        /// <summary>Signal 或 Aggregate。</summary>
        [Column("Type")]
        public RedDotNodeKind Type { get; set; }

        /// <summary>Signal 填 None；Aggregate 选择聚合算法。</summary>
        [Column("Aggregation")]
        public RedDotAggregation Aggregation { get; set; }

        /// <summary>策划说明。</summary>
        [Column("Description")]
        public string Description { get; set; }
    }
}
