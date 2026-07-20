// ==========================================
// 自动生成的表加载类: RedDotRetiredRefTable
// 来源工作表: red_dot_retired_ref
// ==========================================

using System;
using Framework.Data;
using Framework.Foundation;
using HotUpdate.Config.Data;

namespace HotUpdate.Config.Table
{
    /// <summary>RedDotRetiredRef 表加载器。</summary>
    public class RedDotRetiredRefTable : ConfigBase<int, RedDotRetiredRef>
    {
        public RedDotRetiredRefTable() { }

        protected override int GetKey(RedDotRetiredRef item) => item.Id;
    }
}
