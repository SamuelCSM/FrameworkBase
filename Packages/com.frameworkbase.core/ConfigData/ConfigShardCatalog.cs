using System;
using System.Collections.Generic;
using System.IO;

namespace Framework
{
    /// <summary>
    /// 配表分片目录（ADR-006）：表名 → 片文件名的唯一映射源，运行时路由与导出管线共用。
    /// <para>
    /// 片（shard）是配表版本、下载、回滚、覆盖的统一原子单元：互相引用的表必须同片，
    /// 片内整体替换，片间独立版本。默认两片：<c>main</c>（config.db，未登记表的默认归属）
    /// 与 <c>language</c>（language.db——启动最早需要、文案改动最频繁，独立成片使
    /// 「改文案不动大库」且可在 LaunchFlow 第一条 Loading 文案前提前就绪）。
    /// </para>
    /// <para>
    /// 兼容语义：片文件缺失时表路由回退主库（见 <see cref="ResolveDbPathForTable"/>）——
    /// 未导出分片的老项目零迁移。线程边界：<see cref="RegisterTableShard"/> 仅应在组合根
    /// 启动早期调用，运行期只读。
    /// </para>
    /// </summary>
    public static class ConfigShardCatalog
    {
        /// <summary>主片（默认片）文件名。未登记到任何片的表一律归主片。</summary>
        public const string MainShardFileName = "config.db";

        /// <summary>language 片文件名（框架内置片：多语言文案表独立成片）。</summary>
        public const string LanguageShardFileName = "language.db";

        /// <summary>language 表名（框架 Bootstrap 表：生成类预置于 ConfigData/Bootstrap/）。</summary>
        public const string LanguageTableName = "language";

        /// <summary>表名 → 片文件名。未登记的表归主片。</summary>
        private static readonly Dictionary<string, string> ShardFileByTable =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                { LanguageTableName, LanguageShardFileName },
            };

        /// <summary>
        /// 框架 Bootstrap 表集合：生成类已预置在框架包内（ConfigData/Bootstrap/），
        /// 导出管线对这些表只导数据、跳过代码生成，避免热更侧长出重复类。
        /// </summary>
        private static readonly HashSet<string> FrameworkBootstrapTables =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase) { LanguageTableName };

        /// <summary>
        /// 全部片文件名（主片在前，其余按登记顺序去重）。
        /// ensure/confirm/restore 等按片迭代的入口统一从这里取片清单。
        /// </summary>
        /// <returns>片文件名列表；首元素恒为 <see cref="MainShardFileName"/>。</returns>
        public static List<string> GetAllShardFileNames()
        {
            var names = new List<string> { MainShardFileName };
            foreach (string fileName in ShardFileByTable.Values)
            {
                if (!names.Contains(fileName))
                    names.Add(fileName);
            }

            return names;
        }

        /// <summary>
        /// 解析表所属片的文件名；未登记的表归主片。
        /// </summary>
        /// <param name="tableName">SQLite 表名（大小写不敏感）。</param>
        /// <returns>片文件名。</returns>
        public static string ResolveFileNameByTable(string tableName)
        {
            if (!string.IsNullOrEmpty(tableName) &&
                ShardFileByTable.TryGetValue(tableName, out string fileName))
            {
                return fileName;
            }

            return MainShardFileName;
        }

        /// <summary>
        /// 由主库路径推导片库路径：主片即主库路径本身（主库文件名允许被调用方自定义），
        /// 其余片与主库同目录、按片文件名落盘。
        /// </summary>
        /// <param name="mainDbPath">主库绝对路径。</param>
        /// <param name="shardFileName">片文件名。</param>
        /// <returns>片库绝对路径。</returns>
        public static string GetShardDbPath(string mainDbPath, string shardFileName)
        {
            if (string.IsNullOrEmpty(mainDbPath))
                throw new ArgumentException("主库路径不能为空", nameof(mainDbPath));

            if (string.Equals(shardFileName, MainShardFileName, StringComparison.OrdinalIgnoreCase))
                return mainDbPath;

            string directory = Path.GetDirectoryName(mainDbPath) ?? string.Empty;
            return Path.Combine(directory, shardFileName);
        }

        /// <summary>
        /// 解析表的实际读取库路径（纯函数，文件存在性经注入判断，可脱离 Unity 单测）。
        /// 片文件缺失时回退主库并置 <paramref name="fellBackToMain"/>——老项目单库零迁移，
        /// 导出配置错误可见（调用方记日志）而不致崩。
        /// </summary>
        /// <param name="mainDbPath">主库绝对路径。</param>
        /// <param name="tableName">SQLite 表名。</param>
        /// <param name="fileExists">文件存在性判断（生产传 File.Exists）。</param>
        /// <param name="fellBackToMain">片文件缺失而回退主库时为 true。</param>
        /// <returns>该表应读取的库路径。</returns>
        public static string ResolveDbPathForTable(
            string mainDbPath, string tableName, Func<string, bool> fileExists, out bool fellBackToMain)
        {
            if (fileExists == null)
                throw new ArgumentNullException(nameof(fileExists));

            fellBackToMain = false;
            string fileName = ResolveFileNameByTable(tableName);
            string path = GetShardDbPath(mainDbPath, fileName);
            if (ReferenceEquals(path, mainDbPath) || string.Equals(path, mainDbPath, StringComparison.Ordinal))
                return mainDbPath;

            if (!fileExists(path))
            {
                fellBackToMain = true;
                return mainDbPath;
            }

            return path;
        }

        /// <summary>
        /// 判断是否是框架 Bootstrap 表（导出管线跳过代码生成、只导数据）。
        /// </summary>
        /// <param name="tableName">SQLite 表名。</param>
        /// <returns>是 Bootstrap 表返回 true。</returns>
        public static bool IsFrameworkBootstrapTable(string tableName)
        {
            return !string.IsNullOrEmpty(tableName) && FrameworkBootstrapTables.Contains(tableName);
        }

        /// <summary>
        /// 登记业务表到指定片（组合根启动早期调用；新增业务片前必须先满足 ADR-006 的
        /// 扩片前置条件——跨片外键校验门禁）。同表重复登记到不同片视为配置漂移，直接拒绝。
        /// </summary>
        /// <param name="tableName">SQLite 表名。</param>
        /// <param name="shardFileName">片文件名（纯文件名，不含路径分隔符）。</param>
        public static void RegisterTableShard(string tableName, string shardFileName)
        {
            if (string.IsNullOrWhiteSpace(tableName))
                throw new ArgumentException("表名不能为空", nameof(tableName));
            if (string.IsNullOrWhiteSpace(shardFileName))
                throw new ArgumentException("片文件名不能为空", nameof(shardFileName));
            if (shardFileName.IndexOfAny(new[] { '/', '\\' }) >= 0)
                throw new ArgumentException($"片文件名不得含路径分隔符：{shardFileName}", nameof(shardFileName));

            if (ShardFileByTable.TryGetValue(tableName, out string existing))
            {
                if (string.Equals(existing, shardFileName, StringComparison.OrdinalIgnoreCase))
                    return; // 幂等重登记

                throw new InvalidOperationException(
                    $"表 {tableName} 已登记到片 {existing}，拒绝改登记到 {shardFileName}（配置漂移）。");
            }

            ShardFileByTable[tableName] = shardFileName;
        }
    }
}
