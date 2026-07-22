// ==========================================
// 自动生成的配置类: UiWindowRef
// 来源工作表: ui_window_ref
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
    /// UiWindowRef 配置数据。
    /// </summary>
    [Table("ui_window_ref")]
    [Serializable]
    public class UiWindowRef
    {
        /// <summary>
        /// 稳定窗口ID
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
        /// 模块内程序短名
        /// </summary>
        [Column("CodeName")]
        public string CodeName { get; set; }

        /// <summary>
        /// 窗口逻辑类程序集限定名
        /// </summary>
        [Column("LogicType")]
        public string LogicType { get; set; }

        /// <summary>
        /// Addressable或Code
        /// </summary>
        [Column("RegistrationMode")]
        public UIWindowRegistrationMode RegistrationMode { get; set; }

        /// <summary>
        /// Addressables地址；Code窗口留空
        /// </summary>
        [Column("Address")]
        public string Address { get; set; }

        /// <summary>
        /// 窗口层级
        /// </summary>
        [Column("Layer")]
        public UILayer Layer { get; set; }

        /// <summary>
        /// 是否允许多实例
        /// </summary>
        [Column("AllowMultiple")]
        public bool AllowMultiple { get; set; }

        /// <summary>
        /// 导航栈行为
        /// </summary>
        [Column("StackBehavior")]
        public UIStackBehavior StackBehavior { get; set; }

        /// <summary>
        /// 遮罩模式
        /// </summary>
        [Column("BlockerMode")]
        public UIBlockerMode BlockerMode { get; set; }

        /// <summary>
        /// 策划说明
        /// </summary>
        [Column("Description")]
        public string Description { get; set; }

    }

}
