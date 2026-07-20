using System;
using System.IO;
using System.IO.Compression;

namespace Framework
{
    /// <summary>一次日志打包的结果。</summary>
    public readonly struct LogArchiveResult
    {
        private LogArchiveResult(bool success, string archivePath, int fileCount, long archiveBytes, string error)
        {
            Success = success;
            ArchivePath = archivePath;
            FileCount = fileCount;
            ArchiveBytes = archiveBytes;
            Error = error;
        }

        /// <summary>是否打包成功。</summary>
        public bool Success { get; }

        /// <summary>zip 产物完整路径（失败时为空串）。</summary>
        public string ArchivePath { get; }

        /// <summary>打进包的日志文件个数。</summary>
        public int FileCount { get; }

        /// <summary>zip 产物字节数。</summary>
        public long ArchiveBytes { get; }

        /// <summary>失败原因（成功时为空串）。</summary>
        public string Error { get; }

        internal static LogArchiveResult Ok(string path, int fileCount, long bytes)
            => new LogArchiveResult(true, path, fileCount, bytes, string.Empty);

        internal static LogArchiveResult Fail(string error)
            => new LogArchiveResult(false, string.Empty, 0, 0, error ?? string.Empty);
    }

    /// <summary>
    /// 日志打包器：把日志目录压缩成单个 zip 落到独立产物目录，供回捞上报 / 玩家反馈附带。
    /// 纯文件系统逻辑、零 Unity 依赖，EditMode 直接可测。
    /// <para>
    /// 产物目录自带保留上限（最旧先删），长期运营的真机设备不会被回捞包撑爆存储——
    /// 与 GameLog 日志文件轮转同款思路。产物目录应与日志目录分开，避免 zip 被下次打包收进去。
    /// </para>
    /// </summary>
    public static class LogArchiver
    {
        /// <summary>产物文件名前缀（保留清理按此前缀匹配，不误删目录里的其它文件）。</summary>
        public const string ArchivePrefix = "logdump_";

        /// <summary>
        /// 打包日志目录。日志文件可能正被写线程持有（FileShare 允许读），以共享读方式拷入 zip。
        /// </summary>
        /// <param name="logDirectory">日志目录（GameLog.LogDirectory）。</param>
        /// <param name="outputDirectory">zip 产物目录，不存在会创建；不得与日志目录相同。</param>
        /// <param name="maxArchives">产物保留个数上限（含本次），最旧先删。</param>
        /// <param name="searchPattern">参与打包的文件通配（默认 GameLog 的轮转文件名模式）。</param>
        public static LogArchiveResult CreateArchive(
            string logDirectory,
            string outputDirectory,
            int maxArchives = 3,
            string searchPattern = "Log_*.txt")
        {
            if (string.IsNullOrEmpty(logDirectory) || !Directory.Exists(logDirectory))
                return LogArchiveResult.Fail("日志目录不存在。");
            if (string.IsNullOrEmpty(outputDirectory))
                return LogArchiveResult.Fail("产物目录未指定。");
            if (maxArchives < 1)
                return LogArchiveResult.Fail("产物保留个数至少为 1。");
            if (string.Equals(
                    Path.GetFullPath(logDirectory).TrimEnd(Path.DirectorySeparatorChar),
                    Path.GetFullPath(outputDirectory).TrimEnd(Path.DirectorySeparatorChar),
                    StringComparison.OrdinalIgnoreCase))
                return LogArchiveResult.Fail("产物目录不得与日志目录相同（zip 会被下次打包收进去）。");

            try
            {
                string[] files = Directory.GetFiles(logDirectory, searchPattern);
                if (files.Length == 0)
                    return LogArchiveResult.Fail("日志目录内没有匹配的日志文件。");
                Array.Sort(files, StringComparer.Ordinal); // 时间戳文件名字典序即时间序，包内条目稳定

                Directory.CreateDirectory(outputDirectory);
                string archivePath = Path.Combine(
                    outputDirectory, $"{ArchivePrefix}{DateTime.Now:yyyyMMdd_HHmmss_fff}.zip"); // banned-api-allow: local-time 归档文件名按本地时间

                using (var zipStream = new FileStream(archivePath, FileMode.CreateNew))
                using (var zip = new ZipArchive(zipStream, ZipArchiveMode.Create))
                {
                    foreach (string file in files)
                    {
                        ZipArchiveEntry entry = zip.CreateEntry(Path.GetFileName(file), CompressionLevel.Optimal);
                        // 共享读打开：当前日志文件正被写线程持有写句柄
                        using (var source = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                        using (Stream target = entry.Open())
                        {
                            source.CopyTo(target);
                        }
                    }
                }

                CleanupOldArchives(outputDirectory, maxArchives);
                long bytes = new FileInfo(archivePath).Length;
                return LogArchiveResult.Ok(archivePath, files.Length, bytes);
            }
            catch (Exception ex)
            {
                return LogArchiveResult.Fail($"{ex.GetType().Name}: {ex.Message}");
            }
        }

        /// <summary>删除超出保留个数的旧产物（时间戳文件名按字典序即时间序）。</summary>
        private static void CleanupOldArchives(string outputDirectory, int maxArchives)
        {
            string[] archives = Directory.GetFiles(outputDirectory, ArchivePrefix + "*.zip");
            if (archives.Length <= maxArchives)
                return;

            Array.Sort(archives, StringComparer.Ordinal); // 旧在前
            int deleteCount = archives.Length - maxArchives;
            for (int i = 0; i < deleteCount; i++)
            {
                try { File.Delete(archives[i]); }
                catch { /* 单个删除失败不影响其余，下次打包再清 */ }
            }
        }
    }
}
