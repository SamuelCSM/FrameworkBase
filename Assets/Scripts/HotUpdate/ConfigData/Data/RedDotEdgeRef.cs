// ==========================================
// 自动生成的配置类: RedDotEdgeRef
// 来源工作表: red_dot_edge_ref
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
    /// RedDotEdgeRef 配置数据。
    /// </summary>
    [Table("red_dot_edge_ref")]
    [Serializable]
    public class RedDotEdgeRef
    {
        /// <summary>
        /// 父节点(依赖方Aggregate)ID；List关系表允许重复
        /// </summary>
        [Column("ParentId")]
        public int ParentId { get; set; }

        /// <summary>
        /// 子节点(被依赖)ID；变化向ParentId传播；同一节点可出现在多条边
        /// </summary>
        [Column("ChildId")]
        public int ChildId { get; set; }

        /// <summary>
        /// 关系说明
        /// </summary>
        [Column("Description")]
        public string Description { get; set; }

    }

}
