using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using Cysharp.Threading.Tasks;
using Framework.HotUpdate;

namespace Framework
{
    /// <summary>
    /// Catalog 检查/更新编排（纯逻辑，可测试）。
    /// <para>
    /// 通过 <see cref="IAddressablesCatalogService"/> 注入底层能力，把每一种失败显式映射为
    /// <see cref="CatalogUpdateResult"/> 的独立终态。本类是"资源更新失败误判为成功"P0 的修复核心：
    /// 任何检查失败、更新失败、异常或取消都不允许再被折叠成"没有更新"。
    /// </para>
    /// <para>线程边界：本类无内部状态，方法可在任意上下文 await；日志回调由调用方注入。</para>
    /// </summary>
    public sealed class CatalogUpdateFlow
    {
        private readonly IAddressablesCatalogService _service;
        private readonly Action<string> _log;
        private readonly Action<string> _logError;

        /// <param name="service">Addressables 底层适配（生产环境传 <c>AddressablesCatalogService</c>，测试传假实现）。</param>
        /// <param name="log">普通日志回调；null 时丢弃。</param>
        /// <param name="logError">错误日志回调；null 时丢弃。</param>
        public CatalogUpdateFlow(IAddressablesCatalogService service, Action<string> log = null, Action<string> logError = null)
        {
            _service = service ?? throw new ArgumentNullException(nameof(service));
            _log = log ?? (_ => { });
            _logError = logError ?? (_ => { });
        }

        /// <summary>
        /// 检查并更新 Catalog。所有终态见 <see cref="CatalogUpdateStatus"/>；本方法不抛异常（取消也转为结果），
        /// 调用方必须检查 <see cref="CatalogUpdateResult.Succeeded"/> 决定是否继续资源下载。
        /// </summary>
        public async UniTask<CatalogUpdateResult> CheckAndUpdateAsync(
            CancellationToken cancellationToken = default,
            ResourceCatalogFile expectedCatalog = null)
        {
            // ── 阶段 1：检查远端是否有新 Catalog ─────────────────────────────
            List<string> updatedCatalogs;
            try
            {
                updatedCatalogs = await _service.CheckForCatalogUpdatesAsync(cancellationToken);
            }
            catch (OperationCanceledException)
            {
                _log("[ResourceManager] Catalog 检查被取消。");
                return CatalogUpdateResult.Failed(
                    CatalogUpdateStatus.Canceled, CatalogUpdateErrorCodes.Canceled, "Catalog 检查被取消");
            }
            catch (CatalogOperationException ex)
            {
                _logError($"[ResourceManager] CheckForCatalogUpdates 操作失败：{ex.Message}");
                return CatalogUpdateResult.Failed(
                    CatalogUpdateStatus.CheckFailed, CatalogUpdateErrorCodes.CheckOperationFailed, ex.Message);
            }
            catch (Exception ex)
            {
                // 失败恢复路径：检查异常时本地 Catalog 未被修改，中止本次更新即可，无需回滚动作；
                // 但绝不允许吞掉异常返回"没有更新"——那会让上层错误提交 ResourceVersion。
                _logError($"[ResourceManager] CheckForCatalogUpdates 异常：{ex.Message}");
                return CatalogUpdateResult.Failed(
                    CatalogUpdateStatus.CheckFailed, CatalogUpdateErrorCodes.CheckException, ex.Message);
            }

            if (updatedCatalogs == null)
            {
                // 契约违规：操作声称成功但结果为 null。失败关闭，禁止猜测语义。
                _logError("[ResourceManager] CheckForCatalogUpdates 返回 null 结果，违反 Addressables 契约。");
                return CatalogUpdateResult.Failed(
                    CatalogUpdateStatus.Invalid, CatalogUpdateErrorCodes.InvalidResult, "检查操作返回 null 结果");
            }

            if (updatedCatalogs.Count == 0)
            {
                _log("[ResourceManager] Catalog 已是最新，无需更新。");
                return CatalogUpdateResult.UpToDate();
            }

            // ── 阶段 1.5：已验签内容身份校验（ADR-009）─────────────────────
            // 仅当调用方传入 expectedCatalog（资源版本增长、清单已带签名身份）时执行；应用一个未经发布方
            // 签名背书的资源目录是热更安全红线。老项目/纯代码更新 expectedCatalog 为 null，保持原行为。
            if (expectedCatalog != null)
            {
                CatalogUpdateResult? integrityFailure =
                    await VerifyCatalogIntegrityAsync(updatedCatalogs, expectedCatalog, cancellationToken);
                if (integrityFailure.HasValue)
                    return integrityFailure.Value;
            }

            // ── 阶段 2：下载并激活新 Catalog ────────────────────────────────
            _log($"[ResourceManager] 发现 {updatedCatalogs.Count} 个 Catalog 需要更新，开始下载...");
            try
            {
                await _service.UpdateCatalogsAsync(updatedCatalogs, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                _log("[ResourceManager] Catalog 更新被取消。");
                return CatalogUpdateResult.Failed(
                    CatalogUpdateStatus.Canceled, CatalogUpdateErrorCodes.Canceled, "Catalog 更新被取消");
            }
            catch (CatalogOperationException ex)
            {
                _logError($"[ResourceManager] UpdateCatalogs 操作失败：{ex.Message}");
                return CatalogUpdateResult.Failed(
                    CatalogUpdateStatus.UpdateFailed, CatalogUpdateErrorCodes.UpdateOperationFailed, ex.Message);
            }
            catch (Exception ex)
            {
                _logError($"[ResourceManager] UpdateCatalogs 异常：{ex.Message}");
                return CatalogUpdateResult.Failed(
                    CatalogUpdateStatus.UpdateFailed, CatalogUpdateErrorCodes.UpdateException, ex.Message);
            }

            _log($"[ResourceManager] Catalog 更新完成，共更新 {updatedCatalogs.Count} 个。");
            return CatalogUpdateResult.Updated(updatedCatalogs.Count);
        }

        /// <summary>
        /// 应用远端 Catalog 前，对其与已验签清单声明的内容身份（Size/SHA-256）做校验（ADR-009）。
        /// 返回 null 表示通过；返回非 null 的失败结果表示失败关闭，调用方据此中止、不应用任何 Catalog。
        /// </summary>
        private async UniTask<CatalogUpdateResult?> VerifyCatalogIntegrityAsync(
            List<string> updatedCatalogs, ResourceCatalogFile expected, CancellationToken cancellationToken)
        {
            // 定位与已验签身份对应的待更新 Catalog：仅一个时直接取（单一远端内容 Catalog 是常态），
            // 多个时按文件名匹配。定位不到即失败关闭——签名声明了该身份、更新集里却没有，视为异常。
            string targetId = updatedCatalogs.Count == 1
                ? updatedCatalogs[0]
                : updatedCatalogs.FirstOrDefault(id =>
                    !string.IsNullOrEmpty(id) &&
                    id.IndexOf(expected.FileName, StringComparison.OrdinalIgnoreCase) >= 0);

            if (string.IsNullOrEmpty(targetId))
            {
                _logError($"[ResourceManager] 已验签清单声明了资源 Catalog 身份 {expected.FileName}，" +
                          "但待更新 Catalog 中无匹配项，拒绝应用。");
                return CatalogUpdateResult.Failed(
                    CatalogUpdateStatus.IntegrityFailed, CatalogUpdateErrorCodes.IntegrityFailed,
                    $"无法定位与已验签身份匹配的待更新 Catalog：{expected.FileName}");
            }

            byte[] catalogBytes;
            try
            {
                catalogBytes = await _service.DownloadRemoteCatalogBytesAsync(targetId, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                _log("[ResourceManager] Catalog 完整性校验被取消。");
                return CatalogUpdateResult.Failed(
                    CatalogUpdateStatus.Canceled, CatalogUpdateErrorCodes.Canceled, "Catalog 完整性校验被取消");
            }
            catch (Exception ex)
            {
                // 下载失败无法证明身份，失败关闭（不退化成"当作没更新"继续）。
                _logError($"[ResourceManager] 下载远端 Catalog 字节失败：{ex.Message}");
                return CatalogUpdateResult.Failed(
                    CatalogUpdateStatus.IntegrityFailed, CatalogUpdateErrorCodes.IntegrityFailed, ex.Message);
            }

            if (!ResourceCatalogVerifier.Verify(catalogBytes, expected, out string reason))
            {
                _logError($"[ResourceManager] 远端 Catalog 内容身份校验失败，拒绝应用：{reason}");
                return CatalogUpdateResult.Failed(
                    CatalogUpdateStatus.IntegrityFailed, CatalogUpdateErrorCodes.IntegrityFailed, reason);
            }

            _log("[ResourceManager] 远端 Catalog 已验签内容身份校验通过。");
            return null;
        }

        /// <summary>
        /// 查询指定 key 的待下载字节数。查询失败返回 <see cref="DownloadSizeStatus.Failed"/>，
        /// 与"无需下载（0 字节）"严格区分；本方法不抛异常。
        /// </summary>
        public async UniTask<DownloadSizeResult> GetDownloadSizeAsync(object key, CancellationToken cancellationToken = default)
        {
            try
            {
                long bytes = await _service.GetDownloadSizeAsync(key, cancellationToken);
                if (bytes < 0)
                {
                    _logError($"[ResourceManager] GetDownloadSize 返回负值 [{key}]：{bytes}");
                    return DownloadSizeResult.Failed($"下载尺寸返回负值：{bytes}");
                }
                return DownloadSizeResult.Ok(bytes);
            }
            catch (OperationCanceledException)
            {
                _log($"[ResourceManager] 下载尺寸查询被取消 [{key}]。");
                return DownloadSizeResult.Canceled();
            }
            catch (Exception ex)
            {
                // "查询失败"必须显式向上传播；旧实现吞异常返回 0 会被误判为"所有 bundle 已是最新"。
                _logError($"[ResourceManager] 下载尺寸查询失败 [{key}]：{ex.Message}");
                return DownloadSizeResult.Failed(ex.Message);
            }
        }
    }

    /// <summary>
    /// 远端 Catalog 内容身份校验（ADR-009）的纯逻辑：把下载到的 Catalog 字节与已验签
    /// <see cref="ResourceCatalogFile"/> 的 Size/SHA-256 比对。无 Unity 依赖，可 EditMode 独立测。
    /// </summary>
    internal static class ResourceCatalogVerifier
    {
        /// <summary>
        /// 校验 Catalog 字节是否匹配期望身份。任一不符即返回 false 并给出原因（失败关闭）。
        /// </summary>
        public static bool Verify(byte[] catalogBytes, ResourceCatalogFile expected, out string rejectReason)
        {
            rejectReason = null;
            if (expected == null)
            {
                rejectReason = "缺少期望的 Catalog 身份。";
                return false;
            }
            if (catalogBytes == null || catalogBytes.Length == 0)
            {
                rejectReason = "下载到的 Catalog 字节为空。";
                return false;
            }
            if (catalogBytes.Length != expected.Size)
            {
                rejectReason = $"Catalog 长度不一致：期望 {expected.Size}，实际 {catalogBytes.Length}。";
                return false;
            }

            string actual = ComputeSha256Hex(catalogBytes);
            if (!FixedTimeHexEquals(actual, expected.SHA256))
            {
                rejectReason = $"Catalog SHA-256 不一致：期望 {expected.SHA256}，实际 {actual}。";
                return false;
            }
            return true;
        }

        private static string ComputeSha256Hex(byte[] data)
        {
            using (SHA256 sha = SHA256.Create())
            {
                byte[] hash = sha.ComputeHash(data);
                var sb = new StringBuilder(hash.Length * 2);
                foreach (byte b in hash)
                    sb.Append(b.ToString("x2"));
                return sb.ToString();
            }
        }

        // 定长比较，避免按字符提前返回泄漏摘要匹配前缀长度。
        private static bool FixedTimeHexEquals(string a, string b)
        {
            if (a == null || b == null || a.Length != b.Length)
                return false;
            int diff = 0;
            for (int i = 0; i < a.Length; i++)
                diff |= char.ToLowerInvariant(a[i]) ^ char.ToLowerInvariant(b[i]);
            return diff == 0;
        }
    }
}
