// ==========================================
// 自动生成的表加载类: RedDotModuleRefTable
// 来源工作表: red_dot_module_ref
// ==========================================

using System;
using Framework.Data;
using Framework.Foundation;
using HotUpdate.Config.Data;

namespace HotUpdate.Config.Table
{
    /// <summary>RedDotModuleRef 表加载器。</summary>
    public class RedDotModuleRefTable : ConfigBase<int, RedDotModuleRef>
    {
        public RedDotModuleRefTable() { }

        protected override int GetKey(RedDotModuleRef item) => item.Id;
    }
}
