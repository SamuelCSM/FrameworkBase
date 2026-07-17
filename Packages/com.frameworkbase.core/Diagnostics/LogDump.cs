using System;
using System.IO;
using Cysharp.Threading.Tasks;
using UnityEngine;

namespace Framework.Diagnostics
{
    /// <summary>
    /// 日志回捞编排：冲刷 → 打包 → （可选）上报。
    /// 上报通道由业务注入 <see cref="UploadHandler"/>（HTTP 端点 / Bugly 附件 / 反馈系统均可），
    /// 未注入时只打包留存本地——回捞包本身已具备「玩家反馈时导出」的价值，上报是增量。
    /// </summary>
    public static class LogDump
    {
        /// <summary>回捞包产物子目录（persistentDataPath 下，与日志目录分开）。</summary>
        public const string DumpFolderName = "LogDumps";

        /// <summary>产物保留个数上限（含本次），最旧先删。</summary>
        public static int MaxArchives = 3;

        /// <summary>
        /// 上报通道（业务注入）。参数为 zip 完整路径，返回是否上报成功。
        /// 实现失败 / 抛异常都不影响打包结果——包始终留存本地，下次可重试。
        /// </summary>
        public static Func<string, UniTask<bool>> UploadHandler { get; set; }

        /// <summary>
        /// 执行一次日志回捞。所有失败路径返回失败结果不抛出（与命令总线约定一致）。
        /// </summary>
        public static async UniTask<CommandResult> DumpAsync()
        {
            if (!GameLog.IsFileLogEnabled)
                return CommandResult.Fail("文件日志未启用（GameLog.EnableFileLog），无可回捞内容。");

            bool flushed = GameLog.FlushToDisk();
            string outputDirectory = Path.Combine(Application.persistentDataPath, DumpFolderName);
            LogArchiveResult archive = LogArchiver.CreateArchive(
                GameLog.LogDirectory, outputDirectory, MaxArchives);

            if (!archive.Success)
                return CommandResult.Fail($"日志打包失败:{archive.Error}");

            long kb = archive.ArchiveBytes / 1024;
            string summary = $"{archive.FileCount} 个日志文件,{kb}KB" + (flushed ? "" : ",尾部可能未刷全");
            // 回捞动作留面包屑:排障时「玩家何时导出过日志」本身就是线索。
            Core.Telemetry.CrashReporter.LeaveBreadcrumb($"logdump {summary}");

            if (UploadHandler == null)
                return CommandResult.Ok($"已打包({summary}):{archive.ArchivePath}\n未配置上报通道(LogDump.UploadHandler),包已留存本地。");

            try
            {
                bool uploaded = await UploadHandler(archive.ArchivePath);
                return uploaded
                    ? CommandResult.Ok($"已打包并上报({summary})。本地留存:{archive.ArchivePath}")
                    : CommandResult.Ok($"已打包({summary}),上报未成功,包已留存本地可重试:{archive.ArchivePath}");
            }
            catch (Exception ex)
            {
                return CommandResult.Ok(
                    $"已打包({summary}),上报通道异常({ex.GetType().Name}:{ex.Message}),包已留存本地可重试:{archive.ArchivePath}");
            }
        }
    }
}
