using System;

namespace Framework
{
    /// <summary>
    /// Catalog 更新流程的终态分类。
    /// <para>
    /// 历史缺陷：旧实现用单个 int（更新数量）表示全部结果，返回 0 同时可能意味着
    /// "没有更新 / 检查失败 / 检查抛异常 / 更新失败 / 更新抛异常"五种语义，
    /// 调用方无法区分"无需更新"与"更新失败"，导致失败链路最终被误提交 ResourceVersion。
    /// 本枚举把每一种终态显式分开，禁止再退化回单一数值。
    /// </para>
    /// </summary>
    public enum CatalogUpdateStatus
    {
        /// <summary>检查成功且远端没有新 Catalog，无需更新。允许继续资源下载。</summary>
        UpToDate = 0,

        /// <summary>检查成功、全部新 Catalog 已下载并激活。允许继续资源下载。</summary>
        Updated = 1,

        /// <summary>
        /// CheckForCatalogUpdates 执行失败或抛异常。此时无法得知远端是否有更新，
        /// 本地 Catalog 可能已落后，绝不允许当作"没有更新"继续走提交链路。
        /// </summary>
        CheckFailed = 2,

        /// <summary>UpdateCatalogs 执行失败或抛异常。本地 Catalog 状态不可信，必须中止本次启动更新。</summary>
        UpdateFailed = 3,

        /// <summary>流程被 CancellationToken 取消。调用方应中止本次启动更新，不得提交任何版本状态。</summary>
        Canceled = 4,

        /// <summary>
        /// 底层返回值不符合 Addressables 契约（例如操作 Succeeded 但结果列表为 null）。
        /// 视为失败关闭，与 CheckFailed 同样禁止继续。
        /// </summary>
        Invalid = 5,
    }

    /// <summary>
    /// Catalog 更新流程的稳定错误码。用于日志检索、遥测聚合与告警规则，一经发布不得改名。
    /// </summary>
    public static class CatalogUpdateErrorCodes
    {
        /// <summary>无错误。</summary>
        public const string None = "";

        /// <summary>CheckForCatalogUpdates 操作完成但状态非 Succeeded。</summary>
        public const string CheckOperationFailed = "catalog_check_failed";

        /// <summary>CheckForCatalogUpdates 过程抛出异常（网络 / CDN / Catalog 格式等）。</summary>
        public const string CheckException = "catalog_check_exception";

        /// <summary>UpdateCatalogs 操作完成但状态非 Succeeded。</summary>
        public const string UpdateOperationFailed = "catalog_update_failed";

        /// <summary>UpdateCatalogs 过程抛出异常。</summary>
        public const string UpdateException = "catalog_update_exception";

        /// <summary>流程被取消。</summary>
        public const string Canceled = "catalog_canceled";

        /// <summary>底层返回值不符合契约（Succeeded 但结果为 null 等）。</summary>
        public const string InvalidResult = "catalog_invalid_result";
    }

    /// <summary>
    /// Catalog 更新结果（不可变值类型）。
    /// <para>
    /// 满足任务书对结果模型的最低要求：是否成功（<see cref="Succeeded"/>）、是否发生 Catalog 更新
    /// （<see cref="CatalogChanged"/>）、更新数量（<see cref="UpdatedCatalogCount"/>）、稳定错误码
    /// （<see cref="ErrorCode"/>）、可诊断错误信息（<see cref="Message"/>）、是否因取消结束
    /// （<see cref="WasCanceled"/>）。
    /// </para>
    /// </summary>
    public readonly struct CatalogUpdateResult
    {
        /// <summary>终态分类。</summary>
        public CatalogUpdateStatus Status { get; }

        /// <summary>本次实际更新的 Catalog 数量；仅 <see cref="CatalogUpdateStatus.Updated"/> 时大于 0。</summary>
        public int UpdatedCatalogCount { get; }

        /// <summary>稳定错误码（见 <see cref="CatalogUpdateErrorCodes"/>）；成功时为空字符串。</summary>
        public string ErrorCode { get; }

        /// <summary>可诊断错误信息（异常消息 / 操作失败原因）；成功时为空字符串。</summary>
        public string Message { get; }

        /// <summary>是否成功：检查成功且无更新，或更新成功。只有这两种情况允许继续资源下载。</summary>
        public bool Succeeded => Status == CatalogUpdateStatus.UpToDate || Status == CatalogUpdateStatus.Updated;

        /// <summary>本次是否发生了 Catalog 更新（新 Catalog 已激活）。</summary>
        public bool CatalogChanged => Status == CatalogUpdateStatus.Updated;

        /// <summary>是否因取消而结束。</summary>
        public bool WasCanceled => Status == CatalogUpdateStatus.Canceled;

        private CatalogUpdateResult(CatalogUpdateStatus status, int updatedCatalogCount, string errorCode, string message)
        {
            Status = status;
            UpdatedCatalogCount = updatedCatalogCount;
            ErrorCode = errorCode ?? string.Empty;
            Message = message ?? string.Empty;
        }

        /// <summary>检查成功且远端无更新。</summary>
        public static CatalogUpdateResult UpToDate() =>
            new CatalogUpdateResult(CatalogUpdateStatus.UpToDate, 0, CatalogUpdateErrorCodes.None, string.Empty);

        /// <summary>更新成功，<paramref name="updatedCatalogCount"/> 个 Catalog 已激活。</summary>
        public static CatalogUpdateResult Updated(int updatedCatalogCount)
        {
            if (updatedCatalogCount < 1)
                throw new ArgumentOutOfRangeException(nameof(updatedCatalogCount), "更新成功的结果必须至少包含 1 个 Catalog。");
            return new CatalogUpdateResult(CatalogUpdateStatus.Updated, updatedCatalogCount, CatalogUpdateErrorCodes.None, string.Empty);
        }

        /// <summary>失败终态工厂：状态必须是失败类（CheckFailed/UpdateFailed/Canceled/Invalid），错误码不得为空。</summary>
        public static CatalogUpdateResult Failed(CatalogUpdateStatus status, string errorCode, string message)
        {
            if (status == CatalogUpdateStatus.UpToDate || status == CatalogUpdateStatus.Updated)
                throw new ArgumentException("失败工厂不接受成功状态。", nameof(status));
            if (string.IsNullOrEmpty(errorCode))
                throw new ArgumentException("失败结果必须携带稳定错误码。", nameof(errorCode));
            return new CatalogUpdateResult(status, 0, errorCode, message);
        }

        public override string ToString() =>
            Succeeded
                ? $"CatalogUpdateResult(Status={Status}, Updated={UpdatedCatalogCount})"
                : $"CatalogUpdateResult(Status={Status}, ErrorCode={ErrorCode}, Message={Message})";
    }

    /// <summary>
    /// 下载尺寸查询结果状态。
    /// 把"无需下载（0 字节）"和"查询失败"显式分成两种结果——旧实现把异常吞成 0，
    /// 导致"下载尺寸查询失败"被误判为"所有 bundle 已是最新"。
    /// </summary>
    public enum DownloadSizeStatus
    {
        /// <summary>查询成功；<see cref="DownloadSizeResult.Bytes"/> 为待下载字节数（0 表示无需下载）。</summary>
        Succeeded = 0,

        /// <summary>查询失败；无法得知是否需要下载，必须中止本次启动更新。</summary>
        Failed = 1,

        /// <summary>查询被取消。</summary>
        Canceled = 2,
    }

    /// <summary>下载尺寸查询结果（不可变值类型）。</summary>
    public readonly struct DownloadSizeResult
    {
        /// <summary>查询终态。</summary>
        public DownloadSizeStatus Status { get; }

        /// <summary>待下载字节数；仅 <see cref="DownloadSizeStatus.Succeeded"/> 时有效。</summary>
        public long Bytes { get; }

        /// <summary>可诊断错误信息；成功时为空。</summary>
        public string Message { get; }

        /// <summary>是否查询成功。</summary>
        public bool Succeeded => Status == DownloadSizeStatus.Succeeded;

        private DownloadSizeResult(DownloadSizeStatus status, long bytes, string message)
        {
            Status = status;
            Bytes = bytes;
            Message = message ?? string.Empty;
        }

        /// <summary>查询成功。</summary>
        public static DownloadSizeResult Ok(long bytes)
        {
            if (bytes < 0)
                throw new ArgumentOutOfRangeException(nameof(bytes), "待下载字节数不能为负。");
            return new DownloadSizeResult(DownloadSizeStatus.Succeeded, bytes, string.Empty);
        }

        /// <summary>查询失败。</summary>
        public static DownloadSizeResult Failed(string message) =>
            new DownloadSizeResult(DownloadSizeStatus.Failed, 0, message);

        /// <summary>查询被取消。</summary>
        public static DownloadSizeResult Canceled() =>
            new DownloadSizeResult(DownloadSizeStatus.Canceled, 0, string.Empty);

        public override string ToString() =>
            Succeeded ? $"DownloadSizeResult(Bytes={Bytes})" : $"DownloadSizeResult(Status={Status}, Message={Message})";
    }
}
