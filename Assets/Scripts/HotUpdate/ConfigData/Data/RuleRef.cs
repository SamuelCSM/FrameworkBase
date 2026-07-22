// ==========================================
// 自动生成的配置类: RuleRef
// 来源工作表: rule_ref
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
    /// RuleRef 配置数据。
    /// </summary>
    [Table("rule_ref")]
    [Serializable]
    public class RuleRef
    {
        /// <summary>
        /// 稳定Rule实例ID
        /// </summary>
        [PrimaryKey]
        [Column("Id")]
        public int Id { get; set; }

        /// <summary>
        /// 程序短名
        /// </summary>
        [Column("CodeName")]
        public string CodeName { get; set; }

        /// <summary>
        /// 规则树根节点ID
        /// </summary>
        [Column("RootNodeId")]
        public int RootNodeId { get; set; }

        /// <summary>
        /// 策划说明
        /// </summary>
        [Column("Description")]
        public string Description { get; set; }

    }

}
