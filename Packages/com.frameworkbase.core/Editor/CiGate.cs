using System;
using System.Linq;
using UnityEditor;
using UnityEditor.Build;
using UnityEngine;

namespace Framework.Editor
{
    /// <summary>
    /// 命令行资源质量门禁，统一执行工程可复现性、Addressables 规则、纹理审计和字体覆盖检查。
    /// <para>
    /// <see cref="RunAssetGate"/> 供直接 Unity batchmode 调用并自行退出；
    /// <see cref="RunAssetGateForBuilder"/> 供 GameCI unity-builder 等外部构建器调用，失败时抛异常但不抢占宿主退出流程。
    /// 两个入口复用同一个 <see cref="EvaluateAssetGate"/>，避免 CI 与本地标准漂移。
    /// </para>
    /// </summary>
    public static class CiGate
    {
        /// <summary>
        /// 独立 batchmode 入口。调用示例：
        /// <code>
        /// Unity.exe -batchmode -nographics -projectPath &lt;工程根&gt;
        ///   -executeMethod Framework.Editor.CiGate.RunAssetGate -strictFonts
        /// </code>
        /// </summary>
        public static void RunAssetGate()
        {
            try
            {
                int exitCode = EvaluateAssetGate(HasArg("-strictFonts"));
                Debug.Log($"[CiGate] GATE_RESULT exit={exitCode}");
                EditorApplication.Exit(exitCode);
            }
            catch (Exception ex)
            {
                Debug.LogError($"[CiGate] 门禁执行异常：{ex.Message}\n{ex.StackTrace}");
                Debug.Log("[CiGate] GATE_RESULT exit=1");
                EditorApplication.Exit(1);
            }
        }

        /// <summary>
        /// 外部构建器入口。门禁失败抛出 <see cref="BuildFailedException"/>，让宿主可靠获得非零结果；本方法不调用 EditorApplication.Exit。
        /// </summary>
        public static void RunAssetGateForBuilder()
        {
            int exitCode = EvaluateAssetGate(HasArg("-strictFonts"));
            Debug.Log($"[CiGate] GATE_RESULT exit={exitCode}");
            if (exitCode != 0)
                throw new BuildFailedException("FrameworkBase 资源质量门禁未通过，请查看上方逐项报告。");
        }

        /// <summary>
        /// 执行全部门禁并返回 0/1 结果，不退出 Editor，也不吞掉校验器异常。
        /// </summary>
        public static int EvaluateAssetGate(bool strictFonts)
        {
            int exitCode = 0;
            Debug.Log("[CiGate] ===== 资源门禁开始 =====");

            if (ProjectReproducibilityValidator.Validate(
                    EditorUserBuildSettings.activeBuildTarget,
                    requireBuildScenes: false,
                    out string reproducibilityReport))
            {
                Debug.Log("[CiGate] 工程可复现性校验通过。");
            }
            else
            {
                Debug.LogError("[CiGate] 工程可复现性校验失败：\n" + reproducibilityReport);
                exitCode = 1;
            }

            if (AddressablesValidator.ValidateForBuild(out string addressablesSummary))
            {
                Debug.Log("[CiGate] Addressables 校验通过。");
            }
            else
            {
                Debug.LogError("[CiGate] Addressables 门禁未通过：\n" + addressablesSummary);
                exitCode = 1;
            }

            if (TextureAuditCollector.ValidateForCi(out string textureReport, out int textureErrors))
            {
                Debug.Log("[CiGate] 纹理审计通过。\n" + textureReport);
            }
            else
            {
                Debug.LogError($"[CiGate] 纹理审计未通过（{textureErrors} 条 Error）：\n" + textureReport);
                exitCode = 1;
            }

            if (!FontCoverageChecker.CheckFontsForCi(out string fontReport, out int fontsWithMissing))
            {
                Debug.Log("[CiGate] 字体覆盖检查跳过：" + fontReport);
            }
            else
            {
                Debug.Log("[CiGate] 字体覆盖结果：\n" + fontReport);
                if (fontsWithMissing > 0)
                {
                    if (strictFonts)
                    {
                        Debug.LogError($"[CiGate] {fontsWithMissing} 个字体存在缺字，严格模式阻断。");
                        exitCode = 1;
                    }
                    else
                    {
                        Debug.LogWarning($"[CiGate] {fontsWithMissing} 个字体存在缺字，当前仅告警。");
                    }
                }
            }

            Debug.Log($"[CiGate] ===== 资源门禁结束（exit={exitCode}）=====");
            return exitCode;
        }

        private static bool HasArg(string key)
        {
            return Environment.GetCommandLineArgs()
                .Any(argument => string.Equals(argument, key, StringComparison.OrdinalIgnoreCase));
        }
    }
}
