using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace Framework.Core.Privacy
{
    /// <summary>
    /// RTBF（被遗忘权）本地数据抹除编排：把散落各模块的用户数据删除收敛成一次调用，
    /// 应对 GDPR/CCPA/个保法的"删除我的数据"诉求。
    ///
    /// 覆盖面（全部**本地**数据）：埋点队列与落盘快照、远程配置缓存、全部账号加密存档、
    /// PlayerPrefs（含语言/同意状态——抹除后按未同意处理，语义正确）、安全存储（登录令牌等机密）、
    /// 崩溃记录、启动指标快照、文件日志目录。逐项异常隔离并返回执行报告。
    ///
    /// 边界：只清**设备本地**。服务端侧数据删除（账号注销、埋点采集端按 device_id/user_id
    /// 清除）走业务后台流程，本编排管不到也不应假装管到——文档必须向审核方如实陈述两段流程。
    /// </summary>
    public static class PrivacyCompliance
    {
        /// <summary>单项抹除结果。</summary>
        public struct EraseEntry
        {
            public string Item;
            public bool Success;
            public string Error;

            public override string ToString() => Success ? $"✓ {Item}" : $"✗ {Item}: {Error}";
        }

        /// <summary>
        /// 抹除全部本地用户数据。调用前建议先 <c>GameEntry.Analytics.CollectionEnabled = false</c>
        /// 并断开网络会话；调用后应引导重启（各管理器内存态不保证全部回滚）。
        /// </summary>
        /// <returns>逐项执行报告（供 UI 展示或日志留痕）。</returns>
        public static List<EraseEntry> EraseAllLocalUserData()
        {
            var report = new List<EraseEntry>();

            // 1. 埋点：内存队列 + analytics_pending.jsonl 落盘快照
            Run(report, "埋点队列与落盘快照", () =>
            {
                var analytics = GameEntry.Analytics;
                if (analytics != null)
                    analytics.ClearQueue();
                else
                    DeleteFile("analytics_pending.jsonl"); // 未接线时直接删文件兜底
            });

            // 2. 远程配置缓存（含按设备定向的历史内容）
            Run(report, "远程配置缓存", () =>
            {
                var remoteConfig = GameEntry.RemoteConfig;
                if (remoteConfig != null)
                    remoteConfig.ClearCache();
                else
                    DeleteFile("remote_config_cache.json");
            });

            // 3. 全部账号的加密存档 + PlayerPrefs（语言/同意状态一并清空，抹除后按未同意处理）
            Run(report, "加密存档（全部账号）", () => Save.SaveManager.Instance.DeleteAllSaves());
            Run(report, "PlayerPrefs", () => Save.SaveManager.Instance.DeleteAllPrefs());

            // 4. 安全存储（登录令牌等机密）：经抽象 DeleteAll 抹除，覆盖硬件级 Keychain/Keystore 后端；
            //    否则残留会话令牌可被下次冷启动静默恢复，等同「没删干净」。默认 PlayerPrefs 后端虽已被上
            //    一步波及，但仍显式走抽象，保证注入自定义后端时同样彻底。
            Run(report, "安全存储（凭证/令牌）", () => Security.SecureStorage.Shared.DeleteAll());

            // 5. 遥测残留：崩溃记录、启动指标快照
            Run(report, "崩溃记录", () => DeleteFile("crash_reports.jsonl"));
            Run(report, "启动指标快照", () => DeleteFile("launch_metrics_last.json"));

            // 6. 文件日志（可能含 userId 等标识）：先停写再删目录
            Run(report, "文件日志", () =>
            {
                GameLog.Shutdown();
                string logDir = Path.Combine(Application.persistentDataPath, "Logs");
                if (Directory.Exists(logDir))
                    Directory.Delete(logDir, recursive: true);
            });

            foreach (EraseEntry entry in report)
            {
                if (entry.Success)
                    GameLog.Log($"[PrivacyCompliance] {entry}");
                else
                    GameLog.Error($"[PrivacyCompliance] {entry}");
            }
            return report;
        }

        private static void Run(List<EraseEntry> report, string item, Action action)
        {
            try
            {
                action();
                report.Add(new EraseEntry { Item = item, Success = true });
            }
            catch (Exception ex)
            {
                // 单项失败不阻断其余项：能删多少删多少，失败项如实进报告
                report.Add(new EraseEntry { Item = item, Success = false, Error = ex.Message });
            }
        }

        private static void DeleteFile(string persistentRelativePath)
        {
            string path = Path.Combine(Application.persistentDataPath, persistentRelativePath);
            if (File.Exists(path))
                File.Delete(path);
        }
    }
}
