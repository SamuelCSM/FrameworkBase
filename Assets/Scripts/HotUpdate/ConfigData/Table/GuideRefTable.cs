// ==========================================
// 自动生成的表加载类: GuideRefTable
// 来源工作表: guide_ref
// 生成时间: 2026-07-22 11:30:15
// ==========================================

using System;
using Framework.Data;
using Framework.Foundation;
using HotUpdate.Config.Data;

namespace HotUpdate.Config.Table
{
    /// <summary>
    /// GuideRef 表加载器。
    /// </summary>
    public class GuideRefTable : ConfigBase<int, GuideRef>
    {
        /// <summary>
        /// 构造函数。
        /// </summary>
        public GuideRefTable()
        {
            // ConfigManager 会按需加载该配置表。
        }

        /// <summary>
        /// 返回单行配置数据的主键。
        /// </summary>
        protected override int GetKey(GuideRef item)
        {
            return item.Id;
        }
    }
}
