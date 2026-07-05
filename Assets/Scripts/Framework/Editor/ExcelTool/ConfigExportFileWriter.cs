using System;
using System.IO;
using System.Text;

namespace Editor.ExcelTool
{
    /// <summary>
    /// 配表导出文件写入工具，用于避免内容未变化时刷新文件修改时间。
    /// </summary>
    public static class ConfigExportFileWriter
    {
        /// <summary>
        /// 仅当目标文件内容发生变化时写入文本。
        /// </summary>
        /// <param name="path">目标文件路径。</param>
        /// <param name="content">待写入内容。</param>
        /// <param name="encoding">写入编码。</param>
        /// <param name="normalizeForCompare">比较前的内容归一化函数；为空时按原文比较。</param>
        /// <returns>实际写入文件时返回 true，内容未变化时返回 false。</returns>
        public static bool WriteAllTextIfChanged(
            string path,
            string content,
            Encoding encoding,
            Func<string, string> normalizeForCompare = null)
        {
            if (File.Exists(path))
            {
                string oldContent = File.ReadAllText(path, encoding);
                string oldComparable = normalizeForCompare != null
                    ? normalizeForCompare(oldContent)
                    : oldContent;
                string newComparable = normalizeForCompare != null
                    ? normalizeForCompare(content)
                    : content;

                if (string.Equals(oldComparable, newComparable, StringComparison.Ordinal))
                {
                    return false;
                }
            }

            File.WriteAllText(path, content, encoding);
            return true;
        }

        /// <summary>
        /// 移除自动生成头中的生成时间行，用于判断服务端配置类是否只有时间戳变化。
        /// </summary>
        /// <param name="content">生成的 C# 源码内容。</param>
        /// <returns>不包含生成时间行的源码内容。</returns>
        public static string RemoveGeneratedTimeLine(string content)
        {
            if (string.IsNullOrEmpty(content))
            {
                return content;
            }

            var lines = content.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
            var builder = new StringBuilder(content.Length);
            foreach (string line in lines)
            {
                if (line.StartsWith("// 生成时间:", StringComparison.Ordinal))
                {
                    continue;
                }

                builder.Append(line);
                builder.Append('\n');
            }

            return builder.ToString();
        }
    }
}
