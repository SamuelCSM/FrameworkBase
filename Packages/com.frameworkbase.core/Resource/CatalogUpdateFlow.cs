using System;
using System.Collections.Generic;
using System.Threading;
using Cysharp.Threading.Tasks;

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
        public async UniTask<CatalogUpdateResult> CheckAndUpdateAsync(CancellationToken cancellationToken = default)
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
}
