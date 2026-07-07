using System.IO;
using Cysharp.Threading.Tasks;
using Framework.Http;
using Framework.Storage;
using UnityEngine;

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
            HttpResponse response = await HttpClients.Shared.SendAsync(HttpRequest.Get(sourceUrl));
            if (!response.Succeeded)
            {
                GameLog.Warning($"[StreamingAssetsBytesReader] 读取失败: {sourceUrl}, {response.Error}");
                return null;
            }

            return response.Data;
#else
            if (!FileStorages.Shared.FileExists(fullPath))
                return null;

            return await FileStorages.Shared.ReadBytesAsync(fullPath);
#endif
        }
    }
}
