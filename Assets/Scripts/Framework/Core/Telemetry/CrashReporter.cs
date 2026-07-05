using System;
using System.IO;
using Cysharp.Threading.Tasks;
using UnityEngine;
using UnityEngine.Networking;

namespace Framework.Core.Telemetry
{
    /// <summary>
    /// 崩溃/未捕获异常回捞器：捕获运行时 <see cref="LogType.Exception"/> 级日志，
    /// 就地落盘（JSON Lines，崩溃安全），下次启动时尝试上报——这是上线后判断
    /// 「该 bug 能否用热更修复」的一手依据。
    /// </summary>
    /// <remarks>
    /// <para>生命周期：<see cref="Install"/> 在 GameEntry 初始化最早期挂接（越早越能兜住启动期异常）；
    /// <see cref="TryUploadPendingAsync"/> 在框架就绪后调用，上报端点未配置时静默保留本地文件。</para>
    /// <para>线程安全：<c>logMessageReceivedThreaded</c> 可能在任意线程触发，写文件在锁内完成；
    /// 每会话条数与文件体积均有上限，异常风暴不会撑爆存储或卡死主线程。</para>
    /// </remarks>
    public static class CrashReporter
    {
        /// <summary>本地崩溃记录文件名（JSON Lines：每行一条，追加写）。</summary>
        private const string LocalFileName = "crash_reports.jsonl";

        /// <summary>单次会话最多记录条数：异常风暴（每帧抛错）时避免无限写盘。</summary>
        private const int MaxRecordsPerSession = 50;

        /// <summary>本地文件体积上限（字节）：超限删除重建，旧崩溃让位于新崩溃。</summary>
        private const long MaxLocalFileBytes = 1 * 1024 * 1024;

        /// <summary>写文件锁（回调可能来自任意线程）。</summary>
        private static readonly object _writeLock = new object();

        /// <summary>崩溃记录文件完整路径（Install 时在主线程取好，回调线程直接用）。</summary>
        private static string _filePath;

        /// <summary>应用版本（Install 时在主线程取好）。</summary>
        private static string _appVersion;

        /// <summary>本会话已记录条数。</summary>
        private static int _sessionRecordCount;

        /// <summary>是否已挂接（幂等保护）。</summary>
        private static bool _installed;

        /// <summary>单条崩溃记录（JsonUtility 序列化载体）。</summary>
        [Serializable]
        private struct CrashRecord
        {
            /// <summary>发生时间（Unix 秒，UTC）。</summary>
            public long Timestamp;

            /// <summary>应用版本。</summary>
            public string Version;

            /// <summary>异常消息（首行）。</summary>
            public string Message;

            /// <summary>堆栈（IL2CPP 下为托管映射栈）。</summary>
            public string StackTrace;
        }

        /// <summary>
        /// 挂接未捕获异常监听（幂等）。应在框架初始化最早期调用。
        /// </summary>
        public static void Install()
        {
            if (_installed) return;
            _installed = true;

            // persistentDataPath / version 只能在主线程访问，先取好供回调线程使用。
            _filePath = Path.Combine(Application.persistentDataPath, LocalFileName);
            _appVersion = Application.version;

            Application.logMessageReceivedThreaded += OnLogMessage;
        }

        /// <summary>
        /// 日志回调：仅处理 Exception 级（Error 级噪声大且通常已有业务日志），过滤后落盘。
        /// </summary>
        private static void OnLogMessage(string condition, string stackTrace, LogType type)
        {
            if (type != LogType.Exception) return;
            if (_sessionRecordCount >= MaxRecordsPerSession) return;

            var record = new CrashRecord
            {
                Timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                Version = _appVersion,
                Message = condition,
                StackTrace = stackTrace,
            };

            lock (_writeLock)
            {
                if (_sessionRecordCount >= MaxRecordsPerSession) return;
                _sessionRecordCount++;
                try
                {
                    var fileInfo = new FileInfo(_filePath);
                    if (fileInfo.Exists && fileInfo.Length > MaxLocalFileBytes)
                    {
                        // 超限重建：新崩溃比旧崩溃更有排查价值。
                        fileInfo.Delete();
                    }

                    File.AppendAllText(_filePath, JsonUtility.ToJson(record) + "\n");
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // 崩溃记录本身绝不能再抛异常影响主流程；写盘失败只能放弃本条。
                }
            }
        }

        /// <summary>
        /// 尝试把本地积压的崩溃记录上报到 <paramref name="uploadUrl"/>（HTTP POST，body 为 JSON Lines 文本）。
        /// 成功（2xx）后删除本地文件；失败保留、下次启动重试。URL 为空或无积压时直接返回。
        /// </summary>
        /// <param name="uploadUrl">崩溃上报端点（AppConfig.CrashReportUrl）；空串表示未配置、仅本地缓存。</param>
        /// <returns>是否完成了一次成功上报。</returns>
        public static async UniTask<bool> TryUploadPendingAsync(string uploadUrl)
        {
            if (string.IsNullOrWhiteSpace(uploadUrl) || string.IsNullOrEmpty(_filePath))
                return false;

            string payload;
            try
            {
                if (!File.Exists(_filePath))
                    return false;
                payload = File.ReadAllText(_filePath);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Logger.Warning($"[CrashReporter] 读取本地崩溃记录失败：{ex.Message}");
                return false;
            }

            if (string.IsNullOrEmpty(payload))
                return false;

            try
            {
                using (var request = new UnityWebRequest(uploadUrl, UnityWebRequest.kHttpVerbPOST))
                {
                    request.uploadHandler = new UploadHandlerRaw(System.Text.Encoding.UTF8.GetBytes(payload));
                    request.downloadHandler = new DownloadHandlerBuffer();
                    request.SetRequestHeader("Content-Type", "application/x-ndjson");
                    request.timeout = 15;
                    await request.SendWebRequest();

                    if (request.result != UnityWebRequest.Result.Success)
                    {
                        Logger.Warning($"[CrashReporter] 崩溃记录上报失败（保留本地下次重试）：{request.error}");
                        return false;
                    }
                }

                lock (_writeLock)
                {
                    File.Delete(_filePath);
                }

                Logger.Log("[CrashReporter] 积压崩溃记录已上报并清理");
                return true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or UnityWebRequestException)
            {
                Logger.Warning($"[CrashReporter] 崩溃记录上报异常（保留本地下次重试）：{ex.Message}");
                return false;
            }
        }
    }
}
