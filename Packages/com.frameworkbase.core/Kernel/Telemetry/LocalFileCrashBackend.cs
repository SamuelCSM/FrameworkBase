using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
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
    /// 落盘行为与旧版 <c>CrashReporter</c> 等价，另把 <see cref="SetUser"/> / <see cref="SetCustomKey"/> /
    /// <see cref="LeaveBreadcrumb"/> 的归因字段一并写进记录。
    /// </summary>
    public sealed class LocalFileCrashBackend : ICrashBackend
    {
        /// <summary>本地崩溃记录文件名（JSON Lines：每行一条，追加写）。</summary>
        private const string LocalFileName = "crash_reports.jsonl";

        /// <summary>单次会话最多记录条数：异常风暴（每帧抛错）时避免无限写盘。</summary>
        private const int MaxRecordsPerSession = 50;

        /// <summary>本地文件体积上限（字节）：超限删除重建，旧崩溃让位于新崩溃。</summary>
        private const long MaxLocalFileBytes = 1 * 1024 * 1024;

        /// <summary>面包屑保留条数上限（超出丢最旧）。</summary>
        private const int MaxBreadcrumbs = 20;

        /// <summary>上报请求超时（秒）。</summary>
        private const int UploadTimeoutSeconds = 15;

        /// <summary>写文件 + 归因上下文锁（回调可能来自任意线程）。</summary>
        private readonly object _writeLock = new object();

        private string _filePath;
        private string _appVersion;
        private string _buildType;
        private string _userId = string.Empty;
        private int _sessionRecordCount;

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
            _filePath = string.IsNullOrEmpty(session.PersistentDataPath)
                ? null
                : Path.Combine(session.PersistentDataPath, LocalFileName);
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
        /// 成功（2xx）后删除本地文件；失败保留、下次启动重试。URL 为空或无积压时直接返回 false。
        /// </summary>
        public async UniTask<bool> TryFlushPendingAsync()
        {
            string uploadUrl = AppConfig.Load()?.CrashReportUrl;
            if (string.IsNullOrWhiteSpace(uploadUrl) || string.IsNullOrEmpty(_filePath))
                return false;

            string payload;
            try
            {
                if (!FileStorages.Shared.FileExists(_filePath))
                    return false;
                payload = FileStorages.Shared.ReadText(_filePath);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                GameLog.Warning($"[LocalFileCrashBackend] 读取本地崩溃记录失败：{ex.Message}");
                return false;
            }

            if (string.IsNullOrEmpty(payload))
                return false;

            try
            {
                HttpResponse response = await HttpClients.Shared.PostTextAsync(
                    uploadUrl, payload, "application/x-ndjson", UploadTimeoutSeconds);

                if (!response.Succeeded)
                {
                    GameLog.Warning($"[LocalFileCrashBackend] 崩溃记录上报失败（保留本地下次重试）：{response.Error}");
                    return false;
                }

                lock (_writeLock)
                {
                    FileStorages.Shared.DeleteFile(_filePath);
                }

                GameLog.Log("[LocalFileCrashBackend] 积压崩溃记录已上报并清理");
                return true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                GameLog.Warning($"[LocalFileCrashBackend] 崩溃记录上报异常（保留本地下次重试）：{ex.Message}");
                return false;
            }
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
