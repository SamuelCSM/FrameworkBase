using System;
using System.IO;
using UnityEngine;
using UnityEngine.Networking;
using Cysharp.Threading.Tasks;

namespace Framework.HotUpdate
{
    /// <summary>
    /// 补丁下载器
    /// 负责从服务器下载补丁文件，支持断点续传和重试机制
    /// </summary>
    public class PatchDownloader
    {
        private UnityWebRequest _currentRequest;
        private bool _isCancelled;
        private long _downloadedSize;
        private long _totalSize;
        private int _maxRetryCount = 3;
        private float _retryDelay = 2.0f;
        
        /// <summary>
        /// 已下载大小
        /// </summary>
        public long DownloadedSize => _downloadedSize;
        
        /// <summary>
        /// 总大小
        /// </summary>
        public long TotalSize => _totalSize;
        
        /// <summary>
        /// 最大重试次数
        /// </summary>
        public int MaxRetryCount
        {
            get => _maxRetryCount;
            set => _maxRetryCount = Math.Max(0, value);
        }
        
        /// <summary>
        /// 重试延迟（秒）
        /// </summary>
        public float RetryDelay
        {
            get => _retryDelay;
            set => _retryDelay = Math.Max(0, value);
        }
        
        /// <summary>
        /// 下载文件（带重试 + 断点续传）
        /// </summary>
        /// <param name="url">下载 URL</param>
        /// <param name="savePath">保存路径</param>
        /// <param name="onProgress">进度回调 0~1</param>
        /// <param name="forceRefresh">
        /// true  = 强制全新下载，下载前删除本地文件（适用于版本号已变的小文件，如 version.json / HotUpdate.dll）
        /// false = 优先断点续传，服务器返回 416 时自动回退全量下载（适用于大资源包）
        /// </param>
        public async UniTask<bool> DownloadFileAsync(
            string url, string savePath,
            Action<float> onProgress = null,
            bool forceRefresh = false)
        {
            // forceRefresh：直接删除本地文件，跳过断点续传判断
            if (forceRefresh && File.Exists(savePath))
            {
                File.Delete(savePath);
                GameLog.Log($"[PatchDownloader] forceRefresh=true，已清除旧文件: {savePath}");
            }

            int retryCount = 0;
            while (retryCount <= _maxRetryCount)
            {
                bool success = await DownloadFileInternalAsync(url, savePath, onProgress);
                if (success || _isCancelled)
                    return success;

                retryCount++;
                if (retryCount <= _maxRetryCount)
                {
                    GameLog.Warning($"[PatchDownloader] 下载失败，{_retryDelay}秒后重试 ({retryCount}/{_maxRetryCount})");
                    await UniTask.Delay(TimeSpan.FromSeconds(_retryDelay));
                }
            }

            GameLog.Error($"[PatchDownloader] 已达到最大重试次数，放弃下载: {url}");
            return false;
        }

        /// <summary>
        /// 下载文件（内部实现）
        /// 支持断点续传，收到 416（Range 超出）时自动清除本地文件并回退全量下载
        /// </summary>
        private async UniTask<bool> DownloadFileInternalAsync(string url, string savePath, Action<float> onProgress)
        {
            _isCancelled = false;
            _downloadedSize = 0;

            try
            {
                GameLog.Log($"[PatchDownloader] 开始下载: {url} -> {savePath}");

                string directory = Path.GetDirectoryName(savePath);
                if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
                    Directory.CreateDirectory(directory);

                // 断点续传：读取本地已有大小
                long startPosition = 0;
                if (File.Exists(savePath))
                {
                    startPosition = new FileInfo(savePath).Length;
                    _downloadedSize = startPosition;
                    GameLog.Log($"[PatchDownloader] 检测到已下载 {startPosition} 字节，尝试断点续传");
                }

                _currentRequest = UnityWebRequest.Get(url);
                if (startPosition > 0)
                    _currentRequest.SetRequestHeader("Range", $"bytes={startPosition}-");

                var operation = _currentRequest.SendWebRequest();
                while (!operation.isDone)
                {
                    if (_isCancelled)
                    {
                        _currentRequest.Abort();
                        GameLog.Warning("[PatchDownloader] 下载已取消");
                        return false;
                    }
                    onProgress?.Invoke(operation.progress);
                    await UniTask.Yield();
                }

                // 416：服务器文件已更新，本地缓存失效 → 清除后由外层重试全量下载
                if (_currentRequest.responseCode == 416)
                {
                    GameLog.Warning($"[PatchDownloader] 收到 416，服务器文件已变更，清除本地缓存后重试全量下载");
                    _currentRequest.Dispose();
                    _currentRequest = null;
                    if (File.Exists(savePath))
                        File.Delete(savePath);
                    // 直接递归一次全量下载（startPosition = 0）
                    return await DownloadFileInternalAsync(url, savePath, onProgress);
                }

                if (_currentRequest.result != UnityWebRequest.Result.Success)
                {
                    GameLog.Error($"[PatchDownloader] 下载失败: {_currentRequest.error}");
                    return false;
                }

                byte[] data = _currentRequest.downloadHandler.data;
                if (startPosition > 0)
                {
                    // 断点续传追加写入
                    using var fs = new FileStream(savePath, FileMode.Append, FileAccess.Write);
                    fs.Write(data, 0, data.Length);
                }
                else
                {
                    File.WriteAllBytes(savePath, data);
                }

                _downloadedSize = new FileInfo(savePath).Length;
                _totalSize = _downloadedSize;
                GameLog.Log($"[PatchDownloader] 下载完成: {savePath} ({_downloadedSize} 字节)");
                onProgress?.Invoke(1.0f);
                return true;
            }
            catch (Exception ex)
            {
                GameLog.Error($"[PatchDownloader] 下载异常: {ex.Message}");
                return false;
            }
            finally
            {
                _currentRequest?.Dispose();
                _currentRequest = null;
            }
        }
        
        /// <summary>
        /// 取消下载
        /// </summary>
        public void CancelDownload()
        {
            _isCancelled = true;
            GameLog.Log("[PatchDownloader] 请求取消下载");
        }
        
        /// <summary>
        /// 获取已下载大小
        /// </summary>
        public long GetDownloadedSize()
        {
            return _downloadedSize;
        }
        
        /// <summary>
        /// 获取总大小
        /// </summary>
        public long GetTotalSize()
        {
            return _totalSize;
        }
    }
}
