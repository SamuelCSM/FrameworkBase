using Framework.Core;
using HotUpdate.Guide;
using HotUpdate.RedDot;
using HotUpdate.UI;

namespace HotUpdate.Entry
{
    /// <summary>热更侧所有配置目录的单一安装入口，避免多个模块互相覆盖 OnBeforeBusinessEntry。</summary>
    public static class RuntimeCatalogBootstrap
    {
        public static void RegisterPreEntryHook()
        {
            GameEntry.OnBeforeBusinessEntry = Install;
        }

        public static void Install()
        {
            UIWindowBootstrap.Install();
            RedDotBootstrap.Install();
            GuideBootstrap.Install();
        }
    }
}
