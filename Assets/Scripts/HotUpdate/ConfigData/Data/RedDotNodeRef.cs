// ==========================================
// 自动生成的配置类: RedDotNodeRef
// 来源工作表: red_dot_node_ref
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
    /// RedDotNodeRef 配置数据。
    /// </summary>
    [Table("red_dot_node_ref")]
    [Serializable]
    public class RedDotNodeRef
    {
        /// <summary>
        /// 稳定红点ID
        /// </summary>
        [PrimaryKey]
        [Column("Id")]
        public int Id { get; set; }

        /// <summary>
        /// 归属模块ID
        /// </summary>
        [Column("ModuleId")]
        public int ModuleId { get; set; }

        /// <summary>
        /// 模块内程序短名；不要拼模块前缀或层级路径
        /// </summary>
        [Column("CodeName")]
        public string CodeName { get; set; }

        /// <summary>
        /// Signal或Aggregate
        /// </summary>
        [Column("Type")]
        public RedDotNodeKind Type { get; set; }

        /// <summary>
        /// Signal填None；Aggregate显式选聚合算法
        /// </summary>
        [Column("Aggregation")]
        public RedDotAggregation Aggregation { get; set; }

        /// <summary>
        /// 策划说明
        /// </summary>
        [Column("Description")]
        public string Description { get; set; }

    }

}
