using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using HybridCLR.Editor;
using HybridCLR.Editor.Settings;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEditor.Compilation;
using UnityEditor.UnityLinker;
using UnityEngine;
using CompiledAssembly = UnityEditor.Compilation.Assembly;

namespace Framework.Editor
{
    /// <summary>
    /// 出包期向 UnityLinker 追加 link.xml：把「热更程序集所引用的 AOT 侧程序集」整体标记为保留。
    /// <para>
    /// 热更场景下托管代码裁剪的判据是错的。裁剪器以 AOT 侧的引用图为根做可达性分析，而热更 dll
    /// 不在这张图里——它出包时还不存在，将来还会被替换。于是「只被热更侧调用的 AOT 类型」在裁剪器
    /// 看来是死代码，被剪掉后热更程序集加载即抛 <c>Could not load type ...</c>。这个失败只在
    /// IL2CPP 出包后出现：编辑器是 Mono、不裁剪，看不到。
    /// </para>
    /// <para>
    /// HybridCLR 自带的 <c>Generate/LinkXml</c> 只覆盖<b>出包当刻</b>热更 dll 里真实引用到的成员，
    /// 挡不住后续热更版本新调一个当时没人用的 AOT API——那才是热更最要命的形态：包已发到玩家手里，
    /// 补丁一推就崩，且无法再靠改 Player 补救。故此处按程序集粒度整体保留，把 AOT 侧对热更的
    /// 承诺固定为「asmdef 里声明引用的程序集，其公开面在包的生命周期内始终可用」。
    /// </para>
    /// <para>
    /// 代价是包体：这些程序集的未使用代码不再被剪。仅在 <c>HybridCLRSettings.enable</c> 为真时生效，
    /// 不启用热更的工程完全不受影响。见 ADR-009。
    /// </para>
    /// </summary>
    public sealed class HotUpdateAotPreserveLinkXml : IUnityLinkerProcessor
    {
        /// <summary>与其他 linker 处理器无顺序依赖，取默认序。</summary>
        public int callbackOrder => 0;

        /// <summary>生成的 link.xml 落盘位置（工程 Temp 目录，随构建产生、无需入库）。</summary>
        private const string OutputRelativePath = "Temp/FrameworkHotUpdateAotPreserve.link.xml";

        /// <summary>
        /// UnityLinker 回调：返回追加 link.xml 的绝对路径；返回 null 表示本次构建不追加。
        /// </summary>
        /// <param name="report">构建报告，本实现不使用。</param>
        /// <param name="data">链接管线数据，本实现不使用（保留集合由 asmdef 引用图推导）。</param>
        /// <returns>link.xml 绝对路径；未启用热更或无可保留程序集时为 null。</returns>
        public string GenerateAdditionalLinkXmlFile(BuildReport report, UnityLinkerBuildPipelineData data)
        {
            if (!HybridCLRSettings.Instance.enable)
                return null;

            List<string> preserved = ResolveAotAssembliesReferencedByHotUpdate();
            if (preserved.Count == 0)
            {
                Debug.LogWarning("[HotUpdateAotPreserve] 未解析到任何需保留的 AOT 程序集，跳过 link.xml 追加。" +
                                 "请检查 HybridCLRSettings.hotUpdateAssemblies 是否与实际 asmdef 名一致。");
                return null;
            }

            string path = Path.GetFullPath(OutputRelativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, BuildLinkXml(preserved), new UTF8Encoding(false));

            Debug.Log($"[HotUpdateAotPreserve] 保留 {preserved.Count} 个 AOT 程序集：{string.Join(", ", preserved)}\n-> {path}");
            return path;
        }

        /// <summary>
        /// 推导需整体保留的 AOT 程序集：以热更程序集为起点，沿 asmdef 引用图求传递闭包，再剔除热更程序集自身。
        /// </summary>
        /// <remarks>
        /// 以 <see cref="CompilationPipeline"/> 的 Player 程序集图为准而非直接读 asmdef 的 references 字段：
        /// references 可能写成 <c>GUID:xxx</c> 形式，且平台约束（includePlatforms 等）会改变实际引用集合，
        /// 编译管线给出的是当前构建目标下真正成立的那张图。
        /// <para>
        /// 只取 <c>assemblyReferences</c>（asmdef 定义的程序集），不取 <c>compiledAssemblyReferences</c>
        /// （预编译 dll，如 Google.Protobuf）：后者对每个程序集都会带出全量自动引用的 dll 列表，按整体保留会把
        /// 无关第三方库一并锁死。预编译 dll 的保留仍交给 HybridCLR 生成的 link.xml 与常规可达性分析。
        /// </para>
        /// </remarks>
        /// <returns>按名称排序的程序集名列表（不含 .dll 后缀）。</returns>
        private static List<string> ResolveAotAssembliesReferencedByHotUpdate()
        {
            var hotUpdateNames = new HashSet<string>(
                SettingsUtil.HotUpdateAssemblyNamesIncludePreserved, StringComparer.Ordinal);

            Dictionary<string, CompiledAssembly> playerAssemblies = CompilationPipeline
                .GetAssemblies(AssembliesType.Player)
                .GroupBy(a => a.name, StringComparer.Ordinal)
                .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);

            var visited = new HashSet<string>(StringComparer.Ordinal);
            var pending = new Stack<string>();
            foreach (string name in hotUpdateNames)
            {
                if (playerAssemblies.ContainsKey(name))
                    pending.Push(name);
                else
                    Debug.LogWarning($"[HotUpdateAotPreserve] 热更程序集 {name} 不在 Player 编译产物中，已跳过。");
            }

            while (pending.Count > 0)
            {
                string current = pending.Pop();
                if (!visited.Add(current) || !playerAssemblies.TryGetValue(current, out CompiledAssembly assembly))
                    continue;

                foreach (CompiledAssembly reference in assembly.assemblyReferences)
                    pending.Push(reference.name);
            }

            // 热更程序集本身由 HybridCLR 从 Player 中剥离，不参与 AOT 侧保留。
            visited.ExceptWith(hotUpdateNames);

            var result = visited.ToList();
            result.Sort(StringComparer.Ordinal);
            return result;
        }

        /// <summary>按 UnityLinker 的 link.xml 结构拼装整体保留声明。</summary>
        /// <param name="assemblies">需保留的程序集名。</param>
        /// <returns>link.xml 全文。</returns>
        private static string BuildLinkXml(IEnumerable<string> assemblies)
        {
            var sb = new StringBuilder();
            sb.AppendLine("<?xml version=\"1.0\" encoding=\"utf-8\"?>");
            sb.AppendLine("<!-- 由 Framework.Editor.HotUpdateAotPreserveLinkXml 出包期生成，请勿手工编辑。 -->");
            sb.AppendLine("<linker>");
            foreach (string name in assemblies)
            {
                // ignoreIfMissing：程序集可能因平台约束不参与本次构建，缺失时不应中断链接。
                sb.AppendLine($"  <assembly fullname=\"{name}\" preserve=\"all\" ignoreIfMissing=\"1\" />");
            }
            sb.AppendLine("</linker>");
            return sb.ToString();
        }
    }
}
