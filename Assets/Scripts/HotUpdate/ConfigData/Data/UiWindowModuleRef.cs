// ==========================================
// 自动生成的配置类: UiWindowModuleRef
// 来源工作表: ui_window_module_ref
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
    /// UiWindowModuleRef 配置数据。
    /// </summary>
    [Table("ui_window_module_ref")]
    [Serializable]
    public class UiWindowModuleRef
    {
        /// <summary>
        /// 稳定模块ID
        /// </summary>
        [PrimaryKey]
        [Column("Id")]
        public int Id { get; set; }

        /// <summary>
        /// 模块程序短名（用于生成常量分组）
        /// </summary>
        [Column("CodeName")]
        public string CodeName { get; set; }

        /// <summary>
        /// 模块职责说明
        /// </summary>
        [Column("Description")]
        public string Description { get; set; }

        /// <summary>
        /// 窗口ID号段起点
        /// </summary>
        [Column("WindowIdMin")]
        public int WindowIdMin { get; set; }

        /// <summary>
        /// 窗口ID号段终点
        /// </summary>
        [Column("WindowIdMax")]
        public int WindowIdMax { get; set; }

        /// <summary>
        /// TargetId号段起点
        /// </summary>
        [Column("TargetIdMin")]
        public int TargetIdMin { get; set; }

        /// <summary>
        /// TargetId号段终点
        /// </summary>
        [Column("TargetIdMax")]
        public int TargetIdMax { get; set; }

    }

}
