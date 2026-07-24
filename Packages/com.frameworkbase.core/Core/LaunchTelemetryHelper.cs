using System;
using System.Collections.Generic;
using System.IO;
using Framework.Serialization;
using Framework.Storage;
using UnityEngine;

namespace Framework.Core
{
    /// <summary>
    /// LaunchFlow 埋点辅助：阶段计时、结果收口、落盘。
    /// </summary>
    public static class LaunchTelemetryHelper
    {
        [Serializable]
        public class LaunchPhaseMetric
        {
            public string Phase;
            public string DisplayName;
            public bool Success;
            public long DurationMs;
            public string Detail;
            public string Error;
            public long StartTicksUtc;
        }

        [Serializable]
        public class LaunchRunMetric
        {
            public string RunId;
            public string StartedAtUtc;
            public bool Success;
            public string EndReason;
            public List<LaunchPhaseMetric> Phases = new List<LaunchPhaseMetric>();
        }

        /// <summary>创建一次启动运行埋点。</summary>
        public static LaunchRunMetric BeginRunMetric()
        {
            return new LaunchRunMetric
            {
                RunId = Guid.NewGuid().ToString("N"),
                StartedAtUtc = DateTime.UtcNow.ToString("O"),
                Success = false,
                EndReason = "running",
            };
        }

        /// <summary>开始记录单个阶段埋点。</summary>
        public static LaunchPhaseMetric BeginPhaseMetric(LaunchRunMetric run, Enum phase, string displayName)
        {
            var metric = new LaunchPhaseMetric
            {
                Phase = phase.ToString(),
                DisplayName = displayName,
                Success = false,
                DurationMs = 0,
                Detail = string.Empty,
                Error = string.Empty,
                StartTicksUtc = DateTime.UtcNow.Ticks
            };
            run.Phases.Add(metric);

            // 留一条启动阶段面包屑：崩溃报告即可定位「卡在启动哪一步」——启动崩溃最有价值的上下文。
            Telemetry.CrashReporter.LeaveBreadcrumb($"launch:{displayName}");
            return metric;
        }

        /// <summary>结束阶段埋点并计算耗时。</summary>
        public static void EndPhaseMetric(LaunchPhaseMetric metric, bool success, string detail = "")
        {
            if (metric == null) return;
            metric.DurationMs = metric.StartTicksUtc > 0
                ? (long)TimeSpan.FromTicks(DateTime.UtcNow.Ticks - metric.StartTicksUtc).TotalMilliseconds
                : 0;
            metric.Success = success;
            metric.Detail = detail;
        }

        /// <summary>写入启动最终结果：本地落盘（离线可查）+ 埋点管道上报（线上漏斗分析）。</summary>
        public static void FinalizeRunMetric(LaunchRunMetric run, bool success, string endReason)
        {
            run.Success = success;
            run.EndReason = endReason;

            Telemetry.CrashReporter.LeaveBreadcrumb($"launch:end success={success} reason={endReason}");

            string json = JsonSerializers.Shared.ToJson(run, true);
            GameLog.Log($"[LaunchTelemetry] {json}");

            try
            {
                string outputPath = Path.Combine(Application.persistentDataPath, "launch_metrics_last.json");
                FileStorages.Shared.WriteText(outputPath, json);
            }
            catch (Exception ex)
            {
                GameLog.Warning($"[LaunchTelemetry] 写入 launch_metrics_last.json 失败: {ex.Message}");
            }

            TrackRunMetric(run);
        }

        /// <summary>
        /// 启动指标接入埋点管道：一条 launch_run 汇总事件 + 每阶段一条 launch_phase。
        /// 埋点管理器未就绪（纯单测环境）时静默跳过。
        /// </summary>
        private static void TrackRunMetric(LaunchRunMetric run)
        {
            var analytics = GameEntry.Analytics;
            if (analytics == null)
                return;

            long totalMs = 0;
            foreach (LaunchPhaseMetric phase in run.Phases)
                totalMs += phase.DurationMs;

            analytics.Track("launch_run", new Dictionary<string, object>
            {
                { "run_id", run.RunId },
                { "success", run.Success },
                { "end_reason", run.EndReason },
                { "total_ms", totalMs },
                { "phase_count", run.Phases.Count }
            });

            foreach (LaunchPhaseMetric phase in run.Phases)
            {
                analytics.Track("launch_phase", new Dictionary<string, object>
                {
                    { "run_id", run.RunId },
                    { "phase", phase.Phase },
                    { "success", phase.Success },
                    { "duration_ms", phase.DurationMs },
                    { "detail", phase.Detail ?? string.Empty }
                });
            }
        }
    }
}
