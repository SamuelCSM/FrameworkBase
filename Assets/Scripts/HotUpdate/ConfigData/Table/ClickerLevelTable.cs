// ==========================================
// 自动生成的表加载类: ClickerLevelTable
// 来源工作表: clicker_level
// 生成时间: 2026-07-16 10:55:01
// ==========================================

using System;
using Framework.Data;
using HotUpdate.Config.Data;

namespace HotUpdate.Config.Table
{
    /// <summary>
    /// ClickerLevel 表加载器。
    /// </summary>
    public class ClickerLevelTable : ConfigBase<int, ClickerLevel>
    {
        /// <summary>
        /// 构造函数。
        /// </summary>
        public ClickerLevelTable()
        {
            // ConfigManager 会按需加载该配置表。
        }

        /// <summary>
        /// 返回单行配置数据的主键。
        /// </summary>
        protected override int GetKey(ClickerLevel item)
        {
            return item.Id;
        }
    }
}
