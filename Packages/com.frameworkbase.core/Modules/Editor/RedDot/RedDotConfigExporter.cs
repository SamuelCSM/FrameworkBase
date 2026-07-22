using System.IO;

namespace Framework.Editor.RedDot
{
    /// <summary>把红点跨表校验与产物生成接入 ConfigPipeline 的统一发现（ADR-008 形态 C）。</summary>
    internal sealed class RedDotConfigExporter : IConfigArtifactExporter
    {
        // 红点无跨模块产物依赖，最先执行。
        public int Order => 10;
        public string DisplayName => "红点";

        /// <summary>无 RedDot.xlsx 视为无需导出（返回 true）；校验失败返回 false 由管线中止。</summary>
        public bool Export(out string report)
        {
            if (!File.Exists(RedDotConfigCompiler.WorkbookPath)) { report = "无 RedDot.xlsx，跳过。"; return true; }
            if (!RedDotConfigCompiler.TryCompile(out Framework.Foundation.RedDotCatalog catalog, out report)) return false;
            RedDotConfigCompiler.WriteArtifacts(catalog);
            return true;
        }
    }

    /// <summary>把红点构建门禁（配置产物 + Prefab/Scene 引用校验）接入 CiGate 的统一发现（ADR-008 形态 C）。</summary>
    internal sealed class RedDotBuildValidatorAdapter : IBuildValidator
    {
        public string DisplayName => "红点配置与 UI 引用";
        public bool Validate(out string report) => RedDotBuildValidator.ValidateForBuild(out report);
    }
}
