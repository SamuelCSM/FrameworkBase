using System.IO;
using UnityEngine;

namespace Framework
{
    /// <summary>
    /// 路径工具类
    /// 提供统一的路径管理和跨平台路径处理
    /// </summary>
    public static class PathUtil
    {
        /// <summary>
        /// 获取 StreamingAssets 路径
        /// 注意：Android 平台下 StreamingAssets 在 APK 内部，需要使用特殊方式访问
        /// </summary>
        public static string StreamingAssetsPath
        {
            get { return Application.streamingAssetsPath; }
        }

        /// <summary>
        /// 获取 PersistentData 路径（可读写）
        /// 用于存储用户数据、配置文件、日志等
        /// </summary>
        public static string PersistentDataPath
        {
            get { return Application.persistentDataPath; }
        }

        /// <summary>
        /// 获取临时缓存路径（可读写）
        /// 用于存储临时文件，系统可能会自动清理
        /// </summary>
        public static string TemporaryCachePath
        {
            get { return Application.temporaryCachePath; }
        }

        /// <summary>
        /// 获取数据路径（只读）
        /// 通常指向应用程序的安装目录
        /// </summary>
        public static string DataPath
        {
            get { return Application.dataPath; }
        }

        /// <summary>
        /// 拼接路径（跨平台）
        /// 自动处理路径分隔符，确保跨平台兼容性
        /// </summary>
        /// <param name="paths">路径片段</param>
        /// <returns>拼接后的路径</returns>
        public static string Combine(params string[] paths)
        {
            if (paths == null || paths.Length == 0)
                return string.Empty;

            // 使用 Path.Combine 自动处理平台差异
            string result = paths[0];
            for (int i = 1; i < paths.Length; i++)
            {
                if (!string.IsNullOrEmpty(paths[i]))
                {
                    result = Path.Combine(result, paths[i]);
                }
            }

            // 统一使用正斜杠（Unity 推荐）
            return NormalizePath(result);
        }

        /// <summary>
        /// 标准化路径（统一使用正斜杠）
        /// </summary>
        /// <param name="path">原始路径</param>
        /// <returns>标准化后的路径</returns>
        public static string NormalizePath(string path)
        {
            if (string.IsNullOrEmpty(path))
                return path;

            // 将反斜杠替换为正斜杠
            path = path.Replace('\\', '/');

            string prefix = string.Empty;
            int schemeIndex = path.IndexOf("://", System.StringComparison.Ordinal);
            if (schemeIndex >= 0)
            {
                prefix = path.Substring(0, schemeIndex + 3);
                path = path.Substring(schemeIndex + 3);
            }

            // 移除重复的斜杠
            while (path.Contains("//"))
            {
                path = path.Replace("//", "/");
            }

            return prefix + path;
        }

        /// <summary>
        /// 获取文件名（包含扩展名）
        /// </summary>
        /// <param name="path">文件路径</param>
        /// <returns>文件名</returns>
        public static string GetFileName(string path)
        {
            if (string.IsNullOrEmpty(path))
                return string.Empty;

            return Path.GetFileName(path);
        }

        /// <summary>
        /// 获取文件名（不包含扩展名）
        /// </summary>
        /// <param name="path">文件路径</param>
        /// <returns>文件名（不含扩展名）</returns>
        public static string GetFileNameWithoutExtension(string path)
        {
            if (string.IsNullOrEmpty(path))
                return string.Empty;

            return Path.GetFileNameWithoutExtension(path);
        }

        /// <summary>
        /// 获取文件扩展名
        /// </summary>
        /// <param name="path">文件路径</param>
        /// <returns>扩展名（包含点号，如 ".txt"）</returns>
        public static string GetExtension(string path)
        {
            if (string.IsNullOrEmpty(path))
                return string.Empty;

            return Path.GetExtension(path);
        }

        /// <summary>
        /// 获取目录名
        /// </summary>
        /// <param name="path">文件路径</param>
        /// <returns>目录路径</returns>
        public static string GetDirectoryName(string path)
        {
            if (string.IsNullOrEmpty(path))
                return string.Empty;

            string directory = Path.GetDirectoryName(path);
            return NormalizePath(directory);
        }

        /// <summary>
        /// 检查路径是否存在（文件或目录）
        /// </summary>
        /// <param name="path">路径</param>
        /// <returns>是否存在</returns>
        public static bool Exists(string path)
        {
            if (string.IsNullOrEmpty(path))
                return false;

            return File.Exists(path) || Directory.Exists(path);
        }

        /// <summary>
        /// 检查文件是否存在
        /// </summary>
        /// <param name="path">文件路径</param>
        /// <returns>是否存在</returns>
        public static bool FileExists(string path)
        {
            if (string.IsNullOrEmpty(path))
                return false;

            return File.Exists(path);
        }

        /// <summary>
        /// 检查目录是否存在
        /// </summary>
        /// <param name="path">目录路径</param>
        /// <returns>是否存在</returns>
        public static bool DirectoryExists(string path)
        {
            if (string.IsNullOrEmpty(path))
                return false;

            return Directory.Exists(path);
        }

        /// <summary>
        /// 创建目录（如果不存在）
        /// </summary>
        /// <param name="path">目录路径</param>
        /// <returns>是否创建成功</returns>
        public static bool CreateDirectory(string path)
        {
            if (string.IsNullOrEmpty(path))
                return false;

            try
            {
                if (!Directory.Exists(path))
                {
                    Directory.CreateDirectory(path);
                }
                return true;
            }
            catch (System.Exception ex)
            {
                Logger.Error($"创建目录失败: {path}, 错误: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// 删除文件
        /// </summary>
        /// <param name="path">文件路径</param>
        /// <returns>是否删除成功</returns>
        public static bool DeleteFile(string path)
        {
            if (string.IsNullOrEmpty(path) || !File.Exists(path))
                return false;

            try
            {
                File.Delete(path);
                return true;
            }
            catch (System.Exception ex)
            {
                Logger.Error($"删除文件失败: {path}, 错误: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// 删除目录（包括所有子文件和子目录）
        /// </summary>
        /// <param name="path">目录路径</param>
        /// <returns>是否删除成功</returns>
        public static bool DeleteDirectory(string path)
        {
            if (string.IsNullOrEmpty(path) || !Directory.Exists(path))
                return false;

            try
            {
                Directory.Delete(path, true);
                return true;
            }
            catch (System.Exception ex)
            {
                Logger.Error($"删除目录失败: {path}, 错误: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// 获取相对路径
        /// </summary>
        /// <param name="fullPath">完整路径</param>
        /// <param name="basePath">基础路径</param>
        /// <returns>相对路径</returns>
        public static string GetRelativePath(string fullPath, string basePath)
        {
            if (string.IsNullOrEmpty(fullPath) || string.IsNullOrEmpty(basePath))
                return fullPath;

            fullPath = NormalizePath(fullPath);
            basePath = NormalizePath(basePath);

            // 确保基础路径以斜杠结尾
            if (!basePath.EndsWith("/"))
                basePath += "/";

            if (fullPath.StartsWith(basePath))
            {
                return fullPath.Substring(basePath.Length);
            }

            return fullPath;
        }

        /// <summary>
        /// 获取 StreamingAssets 下的完整路径
        /// </summary>
        /// <param name="relativePath">相对路径</param>
        /// <returns>完整路径</returns>
        public static string GetStreamingAssetsPath(string relativePath)
        {
            return Combine(StreamingAssetsPath, relativePath);
        }

        /// <summary>
        /// 获取 PersistentData 下的完整路径
        /// </summary>
        /// <param name="relativePath">相对路径</param>
        /// <returns>完整路径</returns>
        public static string GetPersistentDataPath(string relativePath)
        {
            return Combine(PersistentDataPath, relativePath);
        }

        /// <summary>
        /// 获取 TemporaryCache 下的完整路径
        /// </summary>
        /// <param name="relativePath">相对路径</param>
        /// <returns>完整路径</returns>
        public static string GetTemporaryCachePath(string relativePath)
        {
            return Combine(TemporaryCachePath, relativePath);
        }

        /// <summary>
        /// 获取文件的 URL 格式路径（用于 UnityWebRequest 等）
        /// </summary>
        /// <param name="path">文件路径</param>
        /// <returns>URL 格式路径</returns>
        public static string GetFileUrl(string path)
        {
            if (string.IsNullOrEmpty(path))
                return string.Empty;

            path = NormalizePath(path);

            // 如果已经是 URL 格式，直接返回
            if (path.StartsWith("http://") || path.StartsWith("https://") || path.StartsWith("file://"))
                return path;

            // 添加 file:// 前缀
#if UNITY_ANDROID && !UNITY_EDITOR
            // Android 平台 StreamingAssets 使用特殊路径
            if (path.StartsWith(Application.streamingAssetsPath))
            {
                return path; // Android 的 streamingAssetsPath 已经包含了正确的协议
            }
#endif
            return "file://" + path;
        }
    }
}
