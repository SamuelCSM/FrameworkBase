// ==========================================
// 自动生成的配置类: RuleNodeRef
// 来源工作表: rule_node_ref
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
    /// RuleNodeRef 配置数据。
    /// </summary>
    [Table("rule_node_ref")]
    [Serializable]
    public class RuleNodeRef
    {
        /// <summary>
        /// 稳定规则节点ID
        /// </summary>
        [PrimaryKey]
        [Column("Id")]
        public int Id { get; set; }

        /// <summary>
        /// 所属RuleId
        /// </summary>
        [Column("RuleId")]
        public int RuleId { get; set; }

        /// <summary>
        /// Predicate/All/Any/Not
        /// </summary>
        [Column("Kind")]
        public RuleNodeKind Kind { get; set; }

        /// <summary>
        /// Predicate实现TypeId；组合节点填0
        /// </summary>
        [Column("TypeId")]
        public int TypeId { get; set; }

        /// <summary>
        /// 强类型PayloadId；组合节点填0
        /// </summary>
        [Column("PayloadId")]
        public int PayloadId { get; set; }

        /// <summary>
        /// 策划说明
        /// </summary>
        [Column("Description")]
        public string Description { get; set; }

    }

}
