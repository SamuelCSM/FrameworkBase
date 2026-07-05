namespace Framework
{
    /// <summary>
    /// 文件与字节相关的通用工具方法
    /// </summary>
    public static class FileUtils
    {
        private static readonly string[] Units = { "B", "KB", "MB", "GB", "TB" };

        /// <summary>
        /// 将字节数格式化为人类可读字符串，例如 "12.5 MB"
        /// </summary>
        /// <param name="bytes">字节数（负值按 0 处理）</param>
        /// <returns>格式化后的字符串</returns>
        public static string FormatBytes(long bytes)
        {
            if (bytes <= 0) return "0 B";

            double val = bytes;
            int i = 0;
            while (val >= 1024 && i < Units.Length - 1)
            {
                val /= 1024;
                i++;
            }
            return $"{val:0.##} {Units[i]}";
        }
    }
}
