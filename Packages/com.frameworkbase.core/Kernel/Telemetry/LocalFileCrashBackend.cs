using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using Cysharp.Threading.Tasks;
using Framework.Http;
using Framework.Serialization;
using Framework.Storage;

namespace Framework.Core.Telemetry
{
    /// <summary>
    /// 默认崩溃后端：托管异常本地落盘（JSON Lines，崩溃安全），下次启动 HTTP 上报到
    /// <c>AppConfig.CrashReportUrl</c>。没接厂商 SDK 时的兜底——<b>只能覆盖托管异常</b>，
    /// 原生致命崩溃（SIGSEGV / OOM / ANR）需接入厂商扩展包（见 <see cref="ICrashBackend"/>）。
    /// <see cref="SetUser"/> / <see cref="SetCustomKey"/> / <see cref="LeaveBreadcrumb"/> 设置的
    /// 归因字段会一并写进每条记录。
    /// </summary>
    public sealed class LocalFileCrashBackend : ICrashBackend
    {
        /// <summary>本地崩溃记录文件名（JSON Lines：每行一条，追加写）。</summary>
        private const string LocalFileName = "crash_reports.jsonl";

        /// <summary>
        /// 上报快照文件名：活动文件改名到此后再上传，上传期间新产生的崩溃记录写进重建的活动文件。
        /// 上报失败或进程中途被杀时快照留在盘上，下次上报把它连同新记录一起送出。
        /// <c>PrivacyCompliance</c> 的 RTBF 抹除须一并删除本文件。
        /// </summary>
        private const string UploadSnapshotFileName = LocalFileName + ".uploading";

        /// <summary>单次会话最多记录条数：异常风暴（每帧抛错）时避免无限写盘。</summary>
        private const int MaxRecordsPerSession = 50;

        /// <summary>本地文件体积上限（字节）：超限删除重建，旧崩溃让位于新崩溃。</summary>
        private const long MaxLocalFileBytes = 1 * 1024 * 1024;

        /// <summary>面包屑保留条数上限（超出丢最旧）。</summary>
        private const int MaxBreadcrumbs = 20;

        /// <summary>上报请求超时（秒）。</summary>
        private const int UploadTimeoutSeconds = 15;

        /// <summary>上报响应体字节上限。只需要状态码，正常响应是几十字节量级。</summary>
        private const int MaxUploadResponseBytes = 64 * 1024;

        /// <summary>写文件 + 归因上下文锁（回调可能来自任意线程）。</summary>
        private readonly object _writeLock = new object();

        private string _filePath;
        private string _snapshotPath;
        private string _appVersion;
        private string _buildType;
        private string _userId = string.Empty;
        private int _sessionRecordCount;

        /// <summary>上报在途标志（0=空闲）。并发进入会让两次上报读到同一份快照，把同样的记录送两遍。</summary>
        private int _flushing;

        private readonly Dictionary<string, string> _customKeys = new Dictionary<string, string>();
        private readonly Queue<string> _breadcrumbs = new Queue<string>();

        /// <inheritdoc />
        public string Name => "local-file";

        /// <inheritdoc />
        public void Install(in CrashSessionInfo session)
        {
            _appVersion = session.AppVersion;
            _buildType = session.BuildType;
            // persistentDataPath 由会话在主线程取好传入，回调线程直接用。
            bool hasRoot = !string.IsNullOrEmpty(session.PersistentDataPath);
            _filePath = hasRoot ? Path.Combine(session.PersistentDataPath, LocalFileName) : null;
            _snapshotPath = hasRoot ? Path.Combine(session.PersistentDataPath, UploadSnapshotFileName) : null;
        }

        /// <inheritdoc />
        public void SetUser(string userId)
        {
            lock (_writeLock) _userId = userId ?? string.Empty;
        }

        /// <inheritdoc />
        public void SetCustomKey(string key, string value)
        {
            if (string.IsNullOrEmpty(key)) return;
            lock (_writeLock) _customKeys[key] = value ?? string.Empty;
        }

        /// <inheritdoc />
        public void LeaveBreadcrumb(string message)
        {
            if (string.IsNullOrEmpty(message)) return;
            lock (_writeLock)
            {
                _breadcrumbs.Enqueue(message);
                while (_breadcrumbs.Count > MaxBreadcrumbs) _breadcrumbs.Dequeue();
            }
        }

        /// <inheritdoc />
        public void RecordManagedException(in ManagedExceptionInfo error)
        {
            if (_sessionRecordCount >= MaxRecordsPerSession || string.IsNullOrEmpty(_filePath)) return;

            lock (_writeLock)
            {
                if (_sessionRecordCount >= MaxRecordsPerSession) return;
                _sessionRecordCount++;

                var record = new CrashRecord
                {
                    Timestamp = error.TimestampUnixSeconds,
                    Version = _appVersion,
                    BuildType = _buildType,
                    UserId = _userId,
                    Message = error.Message,
                    StackTrace = error.StackTrace,
                    Breadcrumbs = _breadcrumbs.Count > 0 ? string.Join(" > ", _breadcrumbs) : string.Empty,
                    CustomKeys = FlattenCustomKeys(),
                };

                try
                {
                    if (FileStorages.Shared.GetFileSize(_filePath) > MaxLocalFileBytes)
                    {
                        // 超限重建：新崩溃比旧崩溃更有排查价值。
                        FileStorages.Shared.TryDeleteFile(_filePath);
                    }

                    FileStorages.Shared.AppendText(_filePath, JsonSerializers.Shared.ToJson(record) + "\n");
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // 崩溃记录本身绝不能再抛异常影响主流程；写盘失败只能放弃本条。
                }
            }
        }

        /// <summary>
        /// 把本地积压的崩溃记录上报到 <c>AppConfig.CrashReportUrl</c>（HTTP POST，body 为 JSON Lines）。
        /// URL 为空或无积压时直接返回 false。
        /// <para>
        /// 上传前先把活动文件轮转成独立快照：上传的是快照，成功后也只删快照。上传期间新产生的崩溃记录
        /// 写进重建的活动文件，因而不会被这次清理带走。失败则保留快照，下次上报连同新记录一起重试。
        /// </para>
        /// </summary>
        /// <returns>本次确有内容上报且服务端返回 2xx 时为 true。</returns>
        public async UniTask<bool> TryFlushPendingAsync()
        {
            string uploadUrl = AppConfig.Load()?.CrashReportUrl;
            if (string.IsNullOrWhiteSpace(uploadUrl) || string.IsNullOrEmpty(_filePath))
                return false;

            // 同一时刻只允许一次上报在途：并发进入会让两次上报读到同一份快照，把同样的记录送两遍。
            if (Interlocked.CompareExchange(ref _flushing, 1, 0) != 0)
                return false;

            try
            {
                string payload = null;
                try
                {
                    // 轮转与读取都在写锁内完成：读取若放到锁外，可能与其它线程的 AppendText 交错读到半行。
                    lock (_writeLock)
                    {
                        if (!TryRotatePendingSnapshot())
                            return false;
                        payload = FileStorages.Shared.ReadText(_snapshotPath);
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    GameLog.Warning($"[LocalFileCrashBackend] 读取本地崩溃记录失败：{ex.Message}");
                    return false;
                }

                if (string.IsNullOrEmpty(payload))
                {
                    // 空快照没有上报价值，直接清掉，免得每次启动都来轮转一遍。
                    lock (_writeLock) FileStorages.Shared.TryDeleteFile(_snapshotPath);
                    return false;
                }

                try
                {
                    // 与埋点同一签名契约：已登录附签名头，否则按未签名请求发送（服务端从严限流通道）。
                    // 上报只关心状态码，响应体封顶：采集端异常时不该把一个巨大响应读进内存。
                    HttpRequest request = HttpRequest
                        .Post(uploadUrl, Encoding.UTF8.GetBytes(payload), "application/x-ndjson")
                        .WithTimeout(UploadTimeoutSeconds)
                        .WithMaxResponseBytes(MaxUploadResponseBytes);
                    TelemetryRequestSigner.TrySign(request);
                    HttpResponse response = await HttpClients.Shared.SendAsync(request);

                    if (!response.Succeeded)
                    {
                        GameLog.Warning($"[LocalFileCrashBackend] 崩溃记录上报失败（快照保留下次重试）：{response.Error}");
                        return false;
                    }

                    lock (_writeLock)
                    {
                        FileStorages.Shared.TryDeleteFile(_snapshotPath);
                    }

                    GameLog.Log("[LocalFileCrashBackend] 积压崩溃记录已上报并清理");
                    return true;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    GameLog.Warning($"[LocalFileCrashBackend] 崩溃记录上报异常（快照保留下次重试）：{ex.Message}");
                    return false;
                }
            }
            finally
            {
                Interlocked.Exchange(ref _flushing, 0);
            }
        }

        /// <summary>
        /// 把待上报内容归拢到独立快照。调用方须持有 <see cref="_writeLock"/>。
        /// <para>
        /// 活动文件改名成快照后，本次上报的内容就固定了，新崩溃会写进重建的活动文件——
        /// 这正是"上报成功后删掉整个活动文件"会丢记录的地方。
        /// 若上一轮失败或进程中途被杀留下了快照，则把活动文件的内容并到快照尾部：
        /// 快照里的记录更早，顺序天然正确，也避免快照被覆盖丢失。
        /// </para>
        /// </summary>
        /// <returns>存在可上报内容时返回 true。</returns>
        private bool TryRotatePendingSnapshot()
        {
            bool snapshotExists = FileStorages.Shared.FileExists(_snapshotPath);
            bool activeExists = FileStorages.Shared.FileExists(_filePath);

            if (!snapshotExists)
            {
                if (!activeExists)
                    return false;

                FileStorages.Shared.MoveFile(_filePath, _snapshotPath);
                return true;
            }

            if (activeExists)
            {
                FileStorages.Shared.AppendText(_snapshotPath, FileStorages.Shared.ReadText(_filePath));
                FileStorages.Shared.TryDeleteFile(_filePath);
            }

            return true;
        }

        /// <summary>把当前自定义键拍平成 <c>k=v;k2=v2</c> 文本（调用方须持 <see cref="_writeLock"/>）。</summary>
        private string FlattenCustomKeys()
        {
            if (_customKeys.Count == 0) return string.Empty;

            var sb = new StringBuilder();
            foreach (KeyValuePair<string, string> kv in _customKeys)
            {
                if (sb.Length > 0) sb.Append(';');
                sb.Append(kv.Key).Append('=').Append(kv.Value);
            }
            return sb.ToString();
        }

        /// <summary>单条崩溃记录（JSON 序列化载体）。</summary>
        [Serializable]
        private struct CrashRecord
        {
            /// <summary>发生时间（Unix 秒，UTC）。</summary>
            public long Timestamp;

            /// <summary>应用版本。</summary>
            public string Version;

            /// <summary>构建类型（release / development / editor）。</summary>
            public string BuildType;

            /// <summary>归因用户 ID（未设置时空串）。</summary>
            public string UserId;

            /// <summary>异常消息（首行）。</summary>
            public string Message;

            /// <summary>堆栈（IL2CPP 下为托管映射栈）。</summary>
            public string StackTrace;

            /// <summary>面包屑路径（<c>a &gt; b &gt; c</c>）。</summary>
            public string Breadcrumbs;

            /// <summary>自定义键（<c>k=v;k2=v2</c>）。</summary>
            public string CustomKeys;
        }
    }
}
