// ==========================================
// 自动生成的配置表加载类: LoadingTipsRefTable
// 来源表: loading_tips
// 生成时间: 2026-05-27 21:35:42
// 警告: 请勿手动修改此文件！
// ==========================================

using System;
using Framework.Data;

namespace Framework.Table
{
    /// <summary>
    /// LoadingTipsRef 配置表加载器
    /// </summary>
    public class LoadingTipsRefTable : ConfigBase<long, LoadingTipsRef>
    {
        /// <summary>
        /// 构造函数
        /// </summary>
        public LoadingTipsRefTable()
        {
            // ConfigManager 会按需调用 Load(dbPath, "loading_tips")。
        }

        /// <summary>
        /// 获取配置项的主键
        /// </summary>
        protected override long GetKey(LoadingTipsRef item)
        {
            return item.Id;
        }
    }
}
