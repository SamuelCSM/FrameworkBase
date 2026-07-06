using System;
using System.IO;
using Cysharp.Threading.Tasks;
using Framework;
using HybridCLR;
using UnityEngine;

namespace Framework.HotUpdate
{
    /// <summary>
    /// 加载 HybridCLR AOT 补充元数据（须在热更程序集加载之前执行）。
    /// </summary>
    public static class HybridCLRMetadataLoader
    {
        /// <summary>加载全部 manifest 中的 AOT 补充元数据。</summary>
        public static async UniTask<bool> LoadAllAsync()
        {
#if UNITY_EDITOR
            GameLog.Log("[HybridCLRMetadataLoader] 编辑器模式：跳过 AOT 元数据加载");
            return true;
#else
            string[] assemblies = await ResolveAssemblyListAsync();
            if (assemblies == null || assemblies.Length == 0)
            {
                GameLog.Warning("[HybridCLRMetadataLoader] 元数据程序集列表为空，跳过");
                return true;
            }

            bool allOk = true;
            foreach (string assemblyName in assemblies)
            {
                if (!await LoadOneAsync(assemblyName))
                    allOk = false;
            }

            return allOk;
#endif
        }

        private static async UniTask<string[]> ResolveAssemblyListAsync()
        {
            string manifestRelative = PathUtil.Combine(
                HybridCLRMetadataManifest.StreamingAssetsFolder,
                HybridCLRMetadataManifest.ManifestFileName);

            byte[] manifestBytes = await StreamingAssetsBytesReader.ReadAsync(manifestRelative);
            if (manifestBytes != null && manifestBytes.Length > 0)
            {
                try
                {
                    string json = System.Text.Encoding.UTF8.GetString(manifestBytes);
                    var data = JsonUtility.FromJson<HybridCLRMetadataManifestData>(json);
                    if (data?.assemblies != null && data.assemblies.Length > 0)
                        return data.assemblies;
                }
                catch (Exception ex)
                {
                    GameLog.Warning($"[HybridCLRMetadataLoader] 解析 manifest 失败，使用内置列表: {ex.Message}");
                }
            }

            return HybridCLRMetadataManifest.PatchedAotAssemblies;
        }

        private static async UniTask<bool> LoadOneAsync(string assemblyFileName)
        {
            string bytesFileName = ToBytesFileName(assemblyFileName);
            string relativePath = PathUtil.Combine(HybridCLRMetadataManifest.StreamingAssetsFolder, bytesFileName);

            byte[] dllBytes = TryReadPersistent(bytesFileName);
            if (dllBytes == null)
                dllBytes = await StreamingAssetsBytesReader.ReadAsync(relativePath);

            if (dllBytes == null || dllBytes.Length == 0)
            {
                GameLog.Error($"[HybridCLRMetadataLoader] 未找到 AOT 元数据: {bytesFileName}");
                return false;
            }

            LoadImageErrorCode err = RuntimeApi.LoadMetadataForAOTAssembly(dllBytes, HomologousImageMode.SuperSet);
            if (err == LoadImageErrorCode.OK || err == LoadImageErrorCode.HOMOLOGOUS_ASSEMBLY_HAS_LOADED)
            {
                GameLog.Log($"[HybridCLRMetadataLoader] 元数据已加载: {assemblyFileName} ({err})");
                return true;
            }

            GameLog.Error($"[HybridCLRMetadataLoader] 加载失败: {assemblyFileName}, code={err}");
            return false;
        }

        private static byte[] TryReadPersistent(string bytesFileName)
        {
            string path = Path.Combine(Application.persistentDataPath, bytesFileName);
            if (!File.Exists(path))
                return null;

            try
            {
                return File.ReadAllBytes(path);
            }
            catch (Exception ex)
            {
                GameLog.Warning($"[HybridCLRMetadataLoader] 读取 persistent 元数据失败: {path}, {ex.Message}");
                return null;
            }
        }

        private static string ToBytesFileName(string assemblyFileName)
        {
            if (assemblyFileName.EndsWith(".bytes", StringComparison.OrdinalIgnoreCase))
                return assemblyFileName;

            return assemblyFileName.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)
                ? assemblyFileName + ".bytes"
                : assemblyFileName + ".dll.bytes";
        }
    }
}
