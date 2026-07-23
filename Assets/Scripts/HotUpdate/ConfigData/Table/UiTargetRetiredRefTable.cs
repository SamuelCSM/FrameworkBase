// ==========================================
// 自动生成的表加载类: UiTargetRetiredRefTable
// 来源工作表: ui_target_retired_ref
// 生成时间: 2026-07-22 11:30:16
// ==========================================

using System;
using Framework.Data;
using Framework.Foundation;
using HotUpdate.Config.Data;

namespace HotUpdate.Config.Table
{
    /// <summary>
    /// UiTargetRetiredRef 表加载器。
    /// </summary>
    public class UiTargetRetiredRefTable : ConfigBase<int, UiTargetRetiredRef>
    {
        /// <summary>
        /// 构造函数。
        /// </summary>
        public UiTargetRetiredRefTable()
        {
            // ConfigManager 会按需加载该配置表。
        }

        /// <summary>
        /// 返回单行配置数据的主键。
        /// </summary>
        protected override int GetKey(UiTargetRetiredRef item)
        {
            return item.Id;
        }
    }
}
