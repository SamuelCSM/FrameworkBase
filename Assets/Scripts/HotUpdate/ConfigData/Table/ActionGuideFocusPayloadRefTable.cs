// ==========================================
// 自动生成的表加载类: ActionGuideFocusPayloadRefTable
// 来源工作表: action_guide_focus_payload_ref
// 生成时间: 2026-07-22 11:30:15
// ==========================================

using System;
using Framework.Data;
using Framework.Foundation;
using HotUpdate.Config.Data;

namespace HotUpdate.Config.Table
{
    /// <summary>
    /// ActionGuideFocusPayloadRef 表加载器。
    /// </summary>
    public class ActionGuideFocusPayloadRefTable : ConfigBase<int, ActionGuideFocusPayloadRef>
    {
        /// <summary>
        /// 构造函数。
        /// </summary>
        public ActionGuideFocusPayloadRefTable()
        {
            // ConfigManager 会按需加载该配置表。
        }

        /// <summary>
        /// 返回单行配置数据的主键。
        /// </summary>
        protected override int GetKey(ActionGuideFocusPayloadRef item)
        {
            return item.Id;
        }
    }
}
