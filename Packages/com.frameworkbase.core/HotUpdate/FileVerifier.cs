using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using Cysharp.Threading.Tasks;
using Framework.Storage;

namespace Framework.HotUpdate
{
    /// <summary>
    /// 热更新文件完整性校验器。
    /// <para>
    /// 正式补丁文件必须同时校验文件长度和 SHA-256：长度用于快速发现截断、拼接及错误响应体，
    /// SHA-256 用于验证文件内容是否与已签名清单一致。MD5 仅用于兼容旧清单，不能作为新发布链路的安全摘要。
    /// </para>
    /// <para>
    /// 大文件哈希计算通过线程池执行，避免磁盘读取和摘要计算阻塞 Unity 主线程。
    /// </para>
    /// </summary>
    public class FileVerifier
    {
        /// <summary>
        /// 使用指定摘要校验单个文件。
        /// <para>
        /// 该接口为历史调用保留，传入摘要按 SHA-256 处理。新代码应优先使用
        /// <see cref="VerifyPatchFileAsync"/>，同时校验清单声明的文件长度和摘要。
        /// </para>
        /// </summary>
        /// <param name="filePath">待校验文件的绝对路径或存储层可识别路径。</param>
        /// <param name="expectedHash">清单声明的 SHA-256 十六进制摘要。</param>
        /// <returns>文件存在且摘要一致时返回 <see langword="true"/>。</returns>
        public bool VerifyFile(string filePath, string expectedHash)
        {
            return VerifyFile(filePath, expectedSize: 0, expectedSha256: expectedHash, expectedMd5: null, out _);
        }

        /// <summary>
        /// 在线程池中执行历史单文件 SHA-256 校验，避免阻塞 Unity 主线程。
        /// </summary>
        /// <param name="filePath">待校验文件路径。</param>
        /// <param name="expectedHash">期望的 SHA-256 摘要。</param>
        /// <returns>异步返回校验结果。</returns>
        public async UniTask<bool> VerifyFileAsync(string filePath, string expectedHash)
        {
            return await UniTask.RunOnThreadPool(() => VerifyFile(filePath, expectedHash));
        }

        /// <summary>
        /// 按补丁清单声明的 Size 与 SHA-256 校验文件；旧清单缺少 SHA-256 时才回退到 MD5。
        /// </summary>
        /// <param name="filePath">待校验文件路径。</param>
        /// <param name="patchFile">已通过清单签名和结构校验的文件描述。</param>
        /// <returns>文件长度和内容摘要均满足清单约束时返回 <see langword="true"/>。</returns>
        public async UniTask<bool> VerifyPatchFileAsync(string filePath, PatchFile patchFile)
        {
            return await UniTask.RunOnThreadPool(() => VerifyPatchFile(filePath, patchFile, out _));
        }

        /// <summary>
        /// 同步校验单个补丁文件，并返回可用于日志或发布诊断的失败原因。
        /// </summary>
        /// <param name="filePath">待校验文件的完整路径。</param>
        /// <param name="patchFile">补丁清单中的文件长度和摘要声明。</param>
        /// <param name="error">失败时返回明确原因；成功时为 <see langword="null"/>。</param>
        /// <returns>校验通过时返回 <see langword="true"/>。</returns>
        public static bool VerifyPatchFile(string filePath, PatchFile patchFile, out string error)
        {
            if (patchFile == null)
            {
                error = "补丁文件描述不能为空。";
                return false;
            }

            return VerifyFile(filePath, patchFile.Size, patchFile.SHA256, patchFile.MD5, out error);
        }

        /// <summary>
        /// 批量执行历史“路径到 SHA-256”校验，并在每个文件完成后报告进度。
        /// <para>
        /// 本方法保持输入顺序串行执行，避免移动设备上同时读取多个大文件造成随机 I/O、内存和温度压力。
        /// </para>
        /// </summary>
        /// <param name="files">文件路径到期望 SHA-256 的映射。</param>
        /// <param name="onProgress">进度回调，参数依次为已完成数量和总数量。</param>
        /// <returns>每个输入文件对应的独立校验结果。</returns>
        public async UniTask<Dictionary<string, bool>> VerifyFilesAsync(
            Dictionary<string, string> files,
            Action<int, int> onProgress = null)
        {
            var results = new Dictionary<string, bool>();
            int totalCount = files.Count;
            int currentCount = 0;
            foreach (KeyValuePair<string, string> pair in files)
            {
                results[pair.Key] = await VerifyFileAsync(pair.Key, pair.Value);
                currentCount++;
                onProgress?.Invoke(currentCount, totalCount);
            }
            return results;
        }

        /// <summary>
        /// 校验目录中的完整补丁文件集合；任意文件失败立即停止并返回 <see langword="false"/>。
        /// </summary>
        /// <param name="patchFiles">清单声明的补丁文件集合。</param>
        /// <param name="baseDirectory">补丁文件所在的受控根目录。</param>
        /// <param name="onProgress">每完成一个文件后报告已完成数量和总数量。</param>
        /// <returns>集合为空或全部文件通过校验时返回 <see langword="true"/>。</returns>
        public async UniTask<bool> VerifyPatchFilesAsync(
            List<PatchFile> patchFiles,
            string baseDirectory,
            Action<int, int> onProgress = null)
        {
            if (patchFiles == null || patchFiles.Count == 0)
                return true;

            int current = 0;
            foreach (PatchFile patch in patchFiles)
            {
                string path = Path.Combine(baseDirectory, patch.FileName);
                bool valid = await VerifyPatchFileAsync(path, patch);
                current++;
                onProgress?.Invoke(current, patchFiles.Count);
                if (!valid) return false;
            }
            return true;
        }

        /// <summary>
        /// 计算文件 MD5，仅供旧清单兼容、非安全缓存键或迁移诊断使用。
        /// </summary>
        public string CalculateMD5(string filePath) => MD5Util.GetFileMD5(filePath);

        /// <summary>
        /// 计算文件 SHA-256，并以小写十六进制字符串返回。
        /// </summary>
        public string CalculateSHA256(string filePath) => CalculateHash(filePath, SHA256.Create());

        /// <summary>
        /// 执行文件存在性、长度及摘要校验。摘要优先级固定为 SHA-256，其次才是旧版 MD5。
        /// </summary>
        private static bool VerifyFile(
            string filePath,
            long expectedSize,
            string expectedSha256,
            string expectedMd5,
            out string error)
        {
            error = null;
            try
            {
                if (!FileStorages.Shared.FileExists(filePath))
                {
                    error = $"待校验文件不存在：{filePath}";
                    return false;
                }

                long actualSize = FileStorages.Shared.GetFileSize(filePath);
                if (expectedSize > 0 && actualSize != expectedSize)
                {
                    error = $"文件长度不一致：{Path.GetFileName(filePath)}，期望={expectedSize}，实际={actualSize}";
                    return false;
                }

                if (!string.IsNullOrWhiteSpace(expectedSha256))
                {
                    string actual = CalculateHash(filePath, SHA256.Create());
                    if (!FixedTimeEqualsHex(actual, expectedSha256))
                    {
                        error = $"文件 SHA-256 不一致：{Path.GetFileName(filePath)}，期望={expectedSha256}，实际={actual}";
                        return false;
                    }
                    return true;
                }

                // 仅为旧版清单保留 MD5 回退。新版发布器和正式环境安全准入必须要求 SHA-256，
                // 因此这里不能把“存在 MD5”视为新供应链的安全能力。
                if (!string.IsNullOrWhiteSpace(expectedMd5))
                {
                    string actual = MD5Util.GetFileMD5(filePath);
                    if (!FixedTimeEqualsHex(actual, expectedMd5))
                    {
                        error = $"旧版文件 MD5 不一致：{Path.GetFileName(filePath)}";
                        return false;
                    }
                    return true;
                }

                error = $"文件清单未提供可用摘要：{Path.GetFileName(filePath)}";
                return false;
            }
            catch (Exception ex)
            {
                error = $"文件校验发生异常：{ex.Message}";
                return false;
            }
        }

        /// <summary>
        /// 以流式方式计算文件摘要，避免将热更新大文件整体加载到托管内存。
        /// </summary>
        private static string CalculateHash(string filePath, HashAlgorithm algorithm)
        {
            using (algorithm)
            using (FileStream stream = File.OpenRead(filePath))
            {
                byte[] hash = algorithm.ComputeHash(stream);
                var builder = new StringBuilder(hash.Length * 2);
                foreach (byte value in hash) builder.Append(value.ToString("x2"));
                return builder.ToString();
            }
        }

        /// <summary>
        /// 使用固定循环比较两个十六进制摘要，降低普通字符串比较因首个差异位置不同而产生的时序泄露。
        /// </summary>
        private static bool FixedTimeEqualsHex(string left, string right)
        {
            if (left == null || right == null) return false;
            string a = left.Trim().ToLowerInvariant();
            string b = right.Trim().ToLowerInvariant();
            int diff = a.Length ^ b.Length;
            int count = Math.Min(a.Length, b.Length);
            for (int i = 0; i < count; i++) diff |= a[i] ^ b[i];
            return diff == 0;
        }
    }
}
