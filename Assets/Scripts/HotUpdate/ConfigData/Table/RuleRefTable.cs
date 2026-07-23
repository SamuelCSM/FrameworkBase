// ==========================================
// 自动生成的表加载类: RuleRefTable
// 来源工作表: rule_ref
// 生成时间: 2026-07-22 11:30:15
// ==========================================

using System;
using Framework.Data;
using Framework.Foundation;
using HotUpdate.Config.Data;

namespace HotUpdate.Config.Table
{
    /// <summary>
    /// RuleRef 表加载器。
    /// </summary>
    public class RuleRefTable : ConfigBase<int, RuleRef>
    {
        /// <summary>
        /// 构造函数。
        /// </summary>
        public RuleRefTable()
        {
            // ConfigManager 会按需加载该配置表。
        }

        /// <summary>
        /// 返回单行配置数据的主键。
        /// </summary>
        protected override int GetKey(RuleRef item)
        {
            return item.Id;
        }
    }
}
