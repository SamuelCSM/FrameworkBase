// ==========================================
// 自动生成的配置类: RedDotRetiredRef
// 来源工作表: red_dot_retired_ref
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
    /// RedDotRetiredRef 配置数据。
    /// </summary>
    [Table("red_dot_retired_ref")]
    [Serializable]
    public class RedDotRetiredRef
    {
        /// <summary>
        /// 已退休且永不复用的ID
        /// </summary>
        [PrimaryKey]
        [Column("Id")]
        public int Id { get; set; }

        /// <summary>
        /// 退休前完整Key
        /// </summary>
        [Column("FormerKey")]
        public string FormerKey { get; set; }

        /// <summary>
        /// 退休客户端版本
        /// </summary>
        [Column("RetiredVersion")]
        public string RetiredVersion { get; set; }

        /// <summary>
        /// 退休原因
        /// </summary>
        [Column("Reason")]
        public string Reason { get; set; }

    }

}
