// ==========================================
// 自动生成的配置类: RedDotModuleRef
// 来源工作表: red_dot_module_ref
// ==========================================

using System;
using System.Collections.Generic;
using SQLite;
using Framework;
using Framework.Data;
using Framework.Foundation;

namespace HotUpdate.Config.Data
{
    /// <summary>RedDotModuleRef 配置数据。</summary>
    [Table("red_dot_module_ref")]
    [Serializable]
    public class RedDotModuleRef
    {
        /// <summary>稳定模块 ID。</summary>
        [PrimaryKey]
        [Column("Id")]
        public int Id { get; set; }

        /// <summary>模块程序短名，用于生成枚举与节点完整名称。</summary>
        [Column("CodeName")]
        public string CodeName { get; set; }

        /// <summary>模块职责说明。</summary>
        [Column("Description")]
        public string Description { get; set; }

        /// <summary>节点 ID 号段起点（包含）。</summary>
        [Column("IdMin")]
        public int IdMin { get; set; }

        /// <summary>节点 ID 号段终点（包含）。</summary>
        [Column("IdMax")]
        public int IdMax { get; set; }
    }
}
