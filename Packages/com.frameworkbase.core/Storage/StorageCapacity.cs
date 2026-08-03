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
    /// Player 运行时卷空间查询。先走本平台的原生实现（Windows 用 kernel32 的 <c>GetDiskFreeSpaceEx</c>、
    /// Android 用 StatFs、iOS/macOS/Linux 用 <c>statvfs</c>），原生不可用时回退 <see cref="DriveInfo"/>；
    /// 两条都失败才返回 Unknown，由上层按失败关闭策略中止。
    /// <para>
    /// 真机不能只靠 <see cref="DriveInfo"/>：IL2CPP 的 mscorlib 把 <c>DriveInfo::GetDriveFormat</c>
    /// 桩成 "Unsupported internal call" 并直接抛，而 <c>DriveInfo</c> 取任何属性都会触到它。
    /// 热更装载前的空间预检因此在 IL2CPP 包上得 Unknown 而一律中止——即真机热更装不上。
    /// 编辑器是 Mono，看不到此事，故编辑器与未覆盖的平台仍走 <see cref="DriveInfo"/>。
    /// </para>
    /// </summary>
    public sealed class SystemStorageCapacityProvider : IStorageCapacityProvider
    {
#if UNITY_STANDALONE_WIN && !UNITY_EDITOR
        /// <summary>Windows 卷空闲空间查询；取调用方配额可用字节（受配额限制时小于卷剩余）。</summary>
        [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true, CharSet = System.Runtime.InteropServices.CharSet.Unicode, EntryPoint = "GetDiskFreeSpaceExW")]
        [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
        private static extern bool GetDiskFreeSpaceEx(
            string lpDirectoryName,
            out ulong lpFreeBytesAvailableToCaller,
            out ulong lpTotalNumberOfBytes,
            out ulong lpTotalNumberOfFreeBytes);
#endif

        /// <summary>
        /// 查询目标路径所在卷的可用空间：原生实现优先，失败则回退 <see cref="DriveInfo"/>。
        /// </summary>
        /// <param name="path">安装目标路径；可以尚未创建，实现会上溯到已存在的祖先目录。</param>
        /// <returns>两条路径都失败时为 Unknown，错误信息里保留两者的原因以便排障。</returns>
        public StorageVolumeSnapshot Query(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
                return StorageVolumeSnapshot.Unknown("目标路径为空。");

            StorageVolumeSnapshot native = QueryPlatformNative(path);
            if (native.IsKnown)
                return native;

            StorageVolumeSnapshot managed = QueryViaDriveInfo(path);
            if (managed.IsKnown)
                return managed;

            return StorageVolumeSnapshot.Unknown($"{native.Error}；DriveInfo 回退同样失败：{managed.Error}");
        }

        /// <summary>本平台的原生卷空间查询；无原生实现或查询失败时返回 Unknown 交由上层回退。</summary>
        private static StorageVolumeSnapshot QueryPlatformNative(string path)
        {
            // UNITY_EDITOR 必须排在最前：Windows 编辑器同样定义 UNITY_STANDALONE_WIN，
            // 而编辑器是 Mono、DriveInfo 可用，不必引入原生调用。
#if UNITY_EDITOR
            return StorageVolumeSnapshot.Unknown("编辑器不走原生实现");
#elif UNITY_STANDALONE_WIN
            try
            {
                // GetDiskFreeSpaceEx 要求目录存在；安装目标可能尚未创建，故上溯到最近的已存在祖先。
                string probe = ResolveExistingAncestor(path);
                if (string.IsNullOrEmpty(probe))
                    return StorageVolumeSnapshot.Unknown($"无法定位已存在的祖先目录：{path}");

                if (!GetDiskFreeSpaceEx(probe, out ulong freeToCaller, out _, out _))
                {
                    int err = System.Runtime.InteropServices.Marshal.GetLastWin32Error();
                    return StorageVolumeSnapshot.Unknown($"GetDiskFreeSpaceEx 失败：Win32Error={err}, path={probe}");
                }

                return StorageVolumeSnapshot.Known(
                    (long)Math.Min(freeToCaller, long.MaxValue), "kernel32.GetDiskFreeSpaceEx");
            }
            catch (Exception winEx)
            {
                return StorageVolumeSnapshot.Unknown($"Windows 卷空间查询失败：{winEx.Message}");
            }
#elif UNITY_ANDROID
            try
            {
                // StatFs 同样要求路径存在：首次热更前安装目标尚未创建，直接传会抛 IllegalArgumentException。
                string probe = ResolveExistingAncestor(path);
                if (string.IsNullOrEmpty(probe))
                    return StorageVolumeSnapshot.Unknown($"无法定位已存在的祖先目录：{path}");

                using (var statFs = new AndroidJavaObject("android.os.StatFs", probe))
                {
                    long available = statFs.Call<long>("getAvailableBytes");
                    return StorageVolumeSnapshot.Known(available, "Android.StatFs");
                }
            }
            catch (Exception androidEx)
            {
                return StorageVolumeSnapshot.Unknown($"Android StatFs 查询失败：{androidEx.Message}");
            }
#elif UNITY_IOS || UNITY_STANDALONE_OSX || UNITY_STANDALONE_LINUX
            return UnixStatvfs.QueryAvailableBytes(path);
#else
            return StorageVolumeSnapshot.Unknown("本平台无原生卷空间实现");
#endif
        }

        /// <summary>
        /// 托管回退实现。编辑器与未覆盖平台的正路；IL2CPP 下可能因 icall 不受支持而抛，收口为 Unknown。
        /// </summary>
        private static StorageVolumeSnapshot QueryViaDriveInfo(string path)
        {
            try
            {
                string fullPath = Path.GetFullPath(path);
                string root = Path.GetPathRoot(fullPath);
                if (string.IsNullOrEmpty(root))
                    return StorageVolumeSnapshot.Unknown($"无法解析目标卷：{fullPath}");

                var drive = new DriveInfo(root);
                // 刻意不查 DriveInfo.IsReady：它内部要取卷格式（GetDriveFormat），该 icall 在 IL2CPP
                // 下不受支持并直接抛出。卷真未就绪时 AvailableFreeSpace 自身同样会抛，
                // 由下方 catch 统一收口为 Unknown，判定语义不丢。
                return StorageVolumeSnapshot.Known(drive.AvailableFreeSpace, "System.IO.DriveInfo");
            }
            catch (Exception ex)
            {
                return StorageVolumeSnapshot.Unknown($"卷空间查询失败：{ex.Message}");
            }
        }

        /// <summary>
        /// 上溯到最近的已存在祖先目录——原生查询接口普遍要求目录真实存在，而安装目标常常尚未创建。
        /// </summary>
        /// <param name="path">目标路径，可不存在。</param>
        /// <returns>最近的已存在祖先目录绝对路径；一路上溯到根都不存在时为空串。</returns>
        internal static string ResolveExistingAncestor(string path)
        {
            string probe = Path.GetFullPath(path);
            while (!string.IsNullOrEmpty(probe) && !Directory.Exists(probe))
                probe = Path.GetDirectoryName(probe);

            return probe ?? string.Empty;
        }
    }

    /// <summary>
    /// Unix 系（iOS / macOS / Linux）的 <c>statvfs</c> 卷空间查询。
    /// <para>
    /// 刻意不声明托管结构体接原生写入：<c>statvfs</c> 的字段宽度按平台不同（Darwin 的块计数是 32 位，
    /// Linux LP64 是 64 位），一个结构体套两个平台会错位，声明小了还会被原生写越界。
    /// 改为传一块足够大的字节缓冲区，再按平台偏移解读。
    /// </para>
    /// <para>
    /// 解读与换算部分无条件编译，可被 EditMode 测试覆盖——P/Invoke 只在真机成立，
    /// 但偏移和溢出处理不该跟着一起失去覆盖，那正是这类代码最容易错的地方。
    /// </para>
    /// </summary>
    internal static class UnixStatvfs
    {
        /// <summary>statvfs 结构体的字段布局族。</summary>
        internal enum Layout
        {
            /// <summary>Linux LP64：块计数为 64 位。</summary>
            LinuxLp64,
            /// <summary>Darwin（macOS / iOS）64 位：<c>fsblkcnt_t</c> 是 32 位无符号整数。</summary>
            Darwin64,
        }

        /// <summary>缓冲区尺寸取远大于两个平台的结构体（Linux 112 字节、Darwin 64 字节），避免原生写越界。</summary>
        private const int BufferBytes = 256;

#if (UNITY_IOS || UNITY_STANDALONE_OSX || UNITY_STANDALONE_LINUX) && !UNITY_EDITOR
#if UNITY_IOS
        // iOS 把系统库静态链进主二进制，动态库名不可用，只能走 __Internal。
        private const string LibC = "__Internal";
#elif UNITY_STANDALONE_LINUX
        // glibc 的 libc.so 是链接脚本而非 ELF，dlopen 不了，必须点名 so 版本。
        private const string LibC = "libc.so.6";
#else
        private const string LibC = "libc";
#endif

        /// <summary>路径与结果都按字节数组传，绕开默认字符串封送在各平台上对非 ASCII 路径的分歧。</summary>
        [System.Runtime.InteropServices.DllImport(LibC, EntryPoint = "statvfs", SetLastError = true)]
        private static extern int statvfs(byte[] path, byte[] buffer);

        /// <summary>本平台的结构体布局。</summary>
#if UNITY_STANDALONE_LINUX
        private const Layout PlatformLayout = Layout.LinuxLp64;
#else
        private const Layout PlatformLayout = Layout.Darwin64;
#endif
#endif

        /// <summary>
        /// 查询目标路径所在卷的可用空间。目标目录可不存在，实现会上溯到已存在的祖先。
        /// </summary>
        /// <param name="path">安装目标路径。</param>
        /// <returns>查询失败或本平台未编入原生实现时为 Unknown。</returns>
        public static StorageVolumeSnapshot QueryAvailableBytes(string path)
        {
#if (UNITY_IOS || UNITY_STANDALONE_OSX || UNITY_STANDALONE_LINUX) && !UNITY_EDITOR
            try
            {
                string probe = SystemStorageCapacityProvider.ResolveExistingAncestor(path);
                if (string.IsNullOrEmpty(probe))
                    return StorageVolumeSnapshot.Unknown($"无法定位已存在的祖先目录：{path}");

                // C 字符串要自带结尾 NUL，Encoding.UTF8.GetBytes 不会补。
                byte[] pathBytes = System.Text.Encoding.UTF8.GetBytes(probe + "\0");
                var buffer = new byte[BufferBytes];
                if (statvfs(pathBytes, buffer) != 0)
                {
                    int err = System.Runtime.InteropServices.Marshal.GetLastWin32Error(); // Unix 下取 errno
                    return StorageVolumeSnapshot.Unknown($"statvfs 失败：errno={err}, path={probe}");
                }

                if (!TryReadAvailableBytes(buffer, PlatformLayout, out long available, out string error))
                    return StorageVolumeSnapshot.Unknown($"statvfs 结果不可用：{error}");

                return StorageVolumeSnapshot.Known(available, "libc.statvfs");
            }
            catch (Exception ex)
            {
                // 找不到符号/库（DllNotFoundException、EntryPointNotFoundException）也走这里，
                // 由调用方回退 DriveInfo，不让预检因一次绑定失败直接判死。
                return StorageVolumeSnapshot.Unknown($"statvfs 查询失败：{ex.Message}");
            }
#else
            return StorageVolumeSnapshot.Unknown("本平台未编入 statvfs 实现");
#endif
        }

        /// <summary>
        /// 从 <c>statvfs</c> 写回的缓冲区解读可用字节数 = <c>f_bavail × f_frsize</c>。
        /// 取 <c>f_bavail</c>（非特权用户可用）而非 <c>f_bfree</c>：后者含保留给 root 的块，会高估。
        /// </summary>
        /// <param name="buffer">原生写回的缓冲区。</param>
        /// <param name="layout">本平台的字段布局。</param>
        /// <param name="availableBytes">可用字节数；超出 <see cref="long"/> 时饱和取上限。</param>
        /// <param name="error">失败原因；成功时为空串。</param>
        /// <returns>解读成功返回 true。</returns>
        internal static bool TryReadAvailableBytes(byte[] buffer, Layout layout, out long availableBytes, out string error)
        {
            availableBytes = 0;
            error = string.Empty;

            // 两个平台的前两个字段都是 unsigned long（LP64 下 8 字节）：f_bsize、f_frsize。
            // 差异从第三个字段（块计数）开始：Linux 是 64 位，Darwin 是 32 位。
            int requiredBytes = layout == Layout.Darwin64 ? 28 : 40;
            if (buffer == null || buffer.Length < requiredBytes)
            {
                error = $"缓冲区不足 {requiredBytes} 字节";
                return false;
            }

            ulong blockSize = BitConverter.ToUInt64(buffer, 0);
            ulong fragmentSize = BitConverter.ToUInt64(buffer, 8);
            ulong availableBlocks = layout == Layout.Darwin64
                ? BitConverter.ToUInt32(buffer, 24)
                : BitConverter.ToUInt64(buffer, 32);

            // 有文件系统把 f_frsize 报 0，此时块大小以 f_bsize 为准。
            ulong unit = fragmentSize != 0 ? fragmentSize : blockSize;
            if (unit == 0)
            {
                error = "f_frsize 与 f_bsize 同时为 0";
                return false;
            }

            if (availableBlocks == 0)
                return true;

            // 溢出即饱和：卷比 long 还大不该表现为"空间为负"。
            ulong total = availableBlocks > ulong.MaxValue / unit ? ulong.MaxValue : availableBlocks * unit;
            availableBytes = total > long.MaxValue ? long.MaxValue : (long)total;
            return true;
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
