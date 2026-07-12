using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;

namespace Framework.Editor.Release
{
    /// <summary>
    /// 目录型发布产物存储（主干唯一实现）。
    /// <para>
    /// 把 <see cref="IReleaseArtifactStore"/> 的语义落到一个本地/挂载目录根上，用于本地演练与 CI 端到端。
    /// 目录根可以是本地磁盘、静态服务器挂载点或对象存储的本地网关目录——只要暴露为文件系统路径即可，
    /// 因此它既是"真实发布"的一种存储实现，也是演练与真机路径共用的同一套状态机（非测试专用简化逻辑）。
    /// </para>
    /// <para>路径安全：所有相对路径经规范化后必须仍位于根目录内，越界一律拒绝，防止路径穿越。</para>
    /// </summary>
    public sealed class LocalFileSystemReleaseStore : IReleaseArtifactStore
    {
        private readonly string _root;

        /// <param name="root">存储根目录绝对路径（渠道根 {env}/{platform}/{channel}）。</param>
        public LocalFileSystemReleaseStore(string root)
        {
            if (string.IsNullOrWhiteSpace(root))
                throw new ArgumentException("存储根目录不能为空。", nameof(root));
            _root = Path.GetFullPath(root);
            Directory.CreateDirectory(_root);
        }

        /// <summary>存储根目录绝对路径。</summary>
        public string Root => _root;

        /// <inheritdoc />
        public string Describe() => $"LocalFileSystemReleaseStore(root={_root})";

        /// <inheritdoc />
        public bool Exists(string relativePath) => File.Exists(Resolve(relativePath));

        /// <inheritdoc />
        public byte[] Read(string relativePath) => File.ReadAllBytes(Resolve(relativePath));

        /// <inheritdoc />
        public string ComputeSha256(string relativePath) => Sha256OfFile(Resolve(relativePath));

        /// <inheritdoc />
        public void PutImmutable(string relativePath, string sourceFile)
        {
            if (!File.Exists(sourceFile))
                throw new FileNotFoundException($"发布源文件不存在：{sourceFile}");

            string target = Resolve(relativePath);
            if (File.Exists(target))
            {
                // 幂等重试：内容一致视为已发布，跳过；内容不同则为不可变冲突，失败关闭。
                if (string.Equals(Sha256OfFile(sourceFile), Sha256OfFile(target), StringComparison.OrdinalIgnoreCase))
                    return;
                throw new ImmutableArtifactConflictException(
                    $"不可变产物已存在但摘要不同，拒绝覆盖：{relativePath}");
            }

            Directory.CreateDirectory(Path.GetDirectoryName(target));
            File.Copy(sourceFile, target, false);
        }

        /// <inheritdoc />
        public void PutMutable(string relativePath, string sourceFile)
        {
            if (!File.Exists(sourceFile))
                throw new FileNotFoundException($"可变对象源文件不存在：{sourceFile}");

            string target = Resolve(relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(target));

            // 原子替换：先写临时对象，再 File.Replace / Move，使旧对象在窗口内始终可读。
            string temp = target + ".publishing";
            File.Copy(sourceFile, temp, true);
            if (File.Exists(target))
            {
                string backup = target + ".store-backup";
                if (File.Exists(backup)) File.Delete(backup);
                File.Replace(temp, target, backup, true);
                if (File.Exists(backup)) File.Delete(backup);
            }
            else
            {
                File.Move(temp, target);
            }
        }

        /// <inheritdoc />
        public IReadOnlyList<string> EnumerateReleaseIds()
        {
            string releasesRoot = Path.Combine(_root, "releases");
            if (!Directory.Exists(releasesRoot))
                return Array.Empty<string>();

            // releases/{app}/{releaseId}：releaseId 是包含 ledger.json 的叶子目录。
            return Directory.GetDirectories(releasesRoot, "*", SearchOption.AllDirectories)
                .Where(dir => File.Exists(Path.Combine(dir, "ledger.json")))
                .Select(dir => Path.GetFileName(dir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)))
                .OrderBy(id => id, StringComparer.Ordinal)
                .ToList();
        }

        /// <inheritdoc />
        public void DeleteStaging(string relativePath)
        {
            try
            {
                string target = Resolve(relativePath);
                if (Directory.Exists(target))
                    Directory.Delete(target, true);
                else if (File.Exists(target))
                    File.Delete(target);
            }
            catch
            {
                // staging 清理失败不影响发布正确性（下次发布会重建），仅忽略。
            }
        }

        /// <summary>把相对路径规范化为根目录内的绝对路径，越界拒绝。</summary>
        private string Resolve(string relativePath)
        {
            if (string.IsNullOrWhiteSpace(relativePath))
                throw new ArgumentException("相对路径不能为空。", nameof(relativePath));

            string rootPrefix = _root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
            string full = Path.GetFullPath(Path.Combine(_root, relativePath.Replace('/', Path.DirectorySeparatorChar)));
            if (!full.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"相对路径逃逸存储根目录：{relativePath}");
            return full;
        }

        private static string Sha256OfFile(string path)
        {
            using (SHA256 sha = SHA256.Create())
            using (FileStream stream = File.OpenRead(path))
            {
                return BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", string.Empty).ToLowerInvariant();
            }
        }
    }

    /// <summary>
    /// 发布存储解析工厂：按发布模式与 Profile 决定使用哪种存储，并执行部署目标失败关闭闸门。
    /// <para>
    /// 主干只识别目录型 Store（Profile.UploadRoot 指向的目录）。扩展包可在此之外注册云厂商 Store
    /// （例如按 Profile 增加 StoreType 字段派发），但那属于扩展包职责，不进主干工厂。
    /// </para>
    /// </summary>
    public static class ReleaseArtifactStoreFactory
    {
        /// <summary>
        /// 按模式解析存储。
        /// Publish/Promote/Rollback/VerifyOnly 要求非空部署目标，为空抛
        /// <see cref="ReleaseStoreNotConfiguredException"/>（失败关闭，禁止静默跳过部署）；
        /// BuildOnly 返回 null（只产候选产物，不部署）。
        /// </summary>
        /// <param name="mode">发布模式。</param>
        /// <param name="uploadRoot">部署目标根（Profile.UploadRoot，已应用 -uploadRoot 覆盖）。</param>
        public static IReleaseArtifactStore Resolve(ReleaseMode mode, string uploadRoot)
        {
            if (mode == ReleaseMode.BuildOnly)
                return null;

            if (string.IsNullOrWhiteSpace(uploadRoot))
            {
                throw new ReleaseStoreNotConfiguredException(
                    $"{mode} 模式要求配置部署目标（Profile.UploadRoot 或 -uploadRoot）。" +
                    "只有 BuildOnly 模式允许无部署目标。");
            }

            return new LocalFileSystemReleaseStore(uploadRoot);
        }
    }
}
