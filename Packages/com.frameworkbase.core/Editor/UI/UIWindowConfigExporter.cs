using System.IO;

namespace Framework.Editor.UI
{
    /// <summary>
    /// 把 UI 窗口跨表校验与产物生成接入 ConfigPipeline 的统一发现（ADR-008 形态 C）。
    /// UIWindow 属框架 UI 能力（非中间层模块），故本导出器留在 Framework.Editor。
    /// </summary>
    internal sealed class UIWindowConfigExporter : IConfigArtifactExporter
    {
        public int Order => 20;
        public string DisplayName => "UI 窗口";

        /// <summary>无 UIWindow.xlsx 视为无需导出（返回 true）；校验失败返回 false 由管线中止。</summary>
        public bool Export(out string report)
        {
            if (!File.Exists(UIWindowConfigCompiler.WorkbookPath)) { report = "无 UIWindow.xlsx，跳过。"; return true; }
            if (!UIWindowConfigCompiler.TryCompile(out Framework.UIWindowCatalog catalog, out report)) return false;
            UIWindowConfigCompiler.WriteArtifacts(catalog);
            return true;
        }
    }
}
