using System;
using System.IO;
using Framework.Serialization;
using Framework.Storage;

namespace Framework.HotUpdate
{
    /// <summary>
    /// Addressables Catalog 缓存快照管理器（纯文件逻辑，可测试）。
    /// <para>
    /// 背景：Addressables.UpdateCatalogs 会立即覆盖 persistentDataPath 下的 Catalog 缓存
    /// （com.unity.addressables/catalog_*.json/.hash），下一次进程启动直接加载最新缓存——
    /// 这就是"旧代码槽 + 新 Catalog"错配的来源。资源按哈希寻址只保证 bundle 内容不可变，
    /// 不能保证 Catalog 与代码/配置兼容，因此 Catalog 必须有真实可执行的回滚方案。
    /// </para>
    /// <para>
    /// 方案（内容事务第一阶段）：UpdateCatalogs 执行前对缓存目录做完整快照；
    /// 启动确认成功后丢弃快照（当前缓存成为新 LKG）；确认前失败则在下一次进程启动、
    /// Addressables 初始化之前用快照覆写缓存目录，使旧 Catalog 重新生效，与回滚后的
    /// 旧代码槽、恢复后的旧配置保持一致。恢复发生在 Addressables 感知之外，无兼容风险。
    /// </para>
    /// <para>失败恢复路径：恢复失败时保留快照并返回 false，下一次启动可重试；绝不半途删除快照。</para>
    /// <para>线程边界：非线程安全，仅在启动流程单线程串行使用。</para>
    /// </summary>
    public sealed class CatalogCacheSnapshotManager
    {
        /// <summary>快照自描述文件，记录快照时缓存目录是否存在（不存在时恢复动作 = 清空缓存回到包内 Catalog）。</summary>
        [Serializable]
        private sealed class SnapshotInfo
        {
            public int SchemaVersion = 1;
            public bool HadCache;
            public long CreatedAtUnixSeconds;
        }

        private const string InfoFileName = "snapshot-info.json";

        private readonly string _catalogCacheDirectory;
        private readonly string _snapshotDirectory;
        private readonly Action<string> _log;
        private readonly Action<string> _logError;

        /// <summary>当前是否存在可用快照。</summary>
        public bool HasSnapshot => File.Exists(Path.Combine(_snapshotDirectory, InfoFileName));

        /// <param name="catalogCacheDirectory">Addressables Catalog 缓存目录（{persistent}/com.unity.addressables）。</param>
        /// <param name="snapshotDirectory">快照存放目录（独立于缓存目录，不得互为父子）。</param>
        /// <param name="log">普通日志回调。</param>
        /// <param name="logError">错误日志回调。</param>
        public CatalogCacheSnapshotManager(
            string catalogCacheDirectory,
            string snapshotDirectory,
            Action<string> log = null,
            Action<string> logError = null)
        {
            if (string.IsNullOrEmpty(catalogCacheDirectory))
                throw new ArgumentException("Catalog 缓存目录不能为空。", nameof(catalogCacheDirectory));
            if (string.IsNullOrEmpty(snapshotDirectory))
                throw new ArgumentException("快照目录不能为空。", nameof(snapshotDirectory));

            string cacheFull = Path.GetFullPath(catalogCacheDirectory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            string snapFull = Path.GetFullPath(snapshotDirectory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (string.Equals(cacheFull, snapFull, StringComparison.OrdinalIgnoreCase) ||
                cacheFull.StartsWith(snapFull + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
                snapFull.StartsWith(cacheFull + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            {
                throw new ArgumentException("缓存目录与快照目录不得相同或互为父子。");
            }

            _catalogCacheDirectory = cacheFull;
            _snapshotDirectory = snapFull;
            _log = log ?? (_ => { });
            _logError = logError ?? (_ => { });
        }

        /// <summary>
        /// 创建当前 Catalog 缓存的完整快照（覆盖旧快照）。
        /// 必须在 Addressables.UpdateCatalogs 之前调用；缓存目录不存在（首次热更）时记录空快照，
        /// 使恢复动作等价于"清空缓存回到包内 Catalog"。
        /// </summary>
        /// <returns>快照创建成功返回 true；失败返回 false（调用方必须失败关闭，中止本次更新）。</returns>
        public bool CreateSnapshot()
        {
            try
            {
                if (Directory.Exists(_snapshotDirectory))
                    Directory.Delete(_snapshotDirectory, true);
                Directory.CreateDirectory(_snapshotDirectory);

                bool hadCache = Directory.Exists(_catalogCacheDirectory);
                if (hadCache)
                    CopyDirectory(_catalogCacheDirectory, _snapshotDirectory);

                var info = new SnapshotInfo
                {
                    HadCache = hadCache,
                    CreatedAtUnixSeconds = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                };
                FileStorages.Shared.AtomicWriteText(
                    Path.Combine(_snapshotDirectory, InfoFileName),
                    JsonSerializers.Shared.ToJson(info, true));

                _log($"[ContentRelease] Catalog 缓存快照已创建（hadCache={hadCache}）。");
                return true;
            }
            catch (Exception ex)
            {
                _logError($"[ContentRelease] 创建 Catalog 缓存快照失败：{ex.Message}");
                TryDeleteDirectory(_snapshotDirectory);
                return false;
            }
        }

        /// <summary>
        /// 用快照覆写 Catalog 缓存目录（回滚动作）。必须在 Addressables 初始化之前调用。
        /// 快照记录"当时无缓存"时，恢复动作 = 删除缓存目录（回到包内 Catalog）。
        /// 恢复成功后快照被消费删除；失败时快照保留供下一次启动重试。
        /// </summary>
        /// <returns>实际执行了恢复返回 true；无快照或恢复失败返回 false。</returns>
        public bool RestoreSnapshot()
        {
            string infoPath = Path.Combine(_snapshotDirectory, InfoFileName);
            if (!File.Exists(infoPath))
                return false;

            SnapshotInfo info;
            try
            {
                info = JsonSerializers.Shared.FromJson<SnapshotInfo>(File.ReadAllText(infoPath));
            }
            catch (Exception ex)
            {
                // 快照自描述损坏：失败安全，不执行半信半疑的恢复，只告警并丢弃损坏快照。
                _logError($"[ContentRelease] Catalog 快照描述文件损坏，跳过恢复并丢弃：{ex.Message}");
                TryDeleteDirectory(_snapshotDirectory);
                return false;
            }

            try
            {
                if (Directory.Exists(_catalogCacheDirectory))
                    Directory.Delete(_catalogCacheDirectory, true);

                if (info != null && info.HadCache)
                {
                    Directory.CreateDirectory(_catalogCacheDirectory);
                    CopyDirectory(_snapshotDirectory, _catalogCacheDirectory, excludeFileName: InfoFileName);
                }

                Directory.Delete(_snapshotDirectory, true);
                _log("[ContentRelease] Catalog 缓存已从快照恢复（回滚到上一份 LKG Catalog）。");
                return true;
            }
            catch (Exception ex)
            {
                // 恢复失败：保留快照供下次启动重试；此时缓存可能已被清空，
                // Addressables 会回退包内 Catalog——比错配的新 Catalog 更安全。
                _logError($"[ContentRelease] Catalog 缓存恢复失败（快照保留待重试）：{ex.Message}");
                return false;
            }
        }

        /// <summary>启动确认成功后丢弃快照：当前缓存正式成为新的 LKG Catalog。</summary>
        public void Discard()
        {
            TryDeleteDirectory(_snapshotDirectory);
        }

        /// <summary>内容级出厂回退：清空 Catalog 缓存与快照，下次启动回到包内 Catalog。</summary>
        public void ResetCacheToFactory()
        {
            TryDeleteDirectory(_catalogCacheDirectory);
            TryDeleteDirectory(_snapshotDirectory);
            _log("[ContentRelease] Catalog 缓存已清空，回退包内出厂 Catalog。");
        }

        private static void CopyDirectory(string source, string destination, string excludeFileName = null)
        {
            Directory.CreateDirectory(destination);
            foreach (string directory in Directory.GetDirectories(source, "*", SearchOption.AllDirectories))
            {
                string relative = directory.Substring(source.Length).TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                Directory.CreateDirectory(Path.Combine(destination, relative));
            }
            foreach (string file in Directory.GetFiles(source, "*", SearchOption.AllDirectories))
            {
                string relative = file.Substring(source.Length).TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                if (excludeFileName != null &&
                    string.Equals(Path.GetFileName(file), excludeFileName, StringComparison.Ordinal))
                {
                    continue;
                }
                string target = Path.Combine(destination, relative);
                Directory.CreateDirectory(Path.GetDirectoryName(target));
                File.Copy(file, target, true);
            }
        }

        private void TryDeleteDirectory(string path)
        {
            try
            {
                if (Directory.Exists(path))
                    Directory.Delete(path, true);
            }
            catch (Exception ex)
            {
                _logError($"[ContentRelease] 删除目录失败 {path}：{ex.Message}");
            }
        }
    }
}
