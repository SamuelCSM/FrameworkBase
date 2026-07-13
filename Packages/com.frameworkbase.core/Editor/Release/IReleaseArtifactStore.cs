using System.Collections.Generic;

namespace Framework.Editor.Release
{
    /// <summary>
    /// 发布产物存储抽象（主干中立接口，不绑定任何云厂商）。
    /// <para>
    /// 目的：Publish / Promote / Rollback / VerifyOnly 共用同一套存储语义与发布状态机，
    /// 主干只提供目录型实现 <see cref="LocalFileSystemReleaseStore"/>（本地演练与 CI 端到端）；
    /// AWS S3 / 阿里云 OSS / 腾讯云 COS / Azure Blob / 公司内部 CDN 等实现作为扩展包提供，
    /// 绝不硬编码进 FrameworkBase 核心主干。
    /// </para>
    /// <para>
    /// 不可变契约：releases/{app}/{releaseId}/ 下的产物一经写入永不修改。
    /// <see cref="PutImmutable"/> 遇到同路径且摘要不同必须失败关闭，禁止覆盖不可变产物；
    /// 唯一可变对象是渠道根指针 current.json / version.json 别名，经 <see cref="PutMutable"/> 提交。
    /// </para>
    /// </summary>
    public interface IReleaseArtifactStore
    {
        /// <summary>存储诊断信息（类型 + 根定位），用于日志与失败排查。</summary>
        string Describe();

        /// <summary>相对路径对象是否存在。</summary>
        bool Exists(string relativePath);

        /// <summary>读取相对路径对象的完整字节。不存在时抛异常。</summary>
        byte[] Read(string relativePath);

        /// <summary>计算相对路径对象内容的 SHA-256（小写十六进制）。不存在时抛异常。</summary>
        string ComputeSha256(string relativePath);

        /// <summary>
        /// 写入不可变对象：目标不存在则写入；已存在且摘要相同视为幂等重试（跳过）；
        /// 已存在但摘要不同必须抛异常（不可变路径冲突），禁止覆盖。
        /// </summary>
        /// <param name="relativePath">目标相对路径。</param>
        /// <param name="sourceFile">源文件绝对路径。</param>
        void PutImmutable(string relativePath, string sourceFile);

        /// <summary>
        /// 写入/覆盖可变对象（仅限渠道根 current.json/current.json.sig 与 version.json 别名）。
        /// 实现须保证单对象替换的原子性（写临时对象后原子替换），使旧对象在替换窗口内始终可读。
        /// </summary>
        void PutMutable(string relativePath, string sourceFile);

        /// <summary>枚举 releases/ 下的全部 releaseId（不可变版本目录名）。</summary>
        IReadOnlyList<string> EnumerateReleaseIds();

        /// <summary>删除发布过程中的临时 staging 对象（相对路径子树）。删除失败只告警，不抛出。</summary>
        void DeleteStaging(string relativePath);
    }

    /// <summary>发布存储相关的稳定错误码（用于 CI 门禁匹配与告警规则，一经发布不得改名）。</summary>
    public static class ReleaseStoreErrorCodes
    {
        /// <summary>Publish/Promote/Rollback 模式下未配置部署目标。</summary>
        public const string StoreNotConfigured = "RELEASE_E_STORE_NOT_CONFIGURED";

        /// <summary>试图覆盖已存在且内容不同的不可变产物。</summary>
        public const string ImmutableConflict = "RELEASE_E_IMMUTABLE_CONFLICT";
    }

    /// <summary>不可变产物路径冲突异常（同路径已存在且内容不同）。</summary>
    public sealed class ImmutableArtifactConflictException : System.Exception
    {
        public ImmutableArtifactConflictException(string message)
            : base($"{ReleaseStoreErrorCodes.ImmutableConflict}: {message}") { }
    }

    /// <summary>部署目标未配置异常（Publish/Promote/Rollback 要求非空部署目标）。</summary>
    public sealed class ReleaseStoreNotConfiguredException : System.Exception
    {
        public ReleaseStoreNotConfiguredException(string message)
            : base($"{ReleaseStoreErrorCodes.StoreNotConfigured}: {message}") { }
    }
}
