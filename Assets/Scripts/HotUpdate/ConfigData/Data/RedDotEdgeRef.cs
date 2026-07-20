// ==========================================
// 自动生成的配置类: RedDotEdgeRef
// 来源工作表: red_dot_edge_ref
// ==========================================

using System;
using System.Collections.Generic;
using SQLite;
using Framework;
using Framework.Data;
using Framework.Foundation;

namespace HotUpdate.Config.Data
{
    /// <summary>RedDotEdgeRef 无主键关系列表数据。</summary>
    [Table("red_dot_edge_ref")]
    [Serializable]
    public class RedDotEdgeRef
    {
        /// <summary>依赖方 Aggregate ID。</summary>
        [Column("ParentId")]
        public int ParentId { get; set; }

        /// <summary>被依赖节点 ID；同一节点可出现在多条边中。</summary>
        [Column("ChildId")]
        public int ChildId { get; set; }

        /// <summary>关系说明。</summary>
        [Column("Description")]
        public string Description { get; set; }
    }
}
