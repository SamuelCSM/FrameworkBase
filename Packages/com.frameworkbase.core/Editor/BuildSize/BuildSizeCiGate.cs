using System;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.Build;
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
    ///   [-buildSizeBudgetMB 50] ^
    ///   -logFile Logs/ci/build-size-gate.log
    /// </code>
    /// <para>
    /// <c>-buildSizeBudgetMB N</c> 启用<b>预算模式</b>：总量 ≤ N MiB 即放行，无需基线、也不必逐版滚基线；
    /// 超预算才阻断。适合「XX MB 内都算正常」的诉求。不传则走「相对上次基线涨幅」的默认模式。
    /// </para>
    /// 结论以 ASCII 哨兵 <c>[BuildSizeGate] GATE_RESULT exit=N</c> 为准（batchmode 退出码不可靠）。
    /// </summary>
    public static class BuildSizeCiGate
    {
        /// <summary>默认基线文件路径（相对工程根）。</summary>
        public const string DefaultBaselinePath = "Tools/ci/build-size-baseline.json";

        /// <summary>菜单：从选定目录更新包体基线。</summary>
        private const string UpdateBaselineMenu = "Framework/发布/更新包体基线";

        /// <summary>独立 batchmode 入口：裁决后以哨兵 + 退出码收口，供本地脚本按哨兵行判定。</summary>
        public static void RunBuildSizeGate()
        {
            int exitCode;
            try
            {
                exitCode = EvaluateGate();
            }
            catch (Exception ex)
            {
                Debug.LogError($"[BuildSizeGate] 门禁执行异常: {ex.Message}\n{ex.StackTrace}");
                exitCode = 1;
            }
            Finish(exitCode);
        }

        /// <summary>
        /// 外部构建器（GameCI unity-builder 等）入口：失败以 <see cref="BuildFailedException"/> 上抛，
        /// 绕开 batchmode 进程退出码不可靠问题；本方法不调用 EditorApplication.Exit。
        /// </summary>
        public static void RunBuildSizeGateForBuilder()
        {
            int exitCode = EvaluateGate();
            Debug.Log($"[BuildSizeGate] GATE_RESULT exit={exitCode}");
            if (exitCode != 0)
                throw new BuildFailedException("包体门禁未通过，请查看上方 [BuildSizeGate] 逐项报告。");
        }

        /// <summary>执行扫描、基线比对与裁决，返回 0/1；不负责进程收口。</summary>
        private static int EvaluateGate()
        {
            Debug.Log("[BuildSizeGate] ===== 包体门禁开始 =====");

            string dir = GetArgValue("-buildSizeDir");
            if (string.IsNullOrEmpty(dir))
            {
                Debug.LogError("[BuildSizeGate] 缺少必需参数 -buildSizeDir，无法执行包体门禁。");
                return 1;
            }

            string baselinePath = ResolveBaselinePath(GetArgValue("-buildSizeBaseline"));
            string label = GetArgValue("-buildSizeLabel") ?? string.Empty;
            bool warnOnly = HasArg("-buildSizeWarnOnly");
            bool updateBaseline = HasArg("-buildSizeUpdateBaseline");
            long budgetBytes = ParseBudgetBytes(GetArgValue("-buildSizeBudgetMB"));

            var current = BuildSizeSnapshotIO.FromDirectory(dir, label);
            if (current.totalBytes == 0 || current.entries == null || current.entries.Count == 0)
            {
                Debug.LogError($"[BuildSizeGate] 产物目录不存在或为空：{dir}。");
                return 1;
            }

            var baseline = BuildSizeSnapshotIO.LoadBaseline(baselinePath);
            // 预算模式只看绝对上限，无需基线；相对涨幅模式才要求基线存在。
            if (baseline == null && !updateBaseline && budgetBytes <= 0)
            {
                Debug.LogError($"[BuildSizeGate] 缺少包体基线：{baselinePath}。只有显式传入 -buildSizeUpdateBaseline 才允许创建基线。");
                return 1;
            }
            if (updateBaseline)
            {
                BuildSizeSnapshotIO.SaveBaseline(baselinePath, current);
                Debug.Log($"[BuildSizeGate] 已显式更新基线：{baselinePath}");
                return 0;
            }

            Debug.Log($"[BuildSizeGate] 产物总量 {BuildSizeGate.Human(current.totalBytes)}，条目 {current.entries.Count} 个");

            // ── 预算模式：两档 —— 硬上限阻断 + 相对基线涨幅仅告警（大厂形：loose block + informative regression）──
            if (budgetBytes > 0)
                return EvaluateBudgetTwoTier(baseline, current, budgetBytes, warnOnly);

            // ── 非预算模式：原「相对上次基线涨幅」阻断逻辑 ──────────────────────────────
            var verdict = BuildSizeGate.Evaluate(baseline, current, new BuildSizePolicy { warnOnly = warnOnly });
            Debug.Log($"[BuildSizeGate] {verdict.Summary}");
            foreach (var v in verdict.Violations)
                Debug.LogWarning($"[BuildSizeGate]   · {v.reason}");
            return verdict.IsBlocking ? 1 : 0;
        }

        /// <summary>
        /// 预算模式两档裁决：<b>硬上限</b>（超预算才阻断）是唯一决定退出码的闸；<b>相对基线涨幅</b>
        /// 只作告警信号（保留回归可见性与归因，但不拦开发速度）。基线缺失则跳过告警档。
        /// </summary>
        private static int EvaluateBudgetTwoTier(
            BuildSizeSnapshot baseline, BuildSizeSnapshot current, long budgetBytes, bool warnOnly)
        {
            Debug.Log($"[BuildSizeGate] 预算模式：硬上限 {BuildSizeGate.Human(budgetBytes)}（超出才阻断）+ 相对基线涨幅仅告警。");

            // 档一（阻断）：绝对预算
            var budgetVerdict = BuildSizeGate.Evaluate(
                baseline, current, new BuildSizePolicy { warnOnly = warnOnly, totalBudgetBytes = budgetBytes });
            Debug.Log($"[BuildSizeGate] {budgetVerdict.Summary}");
            foreach (var v in budgetVerdict.Violations)
                Debug.LogError($"[BuildSizeGate]   · 超硬上限：{v.reason}");

            // 档二（仅告警）：相对基线涨幅——给出回归信号但不影响退出码
            if (baseline != null)
            {
                var relVerdict = BuildSizeGate.Evaluate(baseline, current, new BuildSizePolicy { warnOnly = true });
                if (relVerdict.Violations.Count > 0)
                {
                    Debug.LogWarning($"[BuildSizeGate] 相对基线涨幅（仅告警，不阻断）：{relVerdict.Summary}");
                    foreach (var v in relVerdict.Violations)
                        Debug.LogWarning($"[BuildSizeGate]   ⚠ {v.reason}");
                    Debug.LogWarning("[BuildSizeGate] 如告警持续，可用「Framework/发布/更新包体基线」滚基线重置涨幅参照（不影响放行）。");
                }
                else
                {
                    Debug.Log("[BuildSizeGate] 相对基线：无超阈项。");
                }
            }

            return budgetVerdict.IsBlocking ? 1 : 0;
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

        /// <summary>解析 <c>-buildSizeBudgetMB</c>（MiB，支持小数）为字节；缺失 / 非法 / 非正返回 0（关闭预算模式）。</summary>
        private static long ParseBudgetBytes(string mbArg)
        {
            if (string.IsNullOrEmpty(mbArg) ||
                !double.TryParse(mbArg, System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out double mb) ||
                mb <= 0)
            {
                return 0;
            }
            return (long)(mb * 1024 * 1024);
        }

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
