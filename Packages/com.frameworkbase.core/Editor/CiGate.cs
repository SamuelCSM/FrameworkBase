using System;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace Framework.Editor
{
    /// <summary>
    /// 命令行（batchmode）资源门禁入口。CI / 提交前脚本经 <c>-executeMethod</c> 调用，
    /// 把已有的资源规范校验器串成一道不依赖完整出包的检查，拦截「包体一版版胖、真机豆腐块」等
    /// 长期运营隐患。Editor 窗口不依赖本类。
    ///
    /// 用法（工程未被编辑器占用时）：
    /// <code>
    /// Unity.exe -batchmode -nographics -projectPath &lt;工程根&gt; ^
    ///   -executeMethod Framework.Editor.CiGate.RunAssetGate ^
    ///   [-strictFonts] -logFile Logs/ci/asset-gate.log
    /// </code>
    ///
    /// 当前检查项：
    ///   1. <b>Addressables 校验</b>（<see cref="AddressablesValidator.ValidateForBuild"/>）——Error 级<b>阻断</b>；
    ///      Settings 不存在（纯框架壳）视为通过。
    ///   2. <b>字体缺字</b>（<see cref="FontCoverageChecker.CheckFontsForCi"/>）——默认<b>告警不阻断</b>
    ///      （避免图标字体/局部字库误报卡 CI）；传 <c>-strictFonts</c> 升级为阻断。
    ///      config.db 不存在或工程无 TMP 字体时跳过（视为通过）。
    ///
    /// 约定：方法内部自行 <see cref="EditorApplication.Exit"/>（0=通过 / 1=有阻断项），调用方不要再传 -quit。
    /// 包体大小检查依赖已出包产物，属构建后置检查（见 <c>FullPackageBuildPostprocessor</c>），不在本门禁内。
    /// </summary>
    public static class CiGate
    {
        /// <summary>执行资源门禁并以退出码收口。</summary>
        public static void RunAssetGate()
        {
            try
            {
                int exitCode = 0;
                Debug.Log("[CiGate] ===== 资源门禁开始 =====");

                // ── 1) Addressables 校验（Error 级阻断）─────────────────────────
                if (AddressablesValidator.ValidateForBuild(out string addrSummary))
                {
                    Debug.Log("[CiGate] ✓ Addressables 校验通过");
                }
                else
                {
                    Debug.LogError("[CiGate] ✗ Addressables 门禁未通过：\n" + addrSummary);
                    exitCode = 1;
                }

                // ── 2) 字体缺字（默认告警；-strictFonts 阻断）───────────────────
                bool strictFonts = HasArg("-strictFonts");
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
                            Debug.LogError($"[CiGate] ✗ {fontsWithMissing} 个字体存在缺字（-strictFonts 阻断）");
                            exitCode = 1;
                        }
                        else
                        {
                            Debug.LogWarning($"[CiGate] {fontsWithMissing} 个字体存在缺字（告警，未阻断；加 -strictFonts 可阻断）");
                        }
                    }
                }

                Debug.Log($"[CiGate] ===== 资源门禁结束（exit={exitCode}）=====");
                // 纯 ASCII 结论哨兵：供 CI 脚本免受日志编码影响地判定门禁结果
                // （batchmode 进程退出码不可靠，脚本以此行为准，不信进程码）。
                Debug.Log($"[CiGate] GATE_RESULT exit={exitCode}");
                EditorApplication.Exit(exitCode);
            }
            catch (Exception ex)
            {
                Debug.LogError($"[CiGate] 门禁执行异常: {ex.Message}\n{ex.StackTrace}");
                EditorApplication.Exit(1);
            }
        }

        /// <summary>命令行是否包含指定开关参数。</summary>
        private static bool HasArg(string key)
        {
            return Environment.GetCommandLineArgs()
                .Any(a => string.Equals(a, key, StringComparison.OrdinalIgnoreCase));
        }
    }
}
