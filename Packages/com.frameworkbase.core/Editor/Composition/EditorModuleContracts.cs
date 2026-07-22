namespace Framework.Editor
{
    /// <summary>
    /// 配置产物导出器契约（ADR-008 形态 C）：各模块/能力实现本接口，声明"如何跨表校验并写生成产物"。
    /// <see cref="ConfigPipeline"/> 用 <c>UnityEditor.TypeCache</c> 发现全部实现并按 <see cref="Order"/> 遍历执行，
    /// 无需静态引用任何具体编译器——新增带配置的模块只要实现本接口即被自动纳入导出，管线零改动。
    /// </summary>
    public interface IConfigArtifactExporter
    {
        /// <summary>执行顺序（升序）；有产物依赖关系时用它排定先后（如引导校验内部引用窗口配置，排在窗口之后）。</summary>
        int Order { get; }

        /// <summary>诊断用显示名。</summary>
        string DisplayName { get; }

        /// <summary>跨表校验并写产物；失败返回 false 并给出 report。缺少工作簿等"无需导出"情形应返回 true。</summary>
        bool Export(out string report);
    }

    /// <summary>
    /// 构建门禁校验器契约（ADR-008 形态 C）：各模块实现本接口声明构建期校验，<see cref="CiGate"/> 用
    /// <c>UnityEditor.TypeCache</c> 发现并遍历，无需静态引用具体校验器。框架级校验（可复现/Addressables/
    /// 纹理/字体）仍由 CiGate 直接执行，只有模块特定校验（如红点）经本接口解耦。
    /// </summary>
    public interface IBuildValidator
    {
        /// <summary>诊断用显示名。</summary>
        string DisplayName { get; }

        /// <summary>执行校验；未通过返回 false 并给出 report。</summary>
        bool Validate(out string report);
    }
}
