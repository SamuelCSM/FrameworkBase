// ==========================================
// 自动生成的表加载类: RedDotSeenPolicyRefTable
// 来源工作表: red_dot_seen_policy_ref
// 生成时间: 2026-07-23 10:57:15
// ==========================================

using System;
using Framework.Data;
using Framework.Foundation;
using HotUpdate.Config.Data;

namespace HotUpdate.Config.Table
{
    /// <summary>
    /// RedDotSeenPolicyRef 表加载器。
    /// </summary>
    public class RedDotSeenPolicyRefTable : ConfigBase<int, RedDotSeenPolicyRef>
    {
        /// <summary>
        /// 构造函数。
        /// </summary>
        public RedDotSeenPolicyRefTable()
        {
            // ConfigManager 会按需加载该配置表。
        }

        /// <summary>
        /// 返回单行配置数据的主键。
        /// </summary>
        protected override int GetKey(RedDotSeenPolicyRef item)
        {
            return item.SignalId;
        }
    }
}
