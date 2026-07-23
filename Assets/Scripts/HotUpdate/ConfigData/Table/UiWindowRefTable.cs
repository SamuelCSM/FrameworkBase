// ==========================================
// 自动生成的表加载类: UiWindowRefTable
// 来源工作表: ui_window_ref
// 生成时间: 2026-07-22 11:30:16
// ==========================================

using System;
using Framework.Data;
using Framework.Foundation;
using HotUpdate.Config.Data;

namespace HotUpdate.Config.Table
{
    /// <summary>
    /// UiWindowRef 表加载器。
    /// </summary>
    public class UiWindowRefTable : ConfigBase<int, UiWindowRef>
    {
        /// <summary>
        /// 构造函数。
        /// </summary>
        public UiWindowRefTable()
        {
            // ConfigManager 会按需加载该配置表。
        }

        /// <summary>
        /// 返回单行配置数据的主键。
        /// </summary>
        protected override int GetKey(UiWindowRef item)
        {
            return item.Id;
        }
    }
}
