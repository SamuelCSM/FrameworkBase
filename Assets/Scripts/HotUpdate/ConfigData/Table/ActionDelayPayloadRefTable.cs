// ==========================================
// 自动生成的表加载类: ActionDelayPayloadRefTable
// 来源工作表: action_delay_payload_ref
// 生成时间: 2026-07-22 11:30:15
// ==========================================

using System;
using Framework.Data;
using Framework.Foundation;
using HotUpdate.Config.Data;

namespace HotUpdate.Config.Table
{
    /// <summary>
    /// ActionDelayPayloadRef 表加载器。
    /// </summary>
    public class ActionDelayPayloadRefTable : ConfigBase<int, ActionDelayPayloadRef>
    {
        /// <summary>
        /// 构造函数。
        /// </summary>
        public ActionDelayPayloadRefTable()
        {
            // ConfigManager 会按需加载该配置表。
        }

        /// <summary>
        /// 返回单行配置数据的主键。
        /// </summary>
        protected override int GetKey(ActionDelayPayloadRef item)
        {
            return item.Id;
        }
    }
}
