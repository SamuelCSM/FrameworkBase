using System;
using System.Collections.Generic;
using System.IO;
using Cysharp.Threading.Tasks;

namespace Framework.HotUpdate
{
    /// <summary>
    /// 文件校验器
    /// 负责校验文件完整性（MD5校验）
    /// </summary>
    public class FileVerifier
    {
        /// <summary>
        /// 校验文件
        /// </summary>
        /// <param name="filePath">文件路径</param>
        /// <param name="expectedMD5">期望的MD5值</param>
        /// <returns>是否校验通过</returns>
        public bool VerifyFile(string filePath, string expectedMD5)
        {
            try
            {
                if (!File.Exists(filePath))
                {
                    Logger.Error($"[FileVerifier] 文件不存在: {filePath}");
                    return false;
                }
                
                string actualMD5 = CalculateMD5(filePath);
                bool isValid = string.Equals(actualMD5, expectedMD5, StringComparison.OrdinalIgnoreCase);
                
                if (isValid)
                {
                    Logger.Log($"[FileVerifier] 文件校验通过: {filePath}");
                }
                else
                {
                    Logger.Error($"[FileVerifier] 文件校验失败: {filePath}, 期望MD5={expectedMD5}, 实际MD5={actualMD5}");
                }
                
                return isValid;
            }
            catch (Exception ex)
            {
                Logger.Error($"[FileVerifier] 校验文件异常: {ex.Message}");
                return false;
            }
        }
        
        /// <summary>
        /// 异步校验文件
        /// </summary>
        /// <param name="filePath">文件路径</param>
        /// <param name="expectedMD5">期望的MD5值</param>
        /// <returns>是否校验通过</returns>
        public async UniTask<bool> VerifyFileAsync(string filePath, string expectedMD5)
        {
            // 在后台线程执行MD5计算
            return await UniTask.RunOnThreadPool(() => VerifyFile(filePath, expectedMD5));
        }
        
        /// <summary>
        /// 批量校验文件
        /// </summary>
        /// <param name="files">文件列表（文件路径 -> 期望MD5）</param>
        /// <param name="onProgress">进度回调（已校验数量，总数量）</param>
        /// <returns>校验结果（文件路径 -> 是否通过）</returns>
        public async UniTask<Dictionary<string, bool>> VerifyFilesAsync(
            Dictionary<string, string> files, 
            Action<int, int> onProgress = null)
        {
            var results = new Dictionary<string, bool>();
            int totalCount = files.Count;
            int currentCount = 0;
            
            Logger.Log($"[FileVerifier] 开始批量校验，共{totalCount}个文件");
            
            foreach (var kvp in files)
            {
                string filePath = kvp.Key;
                string expectedMD5 = kvp.Value;
                
                bool isValid = await VerifyFileAsync(filePath, expectedMD5);
                results[filePath] = isValid;
                
                currentCount++;
                onProgress?.Invoke(currentCount, totalCount);
            }
            
            int passCount = 0;
            foreach (var result in results.Values)
            {
                if (result) passCount++;
            }
            
            Logger.Log($"[FileVerifier] 批量校验完成: {passCount}/{totalCount} 通过");
            
            return results;
        }
        
        /// <summary>
        /// 批量校验补丁文件
        /// </summary>
        /// <param name="patchFiles">补丁文件列表</param>
        /// <param name="baseDirectory">基础目录</param>
        /// <param name="onProgress">进度回调（已校验数量，总数量）</param>
        /// <returns>所有文件是否都通过校验</returns>
        public async UniTask<bool> VerifyPatchFilesAsync(
            List<PatchFile> patchFiles,
            string baseDirectory,
            Action<int, int> onProgress = null)
        {
            if (patchFiles == null || patchFiles.Count == 0)
            {
                Logger.Log("[FileVerifier] 没有需要校验的文件");
                return true;
            }
            
            var filesToVerify = new Dictionary<string, string>();
            
            foreach (var patchFile in patchFiles)
            {
                string filePath = Path.Combine(baseDirectory, patchFile.FileName);
                filesToVerify[filePath] = patchFile.MD5;
            }
            
            var results = await VerifyFilesAsync(filesToVerify, onProgress);
            
            // 检查是否所有文件都通过校验
            foreach (var result in results.Values)
            {
                if (!result)
                {
                    return false;
                }
            }
            
            return true;
        }
        
        /// <summary>
        /// 计算文件MD5
        /// </summary>
        /// <param name="filePath">文件路径</param>
        /// <returns>MD5字符串</returns>
        public string CalculateMD5(string filePath)
        {
            return MD5Util.GetFileMD5(filePath);
        }
    }
}
