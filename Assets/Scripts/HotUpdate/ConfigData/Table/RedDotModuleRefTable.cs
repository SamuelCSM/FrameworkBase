// ==========================================
// 自动生成的表加载类: RedDotModuleRefTable
// 来源工作表: red_dot_module_ref
// 生成时间: 2026-07-23 10:57:15
// ==========================================

using System;
using Framework.Data;
using Framework.Foundation;
using HotUpdate.Config.Data;

namespace HotUpdate.Config.Table
{
    /// <summary>
    /// RedDotModuleRef 表加载器。
    /// </summary>
    public class RedDotModuleRefTable : ConfigBase<int, RedDotModuleRef>
    {
        /// <summary>
        /// 构造函数。
        /// </summary>
        public RedDotModuleRefTable()
        {
            // ConfigManager 会按需加载该配置表。
        }

        /// <summary>
        /// 返回单行配置数据的主键。
        /// </summary>
        protected override int GetKey(RedDotModuleRef item)
        {
            return item.Id;
        }
    }
}
