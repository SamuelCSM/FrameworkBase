// ==========================================
// 自动生成的配置类: UiTargetRef
// 来源工作表: ui_target_ref
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
    /// UiTargetRef 配置数据。
    /// </summary>
    [Table("ui_target_ref")]
    [Serializable]
    public class UiTargetRef
    {
        /// <summary>
        /// 稳定语义TargetId
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
        /// 所属窗口ID
        /// </summary>
        [Column("WindowId")]
        public int WindowId { get; set; }

        /// <summary>
        /// 模块内程序短名
        /// </summary>
        [Column("CodeName")]
        public string CodeName { get; set; }

        /// <summary>
        /// 策划说明
        /// </summary>
        [Column("Description")]
        public string Description { get; set; }

    }

}
