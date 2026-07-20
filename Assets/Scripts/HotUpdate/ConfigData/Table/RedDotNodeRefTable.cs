// ==========================================
// 自动生成的表加载类: RedDotNodeRefTable
// 来源工作表: red_dot_node_ref
// ==========================================

using System;
using Framework.Data;
using Framework.Foundation;
using HotUpdate.Config.Data;

namespace HotUpdate.Config.Table
{
    /// <summary>RedDotNodeRef 表加载器。</summary>
    public class RedDotNodeRefTable : ConfigBase<int, RedDotNodeRef>
    {
        public RedDotNodeRefTable() { }

        protected override int GetKey(RedDotNodeRef item) => item.Id;
    }
}
