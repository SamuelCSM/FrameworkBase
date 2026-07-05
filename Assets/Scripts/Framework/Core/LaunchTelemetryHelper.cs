using System;
using System.Collections.Generic;
using System.IO;
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

        /// <summary>写入启动最终结果并持久化到 persistentDataPath。</summary>
        public static void FinalizeRunMetric(LaunchRunMetric run, bool success, string endReason)
        {
            run.Success = success;
            run.EndReason = endReason;

            string json = JsonUtility.ToJson(run, true);
            Debug.Log($"[LaunchTelemetry] {json}");

            try
            {
                string outputPath = Path.Combine(Application.persistentDataPath, "launch_metrics_last.json");
                File.WriteAllText(outputPath, json);
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[LaunchTelemetry] 写入 launch_metrics_last.json 失败: {ex.Message}");
            }
        }
    }
}
