// ==========================================
// 自动生成的配置类: LanguageRef
// 来源表: language
// 生成时间: 2026-05-27 21:37:04
// 警告: 请勿手动修改此文件！
// ==========================================

using System;
using System.Collections.Generic;
using SQLite;
using Framework.Data;

namespace Framework.Data
{
    /// <summary>
    /// LanguageRef 配置类
    /// </summary>
    [Table("language")]
    [Serializable]
    public class LanguageRef
    {
        /// <summary>
        /// 语言KEY
        /// </summary>
        [PrimaryKey]
        [Column("Key")]
        public string Key { get; set; }

        /// <summary>
        /// 中文
        /// </summary>
        [Column("zh_cn")]
        public string Zh_cn { get; set; }

        /// <summary>
        /// 英文
        /// </summary>
        [Column("en_us")]
        public string En_us { get; set; }

    }

}
