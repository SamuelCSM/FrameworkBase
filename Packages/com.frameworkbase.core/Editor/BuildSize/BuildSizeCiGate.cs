using System;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace Framework.Editor.BuildSize
{
    /// <summary>
    /// 包体尺寸门禁的 batchmode 入口与编辑器菜单。属<b>构建后置</b>检查——需已有产物目录，
    /// 故独立于资源门禁 <see cref="CiGate"/>（后者不依赖出包）。CI 在产出 bundle / 整包后调用。
    ///
    /// 用法：
    /// <code>
    /// Unity.exe -batchmode -nographics -projectPath &lt;工程根&gt; ^
    ///   -executeMethod Framework.Editor.BuildSize.BuildSizeCiGate.RunBuildSizeGate ^
    ///   -buildSizeDir &lt;产物目录&gt; [-buildSizeBaseline &lt;基线json&gt;] ^
    ///   [-buildSizeLabel v1.2.0] [-buildSizeWarnOnly] [-buildSizeUpdateBaseline] ^
    ///   -logFile Logs/ci/build-size-gate.log
    /// </code>
    /// 结论以 ASCII 哨兵 <c>[BuildSizeGate] GATE_RESULT exit=N</c> 为准（batchmode 退出码不可靠）。
    /// </summary>
    public static class BuildSizeCiGate
    {
        /// <summary>默认基线文件路径（相对工程根）。</summary>
        public const string DefaultBaselinePath = "Tools/ci/build-size-baseline.json";

        /// <summary>菜单：从选定目录更新包体基线。</summary>
        private const string UpdateBaselineMenu = "Framework/发布/更新包体基线";

        /// <summary>batchmode 入口：扫描产物 → 比对基线 → 裁决 → 哨兵 + 退出码收口。</summary>
        public static void RunBuildSizeGate()
        {
            try
            {
                Debug.Log("[BuildSizeGate] ===== 包体门禁开始 =====");

                string dir = GetArgValue("-buildSizeDir");
                if (string.IsNullOrEmpty(dir))
                {
                    Debug.LogError("[BuildSizeGate] 缺少必需参数 -buildSizeDir，无法执行包体门禁。");
                    Finish(1);
                    return;
                }

                string baselinePath = ResolveBaselinePath(GetArgValue("-buildSizeBaseline"));
                string label = GetArgValue("-buildSizeLabel") ?? string.Empty;
                bool warnOnly = HasArg("-buildSizeWarnOnly");
                bool updateBaseline = HasArg("-buildSizeUpdateBaseline");

                var current = BuildSizeSnapshotIO.FromDirectory(dir, label);
                if (current.totalBytes == 0 || current.entries == null || current.entries.Count == 0)
                {
                    Debug.LogError($"[BuildSizeGate] 产物目录不存在或为空：{dir}。");
                    Finish(1);
                    return;
                }

                var baseline = BuildSizeSnapshotIO.LoadBaseline(baselinePath);
                if (baseline == null && !updateBaseline)
                {
                    Debug.LogError($"[BuildSizeGate] 缺少包体基线：{baselinePath}。只有显式传入 -buildSizeUpdateBaseline 才允许创建基线。");
                    Finish(1);
                    return;
                }
                if (updateBaseline)
                {
                    BuildSizeSnapshotIO.SaveBaseline(baselinePath, current);
                    Debug.Log($"[BuildSizeGate] 已显式更新基线：{baselinePath}");
                    Finish(0);
                    return;
                }

                var policy = new BuildSizePolicy { warnOnly = warnOnly };
                var verdict = BuildSizeGate.Evaluate(baseline, current, policy);

                Debug.Log($"[BuildSizeGate] 产物总量 {BuildSizeGate.Human(current.totalBytes)}，条目 {current.entries.Count} 个");
                Debug.Log($"[BuildSizeGate] {verdict.Summary}");
                foreach (var v in verdict.Violations)
                    Debug.LogWarning($"[BuildSizeGate]   · {v.reason}");

                Finish(verdict.IsBlocking ? 1 : 0);
            }
            catch (Exception ex)
            {
                Debug.LogError($"[BuildSizeGate] 门禁执行异常: {ex.Message}\n{ex.StackTrace}");
                Finish(1);
            }
        }

        private static void Finish(int exitCode)
        {
            Debug.Log($"[BuildSizeGate] ===== 包体门禁结束（exit={exitCode}）=====");
            Debug.Log($"[BuildSizeGate] GATE_RESULT exit={exitCode}");
            EditorApplication.Exit(exitCode);
        }

        /// <summary>把可能为相对的基线路径解析到工程根下的绝对路径。</summary>
        private static string ResolveBaselinePath(string arg)
        {
            string p = string.IsNullOrEmpty(arg) ? DefaultBaselinePath : arg;
            return Path.IsPathRooted(p) ? p : Path.Combine(Directory.GetCurrentDirectory(), p);
        }

        /// <summary>菜单：选一个产物目录，扫描后写入默认基线路径。</summary>
        [MenuItem(UpdateBaselineMenu)]
        private static void UpdateBaselineFromFolder()
        {
            string dir = EditorUtility.OpenFolderPanel("选择构建产物目录（生成包体基线）", Directory.GetCurrentDirectory(), "");
            if (string.IsNullOrEmpty(dir))
                return;

            var snapshot = BuildSizeSnapshotIO.FromDirectory(dir, "manual");
            string baselinePath = Path.Combine(Directory.GetCurrentDirectory(), DefaultBaselinePath);
            BuildSizeSnapshotIO.SaveBaseline(baselinePath, snapshot);

            Debug.Log($"[BuildSizeGate] 已从 {dir} 生成基线（总量 {BuildSizeGate.Human(snapshot.totalBytes)}，" +
                      $"{snapshot.entries.Count} 条目）→ {baselinePath}");
            EditorUtility.DisplayDialog("包体基线", $"已更新基线\n总量：{BuildSizeGate.Human(snapshot.totalBytes)}", "好");
        }

        /// <summary>命令行是否含开关。</summary>
        private static bool HasArg(string key)
            => Environment.GetCommandLineArgs().Any(a => string.Equals(a, key, StringComparison.OrdinalIgnoreCase));

        /// <summary>取 <c>-key value</c> 形式的参数值；缺失返回 null。</summary>
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
    }
}
