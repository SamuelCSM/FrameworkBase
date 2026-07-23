using System;
using System.Collections.Generic;
using System.Threading;
using Cysharp.Threading.Tasks;
using UnityEngine.AddressableAssets;
using UnityEngine.Networking;
using UnityEngine.ResourceManagement.AsyncOperations;
using UnityEngine.ResourceManagement.ResourceLocations;

namespace Framework
{
    /// <summary>
    /// <see cref="IAddressablesCatalogService"/> 的生产实现：直接包装 Unity Addressables 静态 API。
    /// <para>
    /// 本类必须保持"哑管道"：只做句柄生命周期管理和状态到异常的转换，
    /// 不做任何决策、不吞异常、不记录结论性日志（结论由 <see cref="CatalogUpdateFlow"/> 输出）。
    /// </para>
    /// <para>线程边界：所有方法须在 Unity 主线程调用（Addressables API 要求）。</para>
    /// </summary>
    public sealed class AddressablesCatalogService : IAddressablesCatalogService
    {
        /// <inheritdoc />
        public async UniTask<List<string>> CheckForCatalogUpdatesAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // autoReleaseHandle=false：手动持有句柄以读取 Status 与 OperationException，finally 统一释放。
            var handle = Addressables.CheckForCatalogUpdates(false);
            try
            {
                await handle.Task;
                cancellationToken.ThrowIfCancellationRequested();

                if (handle.Status != AsyncOperationStatus.Succeeded)
                {
                    throw new CatalogOperationException(
                        $"CheckForCatalogUpdates 状态={handle.Status}：{handle.OperationException?.Message ?? "无诊断信息"}");
                }

                // 复制结果列表：句柄释放后 Result 不可再访问。
                return handle.Result == null ? null : new List<string>(handle.Result);
            }
            finally
            {
                if (handle.IsValid())
                    Addressables.Release(handle);
            }
        }

        /// <inheritdoc />
        public async UniTask UpdateCatalogsAsync(IReadOnlyList<string> catalogIds, CancellationToken cancellationToken)
        {
            if (catalogIds == null || catalogIds.Count == 0)
                throw new ArgumentException("UpdateCatalogs 不接受空 Catalog 列表。", nameof(catalogIds));
            cancellationToken.ThrowIfCancellationRequested();

            var handle = Addressables.UpdateCatalogs(new List<string>(catalogIds), false);
            try
            {
                await handle.Task;
                cancellationToken.ThrowIfCancellationRequested();

                // 历史缺陷修复点：旧实现从不检查 UpdateCatalogs 的句柄状态，失败也被当成功。
                if (handle.Status != AsyncOperationStatus.Succeeded)
                {
                    throw new CatalogOperationException(
                        $"UpdateCatalogs 状态={handle.Status}：{handle.OperationException?.Message ?? "无诊断信息"}");
                }
            }
            finally
            {
                if (handle.IsValid())
                    Addressables.Release(handle);
            }
        }

        /// <inheritdoc />
        public async UniTask<byte[]> DownloadRemoteCatalogBytesAsync(string catalogId, CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(catalogId))
                throw new ArgumentException("Catalog ID 不能为空。", nameof(catalogId));
            cancellationToken.ThrowIfCancellationRequested();

            // 远端 Catalog 的 ID 即其加载 URL（本地 catalog 不进入本路径——只有存在远端更新时才校验）。
            // 这里只取字节做验签，不激活；激活仍由 UpdateCatalogsAsync 走 Addressables 常规路径。
            using (UnityWebRequest request = UnityWebRequest.Get(catalogId))
            {
                try
                {
                    await request.SendWebRequest().WithCancellation(cancellationToken);
                }
                catch (UnityWebRequestException ex)
                {
                    throw new CatalogOperationException(
                        $"下载远端 Catalog 字节失败：id={catalogId}，{ex.Result} {ex.Message}");
                }

                if (request.result != UnityWebRequest.Result.Success)
                {
                    throw new CatalogOperationException(
                        $"下载远端 Catalog 字节失败：id={catalogId}，result={request.result}，error={request.error}");
                }

                return request.downloadHandler.data ?? Array.Empty<byte>();
            }
        }

        /// <inheritdoc />
        public async UniTask<long> GetDownloadSizeAsync(object key, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            AsyncOperationHandle<long> handle;
            try
            {
                handle = Addressables.GetDownloadSizeAsync(key);
            }
            catch (UnityEngine.AddressableAssets.InvalidKeyException)
            {
                // key 不在当前 Catalog（分组名当 key、label 未设置等）：语义上等价于"无需下载"，不属于查询失败。
                return 0;
            }

            try
            {
                await handle.Task;
                cancellationToken.ThrowIfCancellationRequested();

                if (handle.Status != AsyncOperationStatus.Succeeded)
                {
                    if (handle.OperationException is UnityEngine.AddressableAssets.InvalidKeyException)
                        return 0;
                    throw new CatalogOperationException(
                        $"GetDownloadSizeAsync 状态={handle.Status}：{handle.OperationException?.Message ?? "无诊断信息"}");
                }

                return handle.Result;
            }
            finally
            {
                if (handle.IsValid())
                    Addressables.Release(handle);
            }
        }
    }
}
