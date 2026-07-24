using System;
using System.Collections.Generic;
using System.Threading;
using Cysharp.Threading.Tasks;

namespace Framework
{
    /// <summary>
    /// Addressables Catalog 操作在"操作完成但状态非 Succeeded"时抛出的异常。
    /// 与普通异常（网络栈 / 序列化等意外异常）分开，使上层能区分
    /// "操作失败"（<see cref="CatalogUpdateErrorCodes.CheckOperationFailed"/> 等）与
    /// "操作过程抛异常"（<see cref="CatalogUpdateErrorCodes.CheckException"/> 等）两类错误码。
    /// </summary>
    public sealed class CatalogOperationException : Exception
    {
        public CatalogOperationException(string message) : base(message) { }
    }

    /// <summary>
    /// Addressables Catalog 底层能力适配层。
    /// <para>
    /// 只封装 Unity Addressables 静态 API 的最小必要子集，不包含任何启动流程决策或业务逻辑。
    /// 存在的唯一目的：Addressables 静态 API 无法在 EditMode 测试中注入失败，
    /// 通过该接口的假实现即可复现 <see cref="CatalogUpdateFlow"/> 的全部失败路径。
    /// </para>
    /// <para>
    /// 契约：
    /// 1. 操作完成但底层状态非 Succeeded 时抛 <see cref="CatalogOperationException"/>；
    /// 2. 取消时抛 <see cref="OperationCanceledException"/>；
    /// 3. 其余意外异常原样上抛，由 <see cref="CatalogUpdateFlow"/> 归类，禁止在本层吞掉。
    /// </para>
    /// </summary>
    public interface IAddressablesCatalogService
    {
        /// <summary>
        /// 检查远端是否有新 Catalog。
        /// </summary>
        /// <param name="cancellationToken">取消令牌。</param>
        /// <returns>需要更新的 Catalog ID 列表；无更新时为空列表。返回 null 属于底层契约违规，由上层判定 Invalid。</returns>
        UniTask<List<string>> CheckForCatalogUpdatesAsync(CancellationToken cancellationToken);

        /// <summary>
        /// 下载并激活给定的 Catalog 集合。任何一个失败都必须以异常终止，禁止部分成功静默返回。
        /// </summary>
        /// <param name="catalogIds">需要更新的 Catalog ID 列表（来自 <see cref="CheckForCatalogUpdatesAsync"/>）。</param>
        /// <param name="cancellationToken">取消令牌。</param>
        UniTask UpdateCatalogsAsync(IReadOnlyList<string> catalogIds, CancellationToken cancellationToken);

        /// <summary>
        /// 下载指定 Catalog 的原始字节，供应用前对已验签内容身份（Size/SHA-256）校验（ADR-009）。
        /// 只取字节、不激活；下载失败以异常终止（由 <see cref="CatalogUpdateFlow"/> 归类为完整性失败）。
        /// </summary>
        /// <param name="catalogId">Catalog ID（来自 <see cref="CheckForCatalogUpdatesAsync"/>，远端 Catalog 通常为其加载 URL）。</param>
        /// <param name="cancellationToken">取消令牌。</param>
        /// <returns>Catalog 文件原始字节。</returns>
        UniTask<byte[]> DownloadRemoteCatalogBytesAsync(string catalogId, CancellationToken cancellationToken);

        /// <summary>
        /// 查询指定 key（Address / Label）需要下载的字节数。
        /// key 在 Catalog 中不存在（InvalidKeyException）视为 0 字节（无需下载）；
        /// 其余失败必须以异常终止，禁止吞成 0——"查询失败"与"无需下载"是两个不同结果。
        /// </summary>
        /// <param name="key">Address / Label / AssetReference。</param>
        /// <param name="cancellationToken">取消令牌。</param>
        /// <returns>待下载字节数。</returns>
        UniTask<long> GetDownloadSizeAsync(object key, CancellationToken cancellationToken);
    }
}
