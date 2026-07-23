// ==========================================
// 自动生成的配置类: UiTargetRetiredRef
// 来源工作表: ui_target_retired_ref
// 生成时间: 2026-07-22 11:30:16
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
    /// UiTargetRetiredRef 配置数据。
    /// </summary>
    [Table("ui_target_retired_ref")]
    [Serializable]
    public class UiTargetRetiredRef
    {
        /// <summary>
        /// 已退休且永不复用的TargetId
        /// </summary>
        [PrimaryKey]
        [Column("Id")]
        public int Id { get; set; }

        /// <summary>
        /// 退休前Key
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
