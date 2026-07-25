using System;
using System.IO;
using System.Threading;
using Cysharp.Threading.Tasks;
using Framework.Storage;
using UnityEngine.Networking;

namespace Framework.HotUpdate
{
    /// <summary>
    /// 面向移动端热更新文件的流式下载器。
    /// <para>
    /// 网络响应始终先写入同目录的 <c>.download</c> 临时文件，成功后再替换正式路径，
    /// 避免进程被杀、磁盘写满或网络中断时留下被误判为可安装的半文件。断点续传只有在服务端明确返回
    /// 206 Partial Content 时才允许追加；若服务端忽略 Range 并返回 200，则必须按完整文件整体替换。
    /// </para>
    /// <para>
    /// 本类型只负责可靠传输，不把 HTTP 成功等同于文件可信。文件长度与 SHA-256 必须由
    /// HotUpdateManager 或 HotUpdateSlotManager 在提交事务槽之前基于已验签清单再次校验。
    /// </para>
    /// </summary>
    internal interface IFileDownloadTransport
    {
        UniTask<bool> DownloadFileAsync(
            string url,
            string savePath,
            Action<float> onProgress,
            bool forceRefresh,
            CancellationToken cancellationToken);
    }

    public sealed class PatchDownloader : IFileDownloadTransport
    {
        private UnityWebRequest _currentRequest;
        private volatile bool _isCancelled;
        private long _downloadedSize;
        private long _totalSize;

        /// <summary>
        /// 首次请求失败后的最大重试次数；总尝试次数为该值加一，负数按零处理。
        /// </summary>
        public int MaxRetryCount { get; set; } = 3;

        /// <summary>
        /// 重试基础延迟（秒）。实际延迟会加入小幅随机抖动，避免大量客户端在服务恢复瞬间同步重试。
        /// </summary>
        public float RetryDelay { get; set; } = 2f;

        /// <summary>
        /// 单次 UnityWebRequest 超时上限（秒），小于 1 的配置按 1 秒处理。
        /// </summary>
        public int RequestTimeoutSeconds { get; set; } = 30;

        /// <summary>
        /// 最近一次成功下载后正式文件的已下载字节数。
        /// </summary>
        public long DownloadedSize => _downloadedSize;

        /// <summary>
        /// 最近一次成功下载后已知的总字节数。当前实现未信任 Content-Length，成功前仅作本地统计。
        /// </summary>
        public long TotalSize => _totalSize;

        /// <summary>
        /// 下载文件到指定正式路径，支持有限重试、显式取消及受控的 HTTP Range 续传。
        /// </summary>
        /// <param name="url">补丁下载 URL；正式环境应在清单准入阶段验证 HTTPS 与受信域名。</param>
        /// <param name="savePath">下载成功后的正式文件路径。</param>
        /// <param name="onProgress">单次 HTTP 请求进度回调，取值通常为 0～1；重试时可能重新从较小值开始。</param>
        /// <param name="forceRefresh">为 true 时先删除旧正式文件，首次尝试禁止基于旧文件续传。</param>
        /// <param name="cancellationToken">调用方生命周期取消令牌；取消后不再继续重试。</param>
        /// <returns>文件已完整落到正式路径时返回 <see langword="true"/>；网络失败或主动取消返回 <see langword="false"/>。</returns>
        public async UniTask<bool> DownloadFileAsync(
            string url,
            string savePath,
            Action<float> onProgress = null,
            bool forceRefresh = false,
            CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(url) || string.IsNullOrWhiteSpace(savePath))
                return false;

            _isCancelled = false;
            string transferPath = savePath + ".download";
            FileStorages.Shared.TryDeleteFile(transferPath);
            if (forceRefresh)
                FileStorages.Shared.TryDeleteFile(savePath);

            int attempts = Math.Max(1, MaxRetryCount + 1);
            for (int attempt = 1; attempt <= attempts; attempt++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                bool success = await DownloadAttemptAsync(
                    url,
                    savePath,
                    transferPath,
                    onProgress,
                    cancellationToken,
                    allowRange: !forceRefresh || attempt > 1);
                if (success) return true;
                if (_isCancelled || cancellationToken.IsCancellationRequested) return false;

                if (attempt < attempts)
                {
                    // 在基础延迟上加入约 ±15% 抖动，降低 CDN 故障恢复或网络切换时的客户端惊群效应。
                    // 用共享随机源而非每次 new Random(TickCount)：同毫秒内重复构造会命中相同种子、抖动退化。
                    double jitter = RandomUtil.NextJitterFactor(0.15);
                    await UniTask.Delay(
                        TimeSpan.FromSeconds(Math.Max(0, RetryDelay) * jitter),
                        cancellationToken: cancellationToken);
                }
            }

            FileStorages.Shared.TryDeleteFile(transferPath);
            return false;
        }

        /// <summary>
        /// 执行一次 HTTP 下载尝试，并严格区分 200、206 与 416 对本地文件的处理语义。
        /// </summary>
        private async UniTask<bool> DownloadAttemptAsync(
            string url,
            string savePath,
            string transferPath,
            Action<float> onProgress,
            CancellationToken cancellationToken,
            bool allowRange)
        {
            long startPosition = allowRange && File.Exists(savePath) ? new FileInfo(savePath).Length : 0;
            FileStorages.Shared.EnsureParentDirectory(savePath);
            FileStorages.Shared.TryDeleteFile(transferPath);

            try
            {
                using (var request = UnityWebRequest.Get(url))
                {
                    _currentRequest = request;
                    request.timeout = Math.Max(1, RequestTimeoutSeconds);

                    // DownloadHandlerFile 将响应体直接流式写入磁盘，避免大型 DLL 或资源补丁整体进入托管堆。
                    request.downloadHandler = new DownloadHandlerFile(transferPath, false);
                    if (startPosition > 0)
                        request.SetRequestHeader("Range", $"bytes={startPosition}-");

                    UnityWebRequestAsyncOperation operation = request.SendWebRequest();
                    while (!operation.isDone)
                    {
                        if (_isCancelled || cancellationToken.IsCancellationRequested)
                        {
                            request.Abort();
                            FileStorages.Shared.TryDeleteFile(transferPath);
                            return false;
                        }

                        onProgress?.Invoke(operation.progress);
                        await UniTask.Yield();
                    }

                    // 416 表示本地续传基线与服务端对象不再一致。删除旧基线，让下一次重试执行全量下载。
                    if (request.responseCode == 416 && startPosition > 0)
                    {
                        FileStorages.Shared.TryDeleteFile(savePath);
                        FileStorages.Shared.TryDeleteFile(transferPath);
                        return false;
                    }

                    if (request.result != UnityWebRequest.Result.Success)
                    {
                        GameLog.Warning($"[PatchDownloader] 下载失败，HTTP={request.responseCode}：{request.error}");
                        FileStorages.Shared.TryDeleteFile(transferPath);
                        return false;
                    }

                    if (startPosition > 0 && request.responseCode == 206)
                    {
                        // 只有明确的 206 Partial Content 才能把响应体追加到旧文件，防止把完整 200 响应拼接到旧内容尾部。
                        AppendFile(transferPath, savePath);
                        FileStorages.Shared.TryDeleteFile(transferPath);
                    }
                    else
                    {
                        // 未请求续传或服务端忽略 Range 返回 200 时，响应体代表完整对象，必须整体替换旧文件。
                        AtomicMoveReplace(transferPath, savePath);
                    }
                }

                _downloadedSize = FileStorages.Shared.GetFileSize(savePath);
                _totalSize = _downloadedSize;
                onProgress?.Invoke(1f);
                return true;
            }
            catch (OperationCanceledException)
            {
                FileStorages.Shared.TryDeleteFile(transferPath);
                return false;
            }
            catch (Exception ex)
            {
                GameLog.Warning($"[PatchDownloader] 下载过程异常：{ex.Message}");
                FileStorages.Shared.TryDeleteFile(transferPath);
                return false;
            }
            finally
            {
                _currentRequest = null;
            }
        }

        /// <summary>
        /// 将临时响应文件顺序追加到已有续传基线；调用方必须已确认服务端返回 206。
        /// </summary>
        private static void AppendFile(string sourcePath, string destinationPath)
        {
            using (FileStream source = File.OpenRead(sourcePath))
            using (FileStream destination = new FileStream(destinationPath, FileMode.Append, FileAccess.Write, FileShare.Read))
                source.CopyTo(destination);
        }

        /// <summary>
        /// 尽可能使用 <see cref="File.Replace(string,string,string,bool)"/> 原子替换同卷目标；
        /// 平台不支持时退化为删除后移动。正式安装的最终原子边界仍由不可变 staging 槽目录提交保证。
        /// </summary>
        private static void AtomicMoveReplace(string sourcePath, string destinationPath)
        {
            string backup = destinationPath + ".bak";
            FileStorages.Shared.TryDeleteFile(backup);
            if (File.Exists(destinationPath))
            {
                try
                {
                    File.Replace(sourcePath, destinationPath, backup, true);
                    FileStorages.Shared.TryDeleteFile(backup);
                    return;
                }
                catch (PlatformNotSupportedException) { }
                catch (IOException) { }
            }

            FileStorages.Shared.TryDeleteFile(destinationPath);
            File.Move(sourcePath, destinationPath);
        }

        /// <summary>
        /// 主动取消当前下载。该调用可来自其他线程，因此仅设置易失标志并尽力中止当前 UnityWebRequest。
        /// </summary>
        public void CancelDownload()
        {
            _isCancelled = true;
            try { _currentRequest?.Abort(); }
            catch { }
        }

        /// <summary>
        /// 获取最近一次成功下载后的本地文件字节数。
        /// </summary>
        public long GetDownloadedSize() => _downloadedSize;

        /// <summary>
        /// 获取最近一次成功下载后记录的总字节数。
        /// </summary>
        public long GetTotalSize() => _totalSize;
    }
}
