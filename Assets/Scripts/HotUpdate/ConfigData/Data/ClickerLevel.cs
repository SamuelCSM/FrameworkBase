// ==========================================
// 自动生成的配置类: ClickerLevel
// 来源工作表: clicker_level
// 生成时间: 2026-07-13 22:37:50
// ==========================================

using System;
using System.Collections.Generic;
using SQLite;
using Framework;
using Framework.Data;

namespace HotUpdate.Config.Data
{
    /// <summary>
    /// ClickerLevel 配置数据。
    /// </summary>
    [Table("clicker_level")]
    [Serializable]
    public class ClickerLevel
    {
        /// <summary>
        /// 等级(主键)
        /// </summary>
        [PrimaryKey]
        [Column("Id")]
        public int Id { get; set; }

        /// <summary>
        /// 点击收益
        /// </summary>
        [Column("ClickGain")]
        public int ClickGain { get; set; }

        /// <summary>
        /// 每秒挂机收益
        /// </summary>
        [Column("IdleGainPerSec")]
        public int IdleGainPerSec { get; set; }

        /// <summary>
        /// 升到下一级花费(0=满级)
        /// </summary>
        [Column("UpgradeCost")]
        public int UpgradeCost { get; set; }

        /// <summary>
        /// 等级名称
        /// </summary>
        [Column("Name")]
        public string Name { get; set; }

    }

}
