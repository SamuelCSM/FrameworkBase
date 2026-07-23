// ==========================================
// 自动生成的配置类: RuleEdgeRef
// 来源工作表: rule_edge_ref
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
    /// RuleEdgeRef 配置数据。
    /// </summary>
    [Table("rule_edge_ref")]
    [Serializable]
    public class RuleEdgeRef
    {
        /// <summary>
        /// 父组合节点ID；List关系表允许重复
        /// </summary>
        [Column("ParentNodeId")]
        public int ParentNodeId { get; set; }

        /// <summary>
        /// 子节点ID
        /// </summary>
        [Column("ChildNodeId")]
        public int ChildNodeId { get; set; }

        /// <summary>
        /// 稳定短路顺序
        /// </summary>
        [Column("Order")]
        public int Order { get; set; }

        /// <summary>
        /// 关系说明
        /// </summary>
        [Column("Description")]
        public string Description { get; set; }

    }

}
