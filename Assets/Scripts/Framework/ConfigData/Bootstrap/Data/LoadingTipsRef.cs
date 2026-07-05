// ==========================================
// 自动生成的配置类: LoadingTipsRef
// 来源表: loading_tips
// 生成时间: 2026-05-27 21:35:42
// 警告: 请勿手动修改此文件！
// ==========================================

using System;
using System.Collections.Generic;
using SQLite;
using Framework.Data;

namespace Framework.Data
{
    /// <summary>
    /// LoadingTipsRef 配置类
    /// </summary>
    [Table("loading_tips")]
    [Serializable]
    public class LoadingTipsRef
    {
        /// <summary>
        /// 唯一id
        /// </summary>
        [PrimaryKey]
        [Column("Id")]
        public long Id { get; set; }

        /// <summary>
        /// 类型
        /// </summary>
        [Column("LoadingType")]
        public ELoadingType LoadingType { get; set; }

        /// <summary>
        /// 语言key
        /// </summary>
        [Column("Key")]
        public string Key { get; set; }

    }

}
