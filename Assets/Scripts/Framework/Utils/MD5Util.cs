using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace Framework
{
    /// <summary>
    /// MD5 工具类
    /// 提供字符串、文件、字节数组的 MD5 计算功能
    /// </summary>
    public static class MD5Util
    {
        /// <summary>
        /// 大文件分块大小（默认 4MB）
        /// </summary>
        private const int CHUNK_SIZE = 4 * 1024 * 1024;

        /// <summary>
        /// 计算字符串的 MD5 值
        /// </summary>
        /// <param name="input">输入字符串</param>
        /// <returns>MD5 值（32位小写十六进制字符串）</returns>
        public static string GetMD5(string input)
        {
            if (string.IsNullOrEmpty(input))
                return string.Empty;

            byte[] bytes = Encoding.UTF8.GetBytes(input);
            return GetBytesMD5(bytes);
        }

        /// <summary>
        /// 计算字节数组的 MD5 值
        /// </summary>
        /// <param name="bytes">字节数组</param>
        /// <returns>MD5 值（32位小写十六进制字符串）</returns>
        public static string GetBytesMD5(byte[] bytes)
        {
            if (bytes == null || bytes.Length == 0)
                return string.Empty;

            using (MD5 md5 = MD5.Create())
            {
                byte[] hash = md5.ComputeHash(bytes);
                return BytesToHexString(hash);
            }
        }

        /// <summary>
        /// 计算文件的 MD5 值
        /// </summary>
        /// <param name="filePath">文件路径</param>
        /// <returns>MD5 值（32位小写十六进制字符串），文件不存在返回空字符串</returns>
        public static string GetFileMD5(string filePath)
        {
            if (string.IsNullOrEmpty(filePath) || !File.Exists(filePath))
            {
                Logger.Warning($"文件不存在，无法计算 MD5: {filePath}");
                return string.Empty;
            }

            try
            {
                using (FileStream fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    using (MD5 md5 = MD5.Create())
                    {
                        byte[] hash = md5.ComputeHash(fs);
                        return BytesToHexString(hash);
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Error($"计算文件 MD5 失败: {filePath}, 错误: {ex.Message}");
                return string.Empty;
            }
        }

        /// <summary>
        /// 计算大文件的 MD5 值（分块计算，避免内存占用过大）
        /// </summary>
        /// <param name="filePath">文件路径</param>
        /// <param name="chunkSize">分块大小（字节），默认 4MB</param>
        /// <param name="onProgress">进度回调（参数：已处理字节数，总字节数）</param>
        /// <returns>MD5 值（32位小写十六进制字符串），文件不存在返回空字符串</returns>
        public static string GetLargeFileMD5(string filePath, int chunkSize = CHUNK_SIZE, Action<long, long> onProgress = null)
        {
            if (string.IsNullOrEmpty(filePath) || !File.Exists(filePath))
            {
                Logger.Warning($"文件不存在，无法计算 MD5: {filePath}");
                return string.Empty;
            }

            try
            {
                using (FileStream fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    long totalBytes = fs.Length;
                    long processedBytes = 0;

                    using (MD5 md5 = MD5.Create())
                    {
                        byte[] buffer = new byte[chunkSize];
                        int bytesRead;

                        while ((bytesRead = fs.Read(buffer, 0, buffer.Length)) > 0)
                        {
                            processedBytes += bytesRead;

                            // 如果是最后一块，计算最终哈希
                            if (processedBytes >= totalBytes)
                            {
                                md5.TransformFinalBlock(buffer, 0, bytesRead);
                            }
                            else
                            {
                                md5.TransformBlock(buffer, 0, bytesRead, buffer, 0);
                            }

                            // 触发进度回调
                            onProgress?.Invoke(processedBytes, totalBytes);
                        }

                        byte[] hash = md5.Hash;
                        return BytesToHexString(hash);
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Error($"计算大文件 MD5 失败: {filePath}, 错误: {ex.Message}");
                return string.Empty;
            }
        }

        /// <summary>
        /// 验证文件的 MD5 值
        /// </summary>
        /// <param name="filePath">文件路径</param>
        /// <param name="expectedMD5">期望的 MD5 值</param>
        /// <returns>是否匹配</returns>
        public static bool VerifyFileMD5(string filePath, string expectedMD5)
        {
            if (string.IsNullOrEmpty(expectedMD5))
                return false;

            string actualMD5 = GetFileMD5(filePath);
            return string.Equals(actualMD5, expectedMD5, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// 验证大文件的 MD5 值（分块计算）
        /// </summary>
        /// <param name="filePath">文件路径</param>
        /// <param name="expectedMD5">期望的 MD5 值</param>
        /// <param name="chunkSize">分块大小（字节），默认 4MB</param>
        /// <param name="onProgress">进度回调（参数：已处理字节数，总字节数）</param>
        /// <returns>是否匹配</returns>
        public static bool VerifyLargeFileMD5(string filePath, string expectedMD5, int chunkSize = CHUNK_SIZE, Action<long, long> onProgress = null)
        {
            if (string.IsNullOrEmpty(expectedMD5))
                return false;

            string actualMD5 = GetLargeFileMD5(filePath, chunkSize, onProgress);
            return string.Equals(actualMD5, expectedMD5, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// 将字节数组转换为十六进制字符串
        /// </summary>
        /// <param name="bytes">字节数组</param>
        /// <returns>十六进制字符串（小写）</returns>
        private static string BytesToHexString(byte[] bytes)
        {
            if (bytes == null || bytes.Length == 0)
                return string.Empty;

            StringBuilder sb = new StringBuilder(bytes.Length * 2);
            for (int i = 0; i < bytes.Length; i++)
            {
                sb.Append(bytes[i].ToString("x2"));
            }
            return sb.ToString();
        }

        /// <summary>
        /// 计算流的 MD5 值
        /// </summary>
        /// <param name="stream">输入流</param>
        /// <returns>MD5 值（32位小写十六进制字符串）</returns>
        public static string GetStreamMD5(Stream stream)
        {
            if (stream == null || !stream.CanRead)
                return string.Empty;

            try
            {
                // 保存当前位置
                long originalPosition = stream.CanSeek ? stream.Position : 0;

                using (MD5 md5 = MD5.Create())
                {
                    byte[] hash = md5.ComputeHash(stream);

                    // 恢复流位置
                    if (stream.CanSeek)
                    {
                        stream.Position = originalPosition;
                    }

                    return BytesToHexString(hash);
                }
            }
            catch (Exception ex)
            {
                Logger.Error($"计算流 MD5 失败: {ex.Message}");
                return string.Empty;
            }
        }

        /// <summary>
        /// 获取文件信息（包含 MD5）
        /// </summary>
        /// <param name="filePath">文件路径</param>
        /// <returns>文件信息（包含路径、大小、MD5）</returns>
        public static FileHashInfo GetFileHashInfo(string filePath)
        {
            if (string.IsNullOrEmpty(filePath) || !File.Exists(filePath))
                return null;

            try
            {
                FileInfo fileInfo = new FileInfo(filePath);
                string md5 = GetFileMD5(filePath);

                return new FileHashInfo
                {
                    FilePath = filePath,
                    FileName = fileInfo.Name,
                    FileSize = fileInfo.Length,
                    MD5 = md5,
                    LastWriteTime = fileInfo.LastWriteTime
                };
            }
            catch (Exception ex)
            {
                Logger.Error($"获取文件哈希信息失败: {filePath}, 错误: {ex.Message}");
                return null;
            }
        }
    }

    /// <summary>
    /// 文件哈希信息
    /// </summary>
    public class FileHashInfo
    {
        /// <summary>
        /// 文件路径
        /// </summary>
        public string FilePath { get; set; }

        /// <summary>
        /// 文件名
        /// </summary>
        public string FileName { get; set; }

        /// <summary>
        /// 文件大小（字节）
        /// </summary>
        public long FileSize { get; set; }

        /// <summary>
        /// MD5 值
        /// </summary>
        public string MD5 { get; set; }

        /// <summary>
        /// 最后修改时间
        /// </summary>
        public DateTime LastWriteTime { get; set; }

        public override string ToString()
        {
            return $"File: {FileName}, Size: {FileSize} bytes, MD5: {MD5}";
        }
    }
}
