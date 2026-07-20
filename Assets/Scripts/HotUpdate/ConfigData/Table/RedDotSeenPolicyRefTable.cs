// ==========================================
// 自动生成的表加载类: RedDotSeenPolicyRefTable
// 来源工作表: red_dot_seen_policy_ref
// ==========================================

using System;
using Framework.Data;
using Framework.Foundation;
using HotUpdate.Config.Data;

namespace HotUpdate.Config.Table
{
    /// <summary>RedDotSeenPolicyRef 表加载器。</summary>
    public class RedDotSeenPolicyRefTable : ConfigBase<int, RedDotSeenPolicyRef>
    {
        public RedDotSeenPolicyRefTable() { }

        protected override int GetKey(RedDotSeenPolicyRef item) => item.SignalId;
    }
}
