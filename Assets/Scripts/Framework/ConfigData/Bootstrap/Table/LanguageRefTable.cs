// ==========================================
// 自动生成的配置表加载类: LanguageRefTable
// 来源表: language
// 生成时间: 2026-05-27 21:37:04
// 警告: 请勿手动修改此文件！
// ==========================================

using System;
using Framework.Data;

namespace Framework.Table
{
    /// <summary>
    /// LanguageRef 配置表加载器
    /// </summary>
    public class LanguageRefTable : ConfigBase<string, LanguageRef>
    {
        /// <summary>
        /// 构造函数
        /// </summary>
        public LanguageRefTable()
        {
            // ConfigManager 会按需调用 Load(dbPath, "language")。
        }

        /// <summary>
        /// 获取配置项的主键
        /// </summary>
        protected override string GetKey(LanguageRef item)
        {
            return item.Key;
        }
    }
}
