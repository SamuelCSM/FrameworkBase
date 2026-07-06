using System;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEngine;

namespace Framework.Editor
{
    /// <summary>
    /// 命令行（batchmode）构建入口。构建机 / CI 通过 -executeMethod 调用，Editor 窗口不依赖本类。
    ///
    /// 用法（在工程未被编辑器占用时）：
    /// <code>
    /// Unity.exe -batchmode -projectPath &lt;工程根&gt; -buildTarget StandaloneWindows64 ^
    ///   -executeMethod Framework.Editor.BuildEntry.BuildPlayer ^
    ///   -outputPath Builds/Windows/Game.exe [-development] -logFile Logs/build.log
    /// </code>
    ///
    /// EditMode 测试无需本类，直接用 Unity 内置测试跑批：
    /// <code>
    /// Unity.exe -batchmode -projectPath &lt;工程根&gt; -runTests -testPlatform EditMode ^
    ///   -testResults Logs/ci/editmode-results.xml -logFile Logs/ci/editmode.log
    /// </code>
    ///
    /// 约定：方法内部自行 EditorApplication.Exit（0=成功 / 1=失败），调用方不要再传 -quit。
    /// </summary>
    public static class BuildEntry
    {
        /// <summary>
        /// 构建 Player（使用 Build Settings 勾选的场景与当前 -buildTarget 平台）。
        /// 参数：-outputPath（必填）；-development（可选，Development Build + ScriptDebugging）。
        /// 打包前置步骤（Addressables / StreamingAssets 同步 / 热更安全检查）由既有的
        /// IPreprocessBuildWithReport 回调与发布管线负责，本入口只做标准 BuildPlayer 收口。
        /// </summary>
        public static void BuildPlayer()
        {
            try
            {
                string outputPath = GetArgValue("-outputPath");
                if (string.IsNullOrEmpty(outputPath))
                    Fail("缺少必填参数 -outputPath（Player 输出完整路径，如 Builds/Windows/Game.exe）");

                string[] scenes = EditorBuildSettings.scenes
                    .Where(s => s.enabled)
                    .Select(s => s.path)
                    .ToArray();
                if (scenes.Length == 0)
                    Fail("Build Settings 中没有启用的场景，无法构建");

                var options = new BuildPlayerOptions
                {
                    scenes = scenes,
                    locationPathName = outputPath,
                    target = EditorUserBuildSettings.activeBuildTarget,
                    options = HasArg("-development")
                        ? BuildOptions.Development | BuildOptions.AllowDebugging
                        : BuildOptions.None
                };

                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outputPath)) ?? ".");

                Debug.Log($"[BuildEntry] BuildPlayer 开始: target={options.target} scenes={scenes.Length} output={outputPath}");
                BuildReport report = BuildPipeline.BuildPlayer(options);
                BuildSummary summary = report.summary;

                Debug.Log($"[BuildEntry] BuildPlayer 结束: result={summary.result} " +
                          $"size={summary.totalSize / (1024 * 1024)}MB " +
                          $"time={summary.totalTime.TotalSeconds:F0}s " +
                          $"errors={summary.totalErrors} warnings={summary.totalWarnings}");

                if (summary.result != BuildResult.Succeeded)
                    Fail($"BuildPlayer 失败: {summary.result}（详见构建日志）");

                EditorApplication.Exit(0);
            }
            catch (Exception ex)
            {
                Fail($"BuildPlayer 异常: {ex.Message}\n{ex.StackTrace}");
            }
        }

        // ── 命令行参数工具 ────────────────────────────────────────────────────

        /// <summary>取命令行 "-key value" 形式参数的 value；不存在返回 null。</summary>
        private static string GetArgValue(string key)
        {
            string[] args = Environment.GetCommandLineArgs();
            for (int i = 0; i < args.Length - 1; i++)
            {
                if (string.Equals(args[i], key, StringComparison.OrdinalIgnoreCase))
                    return args[i + 1];
            }
            return null;
        }

        /// <summary>命令行是否包含指定开关参数。</summary>
        private static bool HasArg(string key)
        {
            return Environment.GetCommandLineArgs()
                .Any(a => string.Equals(a, key, StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>记录错误并以退出码 1 结束 batchmode 进程。</summary>
        private static void Fail(string message)
        {
            Debug.LogError($"[BuildEntry] {message}");
            EditorApplication.Exit(1);
        }
    }
}
