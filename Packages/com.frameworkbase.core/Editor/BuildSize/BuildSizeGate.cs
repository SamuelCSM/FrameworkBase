using System.Collections.Generic;
using System.Text;

namespace Framework.Editor.BuildSize
{
    /// <summary>
    /// 包体尺寸回归门禁核心（纯逻辑，零 Unity 依赖，可直接单测）。
    /// 比对"当前构建"与"基线"两份快照，按策略给出 Pass/Warn/Fail 裁决，
    /// 拦截"包体一版版胖"这类只在运营中后期才痛、且难回溯的隐患。
    /// </summary>
    /// <remarks>
    /// 只查<b>增长</b>（缩小无害）。两道阈值：总量（百分比+可选绝对字节）与单类（百分比，带最小体积门槛防抖）。
    /// 无基线视为首次，直接 Pass（由调用方决定是否落盘为新基线）。
    /// </remarks>
    public static class BuildSizeGate
    {
        /// <summary>
        /// 评估当前构建相对基线的尺寸变化。
        /// </summary>
        /// <param name="baseline">基线快照；为 null 表示首次（返回 Pass）。</param>
        /// <param name="current">当前构建快照。</param>
        /// <param name="policy">阈值策略；为 null 用默认策略。</param>
        /// <returns>裁决结果。</returns>
        public static BuildSizeVerdict Evaluate(BuildSizeSnapshot baseline, BuildSizeSnapshot current, BuildSizePolicy policy)
        {
            policy = policy ?? new BuildSizePolicy();

            if (current == null)
                return new BuildSizeVerdict(BuildSizeStatus.Pass, null, "无当前构建数据，跳过。");

            if (baseline == null)
                return new BuildSizeVerdict(BuildSizeStatus.Pass, null,
                    $"首次运行，无基线可比（当前总量 {Human(current.totalBytes)}），建议落盘为基线。");

            var violations = new List<BuildSizeViolation>();

            // ── 总量 ─────────────────────────────────────────────────────────
            long totalDelta = current.totalBytes - baseline.totalBytes;
            if (totalDelta > 0)
            {
                double pct = Percent(totalDelta, baseline.totalBytes);
                bool overPct = policy.maxTotalGrowthPercent > 0 && pct > policy.maxTotalGrowthPercent;
                bool overAbs = policy.maxTotalGrowthBytes > 0 && totalDelta > policy.maxTotalGrowthBytes;
                if (overPct || overAbs)
                {
                    violations.Add(new BuildSizeViolation
                    {
                        category = "TOTAL",
                        baselineBytes = baseline.totalBytes,
                        currentBytes = current.totalBytes,
                        deltaBytes = totalDelta,
                        reason = $"总量增长 {Human(totalDelta)}（+{pct:F1}%），超阈值。",
                    });
                }
            }

            // ── 单类 ─────────────────────────────────────────────────────────
            var baselineMap = new Dictionary<string, long>();
            foreach (var e in baseline.entries)
            {
                if (e != null && e.name != null)
                    baselineMap[e.name] = e.bytes;
            }

            foreach (var cur in current.entries)
            {
                if (cur == null || cur.name == null)
                    continue;

                if (baselineMap.TryGetValue(cur.name, out long baseBytes))
                {
                    long delta = cur.bytes - baseBytes;
                    if (delta <= 0 || cur.bytes < policy.entryMinBytesToCheck)
                        continue;

                    double pct = Percent(delta, baseBytes);
                    if (policy.maxEntryGrowthPercent > 0 && pct > policy.maxEntryGrowthPercent)
                    {
                        violations.Add(new BuildSizeViolation
                        {
                            category = cur.name,
                            baselineBytes = baseBytes,
                            currentBytes = cur.bytes,
                            deltaBytes = delta,
                            reason = $"'{cur.name}' 增长 {Human(delta)}（+{pct:F1}%），超单类阈值。",
                        });
                    }
                }
                else if (policy.failOnNewEntry)
                {
                    violations.Add(new BuildSizeViolation
                    {
                        category = "NEW:" + cur.name,
                        baselineBytes = 0,
                        currentBytes = cur.bytes,
                        deltaBytes = cur.bytes,
                        reason = $"新增条目 '{cur.name}'（{Human(cur.bytes)}）。",
                    });
                }
            }

            if (violations.Count == 0)
            {
                string sign = totalDelta >= 0 ? "+" : "-";
                return new BuildSizeVerdict(BuildSizeStatus.Pass, null,
                    $"通过：总量 {Human(current.totalBytes)}（{sign}{Human(System.Math.Abs(totalDelta))}），无超阈项。");
            }

            var status = policy.warnOnly ? BuildSizeStatus.Warn : BuildSizeStatus.Fail;
            return new BuildSizeVerdict(status, violations, BuildSummary(status, violations));
        }

        /// <summary>增量占基线的百分比（基线为 0 时按 100% 处理）。</summary>
        private static double Percent(long delta, long baseBytes)
            => baseBytes > 0 ? delta * 100.0 / baseBytes : 100.0;

        private static string BuildSummary(BuildSizeStatus status, List<BuildSizeViolation> violations)
        {
            var sb = new StringBuilder();
            sb.Append(status == BuildSizeStatus.Fail ? "阻断" : "告警");
            sb.Append($"：{violations.Count} 项超阈：");
            for (int i = 0; i < violations.Count; i++)
            {
                if (i > 0) sb.Append("；");
                sb.Append(violations[i].reason);
            }
            return sb.ToString();
        }

        /// <summary>字节数转人类可读（B/KB/MB/GB）。</summary>
        public static string Human(long bytes)
        {
            string[] units = { "B", "KB", "MB", "GB", "TB" };
            double v = bytes;
            int u = 0;
            while (v >= 1024 && u < units.Length - 1)
            {
                v /= 1024;
                u++;
            }
            return u == 0 ? $"{bytes} B" : $"{v:F2} {units[u]}";
        }
    }
}
