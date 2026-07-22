using Framework.Core;
using HotUpdate.Guide;
using HotUpdate.RedDot;
using HotUpdate.UI;

namespace HotUpdate.Entry
{
    /// <summary>
    /// 热更侧所有配置目录的单一安装入口，避免多个模块互相覆盖 GameEntry 上的单槽钩子
    /// （<c>OnBeforeBusinessEntry</c> / <c>OnFreezeOrchestration</c> 都是赋值语义，不是多播）。
    /// </summary>
    public static class RuntimeCatalogBootstrap
    {
        /// <summary>把本类挂成唯一的登录前装配入口；重复调用只是重挂同一委托。</summary>
        public static void RegisterPreEntryHook()
        {
            GameEntry.OnBeforeBusinessEntry = Install;
        }

        /// <summary>
        /// 按依赖顺序装配：先由 <see cref="OrchestrationBootstrap"/> 独占编排冻结钩子，
        /// 再让各模块登记自身（模块在 Install 里向它贡献 Payload 工厂，冻结时统一求值）。
        /// </summary>
        public static void Install()
        {
            OrchestrationBootstrap.Install();
            UIWindowBootstrap.Install();
            RedDotBootstrap.Install();
            GuideBootstrap.Install();
        }
    }
}
