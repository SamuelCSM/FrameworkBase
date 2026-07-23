// ==========================================
// 自动生成的配置类: TriggerRef
// 来源工作表: trigger_ref
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
    /// TriggerRef 配置数据。
    /// </summary>
    [Table("trigger_ref")]
    [Serializable]
    public class TriggerRef
    {
        /// <summary>
        /// 稳定Trigger实例ID
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
        /// Binder实现TypeId
        /// </summary>
        [Column("TypeId")]
        public int TypeId { get; set; }

        /// <summary>
        /// 强类型PayloadId
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
