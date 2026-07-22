// ==========================================
// 自动生成的表加载类: TriggerUiTargetClickPayloadRefTable
// 来源工作表: trigger_ui_target_click_payload_ref
// 生成时间: 2026-07-22 11:30:15
// ==========================================

using System;
using Framework.Data;
using Framework.Foundation;
using HotUpdate.Config.Data;

namespace HotUpdate.Config.Table
{
    /// <summary>
    /// TriggerUiTargetClickPayloadRef 表加载器。
    /// </summary>
    public class TriggerUiTargetClickPayloadRefTable : ConfigBase<int, TriggerUiTargetClickPayloadRef>
    {
        /// <summary>
        /// 构造函数。
        /// </summary>
        public TriggerUiTargetClickPayloadRefTable()
        {
            // ConfigManager 会按需加载该配置表。
        }

        /// <summary>
        /// 返回单行配置数据的主键。
        /// </summary>
        protected override int GetKey(TriggerUiTargetClickPayloadRef item)
        {
            return item.Id;
        }
    }
}
