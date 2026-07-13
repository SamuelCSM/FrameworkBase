using System;
using System.IO;

namespace Framework
{
    /// <summary>
    /// 配置数据库事务化安装器（纯文件逻辑，可测试）。
    /// <para>
    /// 事务边界：安装 = 写临时文件 → 校验 → 备份旧库（.bak）→ 覆盖正式库。
    /// 与旧实现的关键区别：<b>安装成功后不再立即删除 .bak</b>——备份保留到统一启动确认点
    /// （<see cref="ConfirmInstalled"/>），启动确认前任何失败（Hotfix 启动失败、进程被杀）都能通过
    /// <see cref="RestoreLastConfirmed"/> 恢复上一份已确认数据库，避免"代码槽回滚而配置停留新版本"。
    /// </para>
    /// <para>
    /// 所有权边界：本类只负责 db / .tmp / .bak 三个文件的状态迁移；SQLite 校验经构造注入
    /// （生产传 SQLite 打开校验，测试传内容标记校验），配置缓存重载由调用方（ConfigManager）负责。
    /// </para>
    /// <para>线程边界：非线程安全，调用方保证在启动流程单线程串行使用。</para>
    /// </summary>
    public sealed class ConfigDatabaseInstaller
    {
        private readonly string _dbPath;
        private readonly Func<string, bool> _validateDatabaseFile;
        private readonly Action<string> _log;
        private readonly Action<string> _logError;

        /// <summary>正式库旁的备份文件路径（{db}.bak）。</summary>
        public string BackupPath => _dbPath + ".bak";

        /// <summary>安装用临时文件路径（{db}.tmp）。</summary>
        public string TempPath => _dbPath + ".tmp";

        /// <summary>当前是否存在未确认的备份（存在即说明上次安装尚未走到启动确认点）。</summary>
        public bool HasUnconfirmedBackup => File.Exists(BackupPath);

        /// <param name="dbPath">正式配置数据库绝对路径。</param>
        /// <param name="validateDatabaseFile">数据库文件校验函数（打开并读取表结构）；null 视为恒真（仅测试）。</param>
        /// <param name="log">普通日志回调。</param>
        /// <param name="logError">错误日志回调。</param>
        public ConfigDatabaseInstaller(
            string dbPath,
            Func<string, bool> validateDatabaseFile,
            Action<string> log = null,
            Action<string> logError = null)
        {
            if (string.IsNullOrEmpty(dbPath))
                throw new ArgumentException("配置数据库路径不能为空。", nameof(dbPath));
            _dbPath = dbPath;
            _validateDatabaseFile = validateDatabaseFile ?? (_ => true);
            _log = log ?? (_ => { });
            _logError = logError ?? (_ => { });
        }

        /// <summary>
        /// 以事务方式安装数据库字节：临时文件 → 校验 → 备份旧库 → 覆盖。
        /// <para>
        /// 失败恢复路径：覆盖失败时立即用 .bak 回滚正式库；校验失败时正式库未被触碰。
        /// 成功后 .bak 保留，等待 <see cref="ConfirmInstalled"/> 或 <see cref="RestoreLastConfirmed"/> 处置。
        /// </para>
        /// </summary>
        /// <param name="bytes">新数据库完整字节。</param>
        /// <param name="sourceName">来源描述（日志/诊断用）。</param>
        public ConfigInstallResult Install(byte[] bytes, string sourceName)
        {
            if (bytes == null || bytes.Length == 0)
            {
                return ConfigInstallResult.Failed(
                    ConfigInstallStatus.ValidationFailed, $"数据库载荷为空：{sourceName}");
            }

            try
            {
                string directory = Path.GetDirectoryName(_dbPath);
                if (!string.IsNullOrEmpty(directory))
                    Directory.CreateDirectory(directory);
            }
            catch (Exception ex)
            {
                return ConfigInstallResult.Failed(
                    ConfigInstallStatus.ReplaceFailed, $"创建数据库目录失败：{ex.Message}");
            }

            // ── 阶段 1：写临时文件并校验（正式库尚未被触碰，失败无需恢复）────────
            try
            {
                File.WriteAllBytes(TempPath, bytes);
            }
            catch (Exception ex)
            {
                DeleteQuietly(TempPath);
                return ConfigInstallResult.Failed(
                    ConfigInstallStatus.ReplaceFailed, $"写入临时数据库失败：{ex.Message}");
            }

            bool valid;
            try
            {
                valid = _validateDatabaseFile(TempPath);
            }
            catch (Exception ex)
            {
                DeleteQuietly(TempPath);
                return ConfigInstallResult.Failed(
                    ConfigInstallStatus.ValidationFailed, $"校验临时数据库异常：{ex.Message}");
            }

            if (!valid)
            {
                DeleteQuietly(TempPath);
                return ConfigInstallResult.Failed(
                    ConfigInstallStatus.ValidationFailed, $"数据库校验失败：{sourceName}");
            }

            // ── 阶段 2：备份旧库 → 覆盖正式库（失败立即回滚）───────────────────
            try
            {
                if (File.Exists(_dbPath))
                {
                    // 备份必须在覆盖前完成；备份失败视为替换失败，正式库保持原样。
                    File.Copy(_dbPath, BackupPath, overwrite: true);
                }

                File.Copy(TempPath, _dbPath, overwrite: true);
                DeleteQuietly(TempPath);

                // 关键设计点：这里不删除 .bak。备份的生命周期延伸到统一启动确认点，
                // 使"配置安装成功但后续 Hotfix 启动失败"的场景能够恢复上一份已确认库。
                _log($"[ConfigManager] 数据库已安装（备份保留至启动确认）：{sourceName}");
                return ConfigInstallResult.Installed();
            }
            catch (Exception ex)
            {
                _logError($"[ConfigManager] 数据库替换失败：{ex.Message}");
                TryRestoreBackupToDb();
                DeleteQuietly(TempPath);
                return ConfigInstallResult.Failed(
                    ConfigInstallStatus.ReplaceFailed, $"数据库替换失败：{ex.Message}");
            }
        }

        /// <summary>
        /// 统一启动确认点动作：本次安装已随整个内容发行确认成功，删除旧库备份。
        /// 只应由启动确认流程调用；确认前禁止删除备份。
        /// </summary>
        public void ConfirmInstalled()
        {
            DeleteQuietly(BackupPath);
            _log("[ConfigManager] 启动确认完成，配置数据库备份已清理。");
        }

        /// <summary>
        /// 启动确认前失败的恢复动作：把上一份已确认数据库（.bak）恢复为正式库。
        /// 无备份时不做任何事（说明本次启动没有安装过新配置）。
        /// </summary>
        /// <returns>实际发生了恢复返回 true；无备份返回 false。</returns>
        public bool RestoreLastConfirmed()
        {
            if (!File.Exists(BackupPath))
                return false;

            try
            {
                File.Copy(BackupPath, _dbPath, overwrite: true);
                DeleteQuietly(BackupPath);
                _log("[ConfigManager] 已恢复上一份已确认配置数据库。");
                return true;
            }
            catch (Exception ex)
            {
                // 恢复失败必须显式暴露：留着 .bak 供下次启动重试，绝不静默删除。
                _logError($"[ConfigManager] 恢复配置数据库备份失败（备份保留待重试）：{ex.Message}");
                throw;
            }
        }

        /// <summary>覆盖失败时的即时回滚：尽力把 .bak 拷回正式库，保留 .bak 供下次启动兜底。</summary>
        private void TryRestoreBackupToDb()
        {
            try
            {
                if (File.Exists(BackupPath))
                {
                    File.Copy(BackupPath, _dbPath, overwrite: true);
                    _logError("[ConfigManager] 替换失败后已回滚到备份数据库。");
                }
            }
            catch (Exception ex)
            {
                _logError($"[ConfigManager] 替换失败后的即时回滚也失败（备份保留待下次启动恢复）：{ex.Message}");
            }
        }

        private void DeleteQuietly(string path)
        {
            try
            {
                if (File.Exists(path))
                    File.Delete(path);
            }
            catch (Exception ex)
            {
                _logError($"[ConfigManager] 删除文件失败 {path}：{ex.Message}");
            }
        }
    }
}
