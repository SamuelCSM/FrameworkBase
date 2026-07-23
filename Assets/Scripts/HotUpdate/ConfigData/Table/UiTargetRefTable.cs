// ==========================================
// 自动生成的表加载类: UiTargetRefTable
// 来源工作表: ui_target_ref
// 生成时间: 2026-07-22 11:30:16
// ==========================================

using System;
using Framework.Data;
using Framework.Foundation;
using HotUpdate.Config.Data;

namespace HotUpdate.Config.Table
{
    /// <summary>
    /// UiTargetRef 表加载器。
    /// </summary>
    public class UiTargetRefTable : ConfigBase<int, UiTargetRef>
    {
        /// <summary>
        /// 构造函数。
        /// </summary>
        public UiTargetRefTable()
        {
            // ConfigManager 会按需加载该配置表。
        }

        /// <summary>
        /// 返回单行配置数据的主键。
        /// </summary>
        protected override int GetKey(UiTargetRef item)
        {
            return item.Id;
        }
    }
}
