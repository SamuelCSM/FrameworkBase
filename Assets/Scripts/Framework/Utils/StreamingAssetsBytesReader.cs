using System.IO;
using Cysharp.Threading.Tasks;
using UnityEngine;
using UnityEngine.Networking;

namespace Framework
{
    /// <summary>
    /// 从 StreamingAssets 读取二进制（兼容 Android APK 内路径）。
    /// </summary>
    public static class StreamingAssetsBytesReader
    {
        /// <summary>读取 StreamingAssets 相对路径下的文件字节。</summary>
        public static async UniTask<byte[]> ReadAsync(string relativePath)
        {
            if (string.IsNullOrEmpty(relativePath))
                return null;

            string fullPath = PathUtil.GetStreamingAssetsPath(relativePath);

#if UNITY_ANDROID && !UNITY_EDITOR
            string sourceUrl = PathUtil.GetFileUrl(fullPath);
            using (var request = UnityWebRequest.Get(sourceUrl))
            {
                await request.SendWebRequest();
                if (request.result != UnityWebRequest.Result.Success)
                {
                    Logger.Warning($"[StreamingAssetsBytesReader] 读取失败: {sourceUrl}, {request.error}");
                    return null;
                }

                return request.downloadHandler.data;
            }
#else
            if (!File.Exists(fullPath))
                return null;

            byte[] bytes = await UniTask.RunOnThreadPool(() => File.ReadAllBytes(fullPath));
            return bytes;
#endif
        }
    }
}
