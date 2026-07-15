using System;
using System.IO;
using UnityEngine;

namespace Framework.Storage
{
    /// <summary>
    /// 磁盘预检结论。<see cref="Unknown"/> 是失败关闭的关键：卷空间查询失败绝不当作空间充足，
    /// 只有 <see cref="Sufficient"/> 允许安装继续。
    /// </summary>
    public enum StorageCapacityStatus
    {
        /// <summary>可用空间满足预算，允许继续。</summary>
        Sufficient,
        /// <summary>可用空间不足预算，中止安装。</summary>
        Insufficient,
        /// <summary>无法确认卷空间（查询失败/平台不支持）；按失败关闭一律中止。</summary>
        Unknown
    }

    /// <summary>目标卷的可用空间快照。查询失败必须显式为 Unknown，禁止伪装成 0 或无限空间。</summary>
    public readonly struct StorageVolumeSnapshot
    {
        private StorageVolumeSnapshot(bool isKnown, long availableBytes, string source, string error)
        {
            IsKnown = isKnown;
            AvailableBytes = Math.Max(0, availableBytes);
            Source = source ?? string.Empty;
            Error = error ?? string.Empty;
        }

        public bool IsKnown { get; }
        public long AvailableBytes { get; }
        public string Source { get; }
        public string Error { get; }

        public static StorageVolumeSnapshot Known(long availableBytes, string source) =>
            new StorageVolumeSnapshot(true, availableBytes, source, string.Empty);

        public static StorageVolumeSnapshot Unknown(string error) =>
            new StorageVolumeSnapshot(false, 0, string.Empty, error);
    }

    /// <summary>
    /// 目标路径所在卷的可用空间查询抽象。平台实现见 <see cref="SystemStorageCapacityProvider"/>；
    /// 抽象出来是为了让预检逻辑能注入「充足/不足/查询失败」三种测试替身，不触碰开发机真实卷。
    /// </summary>
    public interface IStorageCapacityProvider
    {
        StorageVolumeSnapshot Query(string path);
    }

    /// <summary>
    /// Player 运行时卷空间查询。Android 使用 StatFs；其它平台使用目标路径所在卷的 DriveInfo。
    /// 平台不支持时返回 Unknown，让上层按失败关闭策略处理。
    /// </summary>
    public sealed class SystemStorageCapacityProvider : IStorageCapacityProvider
    {
        public StorageVolumeSnapshot Query(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
                return StorageVolumeSnapshot.Unknown("目标路径为空。");

#if UNITY_ANDROID && !UNITY_EDITOR
            try
            {
                using (var statFs = new AndroidJavaObject("android.os.StatFs", path))
                {
                    long available = statFs.Call<long>("getAvailableBytes");
                    return StorageVolumeSnapshot.Known(available, "Android.StatFs");
                }
            }
            catch (Exception androidEx)
            {
                return StorageVolumeSnapshot.Unknown($"Android StatFs 查询失败：{androidEx.Message}");
            }
#else
            try
            {
                string fullPath = Path.GetFullPath(path);
                string root = Path.GetPathRoot(fullPath);
                if (string.IsNullOrEmpty(root))
                    return StorageVolumeSnapshot.Unknown($"无法解析目标卷：{fullPath}");

                var drive = new DriveInfo(root);
                if (!drive.IsReady)
                    return StorageVolumeSnapshot.Unknown($"目标卷未就绪：{root}");
                return StorageVolumeSnapshot.Known(drive.AvailableFreeSpace, "System.IO.DriveInfo");
            }
            catch (Exception ex)
            {
                return StorageVolumeSnapshot.Unknown($"卷空间查询失败：{ex.Message}");
            }
#endif
        }
    }

    /// <summary>热更安装的磁盘预算策略。</summary>
    public sealed class StorageBudgetPolicy
    {
        public const long MiB = 1024L * 1024L;

        /// <summary>状态文件、日志增长及系统并发写入预留。</summary>
        public long FixedOverheadBytes { get; set; } = 4 * MiB;

        /// <summary>安装完成后仍必须保留的最低自由空间。</summary>
        public long MinimumFreeReserveBytes { get; set; } = 64 * MiB;

        /// <summary>按 Payload 比例追加的动态余量。</summary>
        public double PayloadReserveRatio { get; set; } = 0.10d;

        public long CalculateRequiredBytes(long payloadBytes)
        {
            if (payloadBytes < 0)
                throw new ArgumentOutOfRangeException(nameof(payloadBytes));
            if (FixedOverheadBytes < 0 || MinimumFreeReserveBytes < 0 ||
                PayloadReserveRatio < 0 || double.IsNaN(PayloadReserveRatio) || double.IsInfinity(PayloadReserveRatio))
                throw new InvalidOperationException("磁盘预算策略包含非法负值或比例。");

            long ratioReserve = SaturatingFromDouble(payloadBytes * PayloadReserveRatio);
            long reserve = Math.Max(MinimumFreeReserveBytes, ratioReserve);
            return SaturatingAdd(SaturatingAdd(payloadBytes, FixedOverheadBytes), reserve);
        }

        private static long SaturatingFromDouble(double value) =>
            value >= long.MaxValue ? long.MaxValue : Math.Max(0, (long)Math.Ceiling(value));

        private static long SaturatingAdd(long left, long right) =>
            left > long.MaxValue - right ? long.MaxValue : left + right;
    }

    /// <summary>
    /// 磁盘预检结果。<see cref="CanProceed"/> 仅在 <see cref="StorageCapacityStatus.Sufficient"/> 时为真，
    /// 携带预算/可用/Payload 明细与稳定错误码（<see cref="StoragePreflight.InsufficientCode"/> /
    /// <see cref="StoragePreflight.UnknownCode"/>），供上层提示与排障。
    /// </summary>
    public readonly struct StoragePreflightResult
    {
        internal StoragePreflightResult(
            StorageCapacityStatus status,
            long payloadBytes,
            long requiredBytes,
            long availableBytes,
            string code,
            string message)
        {
            Status = status;
            PayloadBytes = payloadBytes;
            RequiredBytes = requiredBytes;
            AvailableBytes = availableBytes;
            Code = code;
            Message = message;
        }

        public StorageCapacityStatus Status { get; }
        public long PayloadBytes { get; }
        public long RequiredBytes { get; }
        public long AvailableBytes { get; }
        public string Code { get; }
        public string Message { get; }
        public bool CanProceed => Status == StorageCapacityStatus.Sufficient;
    }

    /// <summary>磁盘空间失败关闭门禁。</summary>
    public static class StoragePreflight
    {
        public const string InsufficientCode = "STORAGE_E_INSUFFICIENT_SPACE";
        public const string UnknownCode = "STORAGE_E_SPACE_UNKNOWN";

        public static StoragePreflightResult Check(
            IStorageCapacityProvider provider,
            string targetPath,
            long payloadBytes,
            StorageBudgetPolicy policy = null)
        {
            if (provider == null) throw new ArgumentNullException(nameof(provider));
            policy ??= new StorageBudgetPolicy();

            long required = policy.CalculateRequiredBytes(payloadBytes);
            StorageVolumeSnapshot volume = provider.Query(targetPath);
            if (!volume.IsKnown)
            {
                return new StoragePreflightResult(
                    StorageCapacityStatus.Unknown,
                    payloadBytes,
                    required,
                    0,
                    UnknownCode,
                    $"{UnknownCode}: 无法确认更新目标卷剩余空间；path={targetPath}, reason={volume.Error}");
            }

            if (volume.AvailableBytes < required)
            {
                return new StoragePreflightResult(
                    StorageCapacityStatus.Insufficient,
                    payloadBytes,
                    required,
                    volume.AvailableBytes,
                    InsufficientCode,
                    $"{InsufficientCode}: 更新空间不足；required={required}, available={volume.AvailableBytes}, payload={payloadBytes}, source={volume.Source}");
            }

            return new StoragePreflightResult(
                StorageCapacityStatus.Sufficient,
                payloadBytes,
                required,
                volume.AvailableBytes,
                string.Empty,
                $"磁盘预检通过；required={required}, available={volume.AvailableBytes}, payload={payloadBytes}, source={volume.Source}");
        }
    }
}
