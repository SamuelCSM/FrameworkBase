using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Framework.Core;
using Framework.HotUpdate;
using UnityEditor;
using UnityEditor.AddressableAssets;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEditor.Compilation;
using UnityEngine;

namespace Framework.Editor
{
    /// <summary>
    /// Player 构建前的工程可复现性门禁。
    /// <para>
    /// 该门禁只校验“从一个干净工作副本能否得到相同构建输入”所必需的工程状态，覆盖 Unity ProjectSettings、
    /// Addressables Settings、AppConfig、HybridCLR 配置、Scripting Backend 以及启用的 Build Settings 场景。
    /// </para>
    /// <para>
    /// 本类不负责校验签名私钥、上传凭据等 Secret。此类敏感信息必须由 CI 密钥系统注入，不能为了可复现性提交到仓库。
    /// 校验失败必须中断 Player 构建，避免本地缓存掩盖配置缺失，并防止不可复现的产物进入发布链路。
    /// </para>
    /// </summary>
    public sealed class ProjectReproducibilityBuildCheck : IPreprocessBuildWithReport
    {
        /// <summary>
        /// 使用较早的回调顺序，确保可复现性问题先于 Addressables、HybridCLR 及正式 Player 构建步骤暴露。
        /// </summary>
        public int callbackOrder => -200;

        /// <summary>
        /// 执行 Player 构建前校验；任何必需输入缺失时抛出 <see cref="BuildFailedException"/>，禁止继续生成产物。
        /// </summary>
        /// <param name="report">Unity 当前 Player 构建报告，用于取得真实目标平台。</param>
        public void OnPreprocessBuild(BuildReport report)
        {
            if (!ProjectReproducibilityValidator.Validate(
                    report.summary.platform,
                    requireBuildScenes: true,
                    out string validationReport))
            {
                throw new BuildFailedException(
                    "[ProjectReproducibility] 工程可复现性校验失败：\n" + validationReport);
            }

            Debug.Log("[ProjectReproducibility] 工程可复现性校验通过。");
        }
    }

    /// <summary>
    /// 工程可复现性校验器。
    /// <para>
    /// 校验逻辑与构建回调分离，便于 CI 命令行、Editor 工具和 EditMode 测试复用同一套规则，
    /// 避免本地构建与持续集成各自维护一套标准后逐渐产生差异。
    /// </para>
    /// </summary>
    public static class ProjectReproducibilityValidator
    {
        /// <summary>
        /// 从干净工作副本恢复 Unity 工程和 Player 构建输入所必需的 ProjectSettings 文件。
        /// 此处只列关键文件；完整 ProjectSettings 目录仍应整体纳入版本控制。
        /// </summary>
        private static readonly string[] RequiredProjectFiles =
        {
            "ProjectSettings/ProjectVersion.txt",
            "ProjectSettings/ProjectSettings.asset",
            "ProjectSettings/EditorBuildSettings.asset",
            "ProjectSettings/EditorSettings.asset",
            "ProjectSettings/QualitySettings.asset",
            "ProjectSettings/GraphicsSettings.asset",
        };

        /// <summary>
        /// 校验指定构建目标所依赖的关键工程输入是否完整且相互一致。
        /// </summary>
        /// <param name="target">即将构建的 Unity 目标平台。</param>
        /// <param name="requireBuildScenes">是否要求 Build Settings 至少存在一个启用场景；纯静态检查可按需关闭。</param>
        /// <param name="report">返回逐项问题报告；成功时固定为 PASS。</param>
        /// <returns>全部规则通过时返回 <see langword="true"/>；存在任一问题时返回 <see langword="false"/>。</returns>
        public static bool Validate(
            BuildTarget target,
            bool requireBuildScenes,
            out string report)
        {
            var issues = new List<string>();
            string projectRoot = Directory.GetParent(Application.dataPath)?.FullName ?? Directory.GetCurrentDirectory();

            foreach (string relativePath in RequiredProjectFiles)
            {
                string fullPath = Path.Combine(projectRoot, relativePath);
                if (!File.Exists(fullPath))
                    issues.Add($"缺少必要工程文件：{relativePath}");
            }

            // LaunchFlow 的资源初始化依赖 Addressables。Settings 缺失时，本地 Library 缓存可能暂时掩盖问题，
            // 但干净机器和 CI 一定无法稳定构建，因此必须在进入实际构建前失败。
            if (AddressableAssetSettingsDefaultObject.Settings == null)
            {
                issues.Add(
                    "缺少 Assets/AddressableAssetsData/AddressableAssetSettings.asset；" +
                    "请通过 Framework/Setup Addressables 生成并提交完整配置。");
            }

            AppConfigAsset appConfig = Resources.Load<AppConfigAsset>("AppConfig");
            if (appConfig == null)
            {
                issues.Add("缺少 Resources/AppConfig.asset，无法确定环境、热更新及网络构建参数。");
            }
            else if (appConfig.EnableHotUpdate)
            {
                string hybridClrSettings = Path.Combine(projectRoot, "ProjectSettings/HybridCLRSettings.asset");
                if (!File.Exists(hybridClrSettings))
                {
                    issues.Add(
                        "EnableHotUpdate=true，但缺少 ProjectSettings/HybridCLRSettings.asset；" +
                        "干净工作副本无法重建 HybridCLR 生成物。");
                }

                BuildTargetGroup group = BuildPipeline.GetBuildTargetGroup(target);
                if (group != BuildTargetGroup.Unknown &&
                    PlayerSettings.GetScriptingBackend(group) != ScriptingImplementation.IL2CPP)
                {
                    issues.Add($"EnableHotUpdate=true，但 {target} 未使用 IL2CPP Scripting Backend。");
                }

                ValidateHotUpdateAssemblies(appConfig, issues);
            }

            if (requireBuildScenes && !EditorBuildSettings.scenes.Any(scene => scene.enabled))
                issues.Add("Build Settings 中没有任何启用场景，无法生成可启动的 Player。");

            report = issues.Count == 0
                ? "PASS"
                : string.Join("\n", issues.Select(issue => "- " + issue));
            return issues.Count == 0;
        }

        /// <summary>
        /// 校验 AppConfig 声明的热更新程序集是否确实存在于当前 Player 编译图中。
        /// <para>
        /// 该规则用于防止 HybridCLR 配置、AppConfig 与 asmdef 三者漂移：如果清单声明了不存在的程序集，
        /// 客户端只有在运行到热更新加载阶段才会失败，属于必须前移到构建期发现的问题。
        /// </para>
        /// </summary>
        /// <param name="appConfig">当前工程实际加载的应用配置。</param>
        /// <param name="issues">问题收集器；发现错误时追加可直接定位的中文说明。</param>
        private static void ValidateHotUpdateAssemblies(AppConfigAsset appConfig, List<string> issues)
        {
            string[] required = appConfig.HotUpdateAssemblyFiles != null && appConfig.HotUpdateAssemblyFiles.Length > 0
                ? appConfig.HotUpdateAssemblyFiles
                    .Where(name => !string.IsNullOrWhiteSpace(name))
                    .Select(VersionManager.ToAssemblyName)
                    .ToArray()
                : VersionManager.HotUpdateAssemblyFileNames
                    .Select(VersionManager.ToAssemblyName)
                    .ToArray();

            var playerAssemblies = new HashSet<string>(
                CompilationPipeline.GetAssemblies(AssembliesType.Player).Select(assembly => assembly.name),
                StringComparer.Ordinal);

            foreach (string assemblyName in required)
            {
                if (!playerAssemblies.Contains(assemblyName))
                    issues.Add($"热更新程序集未进入 Player 编译图：{assemblyName}");
            }
        }
    }
}
