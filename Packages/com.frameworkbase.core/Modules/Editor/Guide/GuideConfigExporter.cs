using System.IO;

namespace Framework.Editor.Guide
{
    /// <summary>把引导跨表校验与产物生成接入 ConfigPipeline 的统一发现（ADR-008 形态 C）。</summary>
    internal sealed class GuideConfigExporter : IConfigArtifactExporter
    {
        // 引导校验内部引用窗口配置，故排在 UI 窗口(20)之后。
        public int Order => 30;
        public string DisplayName => "引导";

        /// <summary>无 Guide.xlsx 视为无需导出（返回 true）；校验失败返回 false 由管线中止。</summary>
        public bool Export(out string report)
        {
            if (!File.Exists(GuideConfigCompiler.WorkbookPath)) { report = "无 Guide.xlsx，跳过。"; return true; }
            if (!GuideConfigCompiler.TryCompile(out GuideConfigCompilation compilation, out report)) return false;
            GuideConfigCompiler.WriteArtifacts(compilation);
            return true;
        }
    }
}
