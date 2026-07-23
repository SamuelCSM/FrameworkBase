// ==========================================
// 自动生成的表加载类: TriggerDelayPayloadRefTable
// 来源工作表: trigger_delay_payload_ref
// 生成时间: 2026-07-22 11:30:15
// ==========================================

using System;
using Framework.Data;
using Framework.Foundation;
using HotUpdate.Config.Data;

namespace HotUpdate.Config.Table
{
    /// <summary>
    /// TriggerDelayPayloadRef 表加载器。
    /// </summary>
    public class TriggerDelayPayloadRefTable : ConfigBase<int, TriggerDelayPayloadRef>
    {
        /// <summary>
        /// 构造函数。
        /// </summary>
        public TriggerDelayPayloadRefTable()
        {
            // ConfigManager 会按需加载该配置表。
        }

        /// <summary>
        /// 返回单行配置数据的主键。
        /// </summary>
        protected override int GetKey(TriggerDelayPayloadRef item)
        {
            return item.Id;
        }
    }
}
