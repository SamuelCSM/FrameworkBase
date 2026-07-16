using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using Framework;
using UnityEditor;
using UnityEngine;
using SQLite;

namespace Editor.ExcelTool
{
    /// <summary>
    /// Excel 导出器
    /// 负责将 Excel 数据导出到 SQLite 数据库
    /// </summary>
    public class ExcelExporter
    {
        /// <summary>
        /// 配置数据库输出目标。
        /// </summary>
        public enum DatabaseOutputTarget
        {
            /// <summary>仅写入首包 StreamingAssets，不触发热更资源。</summary>
            StreamingAssetsOnly,
            /// <summary>仅写入热更 config.db.bytes，不修改首包。</summary>
            HotUpdateOnly,
            /// <summary>写入首包后整库同步到热更 .bytes。</summary>
            Both
        }

        /// <summary>
        /// 导出配置
        /// </summary>
        public class ExportConfig
        {
            /// <summary>
            /// 首包配置数据库路径（StreamingAssets）。
            /// </summary>
            public string OutputDbPath { get; set; } = "Assets/StreamingAssets/RefData/config.db";

            /// <summary>
            /// Addressables 热更配置数据库输出路径。
            /// <para>ResourcesOut 同步规则会去掉 .bytes 扩展名，因此默认地址为 RefData/config.db。</para>
            /// </summary>
            public string AddressableBytesOutputPath { get; set; } = "Assets/ResourcesOut/RefData/config.db.bytes";

            /// <summary>
            /// 数据库输出目标：仅首包 / 仅热更 / 两者同步。
            /// </summary>
            public DatabaseOutputTarget OutputTarget { get; set; } = DatabaseOutputTarget.HotUpdateOnly;

            /// <summary>
            /// 是否覆盖已存在的表
            /// </summary>
            public bool OverwriteExistingTables { get; set; } = true;

            /// <summary>
            /// 批量导出成功后，是否删除数据库中已不在 Excel 目录内的旧表。
            /// </summary>
            public bool PruneMissingTablesOnBatch { get; set; } = false;

            /// <summary>
            /// 是否启用数据校验
            /// </summary>
            public bool EnableValidation { get; set; } = true;

            /// <summary>
            /// 是否显示详细日志
            /// </summary>
            public bool VerboseLogging { get; set; } = false;
        }

        /// <summary>
        /// 导出结果
        /// </summary>
        public class ExportResult
        {
            /// <summary>
            /// 是否成功
            /// </summary>
            public bool Success { get; set; }

            /// <summary>
            /// 导出的表名
            /// </summary>
            public string TableName { get; set; }

            /// <summary>
            /// 导出的工作表类型，用于结果面板区分普通表和 general 单例表。
            /// </summary>
            public ExcelReader.ExcelSheetKind SheetKind { get; set; } = ExcelReader.ExcelSheetKind.Table;

            /// <summary>
            /// 是否因为导出目标规则而跳过客户端 SQLite 写入。
            /// </summary>
            public bool Skipped { get; set; }

            /// <summary>
            /// 导出的行数
            /// </summary>
            public int RowCount { get; set; }

            /// <summary>
            /// 错误消息
            /// </summary>
            public string ErrorMessage { get; set; }

            /// <summary>
            /// 警告消息列表
            /// </summary>
            public List<string> Warnings { get; set; } = new List<string>();
        }

        private readonly ExportConfig _config;
        private readonly ExcelReader _reader;
        private readonly ExcelDataValidator _validator;
        private readonly ConfigExportRuleResolver _exportRules;
        private bool _deferAddressableSync;

        /// <summary>
        /// 本轮导出实际写入/清理过的片文件名（ADR-006）：批量结束后只对这些片同步热更 .bytes。
        /// </summary>
        private readonly HashSet<string> _touchedShardFileNames =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// 构造函数
        /// </summary>
        public ExcelExporter(ExportConfig config = null)
        {
            _config = config ?? new ExportConfig();
            _reader = new ExcelReader();
            _validator = new ExcelDataValidator();
            _exportRules = ConfigExportRuleResolver.LoadDefault();
        }

        /// <summary>
        /// 导出单个 Excel 文件（默认仅第一个工作表；指定 sheetName 时导出对应表）。
        /// </summary>
        public ExportResult ExportExcel(string excelPath, string sheetName = null)
        {
            if (!File.Exists(excelPath))
            {
                return FailResult($"Excel 文件不存在: {excelPath}");
            }

            var sheets = _reader.ReadExcel(excelPath);
            if (sheets == null || sheets.Count == 0)
                return FailResult("Excel 文件为空或无法读取");

            ExcelReader.ExcelSheetData targetSheet;
            if (string.IsNullOrEmpty(sheetName))
                targetSheet = sheets.FirstOrDefault();
            else
                targetSheet = sheets.FirstOrDefault(s => s.SheetName == sheetName);

            if (targetSheet == null)
                return FailResult($"未找到工作表: {sheetName}");

            if (!_exportRules.ShouldExportToClient(targetSheet.SheetName))
                return SkipResult(targetSheet, "服务端专用表，已跳过客户端 SQLite 导出");

            return ExportSheet(excelPath, targetSheet);
        }

        /// <summary>
        /// 导出单个 Excel 文件内的全部工作表（批量模式使用）。
        /// </summary>
        public List<ExportResult> ExportExcelAllSheets(string excelPath)
        {
            if (!File.Exists(excelPath))
                return new List<ExportResult> { FailResult($"Excel 文件不存在: {excelPath}") };

            var sheets = _reader.ReadExcel(excelPath);
            if (sheets == null || sheets.Count == 0)
                return new List<ExportResult> { FailResult("Excel 文件为空或无法读取") };

            Debug.Log(
                $"[ExcelExporter] {Path.GetFileName(excelPath)} 读取到 {sheets.Count} 个工作表: " +
                string.Join(", ", sheets.Select(s => s.SheetName)));

            var results = new List<ExportResult>(sheets.Count);
            foreach (var sheet in sheets)
            {
                if (!_exportRules.ShouldExportToClient(sheet.SheetName))
                    results.Add(SkipResult(sheet, "服务端专用表，已跳过客户端 SQLite 导出"));
                else
                    results.Add(ExportSheet(excelPath, sheet));
            }

            return results;
        }

        /// <summary>
        /// 批量导出 Excel 文件（每个文件导出全部工作表）。
        /// </summary>
        public List<ExportResult> ExportBatch(List<string> excelPaths, Action<int, int> progressCallback = null)
        {
            var results = new List<ExportResult>();
            bool canceled = false;
            _deferAddressableSync = _config.OutputTarget == DatabaseOutputTarget.Both;
            _touchedShardFileNames.Clear();

            try
            {
                for (int i = 0; i < excelPaths.Count; i++)
                {
                    var excelPath = excelPaths[i];

                    progressCallback?.Invoke(i + 1, excelPaths.Count);

                    results.AddRange(ExportExcelAllSheets(excelPath));

                    if (EditorUtility.DisplayCancelableProgressBar(
                        "批量导出 Excel",
                        $"正在导出: {Path.GetFileName(excelPath)} ({i + 1}/{excelPaths.Count})",
                        (float)(i + 1) / excelPaths.Count))
                    {
                        Debug.LogWarning("[ExcelExporter] 用户取消了批量导出");
                        canceled = true;
                        break;
                    }
                }

                if (!canceled && _config.PruneMissingTablesOnBatch && results.All(r => r.Success))
                {
                    PruneMissingTables(results.Where(r => !r.Skipped).Select(r => r.TableName));
                }
            }
            finally
            {
                _deferAddressableSync = false;
                if (_config.OutputTarget == DatabaseOutputTarget.Both)
                    SyncAddressableBytesIfNeeded();
            }

            EditorUtility.ClearProgressBar();

            int successCount = results.Count(r => r.Success);
            int failCount = results.Count(r => !r.Success);
            Debug.Log($"[ExcelExporter] 批量导出完成: 成功 {successCount} 张表, 失败 {failCount} 张表");

            return results;
        }

        private static ExportResult FailResult(string message)
        {
            return new ExportResult
            {
                Success = false,
                ErrorMessage = message
            };
        }

        private static ExportResult SkipResult(ExcelReader.ExcelSheetData sheetData, string message)
        {
            var result = new ExportResult
            {
                Success = true,
                Skipped = true,
                TableName = sheetData?.SheetName,
                SheetKind = sheetData?.SheetKind ?? ExcelReader.ExcelSheetKind.Table
            };
            result.Warnings.Add(message);
            return result;
        }

        private ExportResult ExportSheet(string excelPath, ExcelReader.ExcelSheetData targetSheet)
        {
            var result = new ExportResult
            {
                TableName = targetSheet.SheetName,
                SheetKind = targetSheet.SheetKind
            };

            try
            {
                if (_config.EnableValidation)
                {
                    var validationResult = _validator.ValidateSheet(targetSheet);
                    result.Warnings.AddRange(validationResult.Warnings);

                    if (!validationResult.IsValid)
                    {
                        result.Success = false;
                        result.ErrorMessage = $"数据校验失败:\n{string.Join("\n", validationResult.Errors)}";
                        if (validationResult.Warnings.Count > 0)
                            result.ErrorMessage += $"\n\n警告:\n{string.Join("\n", validationResult.Warnings)}";
                        return result;
                    }
                }

                ExportToSQLite(targetSheet, result);

                if (_config.VerboseLogging)
                    Debug.Log($"[ExcelExporter] 成功导出: {Path.GetFileName(excelPath)} -> {result.TableName}, 行数: {result.RowCount}");

                return result;
            }
            catch (Exception ex)
            {
                result.Success = false;
                result.ErrorMessage = $"导出失败: {ex.Message}\n{ex.StackTrace}";
                Debug.LogError($"[ExcelExporter] {excelPath} [{targetSheet.SheetName}] {result.ErrorMessage}");
                return result;
            }
        }

        /// <summary>
        /// 导出到 SQLite
        /// </summary>
        private void ExportToSQLite(ExcelReader.ExcelSheetData sheetData, ExportResult result)
        {
            // ADR-006 片路由：表落到片目录声明的库文件（未登记表归主片 config.db）。
            string shardFileName = ConfigShardCatalog.ResolveFileNameByTable(sheetData.SheetName);
            string outputDbPath = ResolveOutputDbPath(shardFileName);

            var directory = Path.GetDirectoryName(outputDbPath);
            if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
                Directory.CreateDirectory(directory);

            using (var connection = new SQLiteConnection(outputDbPath))
            {
                // 删除已存在的表（如果配置允许）
                if (_config.OverwriteExistingTables)
                {
                    var dropTableSql = $"DROP TABLE IF EXISTS {QuoteSqlIdentifier(sheetData.SheetName)}";
                    connection.Execute(dropTableSql);
                }

                // 创建表
                CreateTable(connection, sheetData);

                // 插入数据
                InsertData(connection, sheetData, result);
            }

            _touchedShardFileNames.Add(shardFileName);
            result.Success = true;

            if (!_deferAddressableSync)
                FinalizeOutputIfNeeded(shardFileName);
        }

        /// <summary>
        /// 首包侧片库路径：主片沿用配置路径原样（允许项目自定义主库文件名），辅片同目录换片文件名。
        /// </summary>
        private string StreamingShardDbPath(string shardFileName)
        {
            return IsMainShard(shardFileName)
                ? _config.OutputDbPath
                : WithFileName(_config.OutputDbPath, shardFileName);
        }

        /// <summary>
        /// 热更侧片 .bytes 路径：主片沿用配置路径原样，辅片同目录按「{片文件名}.bytes」落盘
        /// （ResourcesOut 同步规则去 .bytes 后地址即 RefData/{片文件名}）。
        /// </summary>
        private string AddressableShardBytesPath(string shardFileName)
        {
            return IsMainShard(shardFileName)
                ? _config.AddressableBytesOutputPath
                : WithFileName(_config.AddressableBytesOutputPath, shardFileName + ".bytes");
        }

        private static bool IsMainShard(string shardFileName)
        {
            return string.Equals(
                shardFileName, ConfigShardCatalog.MainShardFileName, StringComparison.OrdinalIgnoreCase);
        }

        private static string WithFileName(string basePath, string fileName)
        {
            string directory = Path.GetDirectoryName(basePath);
            return string.IsNullOrEmpty(directory) ? fileName : Path.Combine(directory, fileName);
        }

        /// <summary>
        /// 根据输出目标解析指定片实际写入的 SQLite 路径。
        /// </summary>
        private string ResolveOutputDbPath(string shardFileName)
        {
            switch (_config.OutputTarget)
            {
                case DatabaseOutputTarget.StreamingAssetsOnly:
                    if (string.IsNullOrEmpty(_config.OutputDbPath))
                        throw new ArgumentException("首包数据库路径不能为空");
                    return StreamingShardDbPath(shardFileName);

                case DatabaseOutputTarget.HotUpdateOnly:
                    if (string.IsNullOrEmpty(_config.AddressableBytesOutputPath))
                        throw new ArgumentException("热更数据库路径不能为空");
                    EnsureHotUpdateDatabaseExists(shardFileName);
                    return AddressableShardBytesPath(shardFileName);

                case DatabaseOutputTarget.Both:
                default:
                    if (string.IsNullOrEmpty(_config.OutputDbPath))
                        throw new ArgumentException("首包数据库路径不能为空");
                    return StreamingShardDbPath(shardFileName);
            }
        }

        /// <summary>
        /// 指定片的热更库不存在时，从该片首包库拷贝一份作为合并基线。
        /// </summary>
        private void EnsureHotUpdateDatabaseExists(string shardFileName)
        {
            string bytesPath = AddressableShardBytesPath(shardFileName);
            string streamingPath = StreamingShardDbPath(shardFileName);

            if (File.Exists(bytesPath))
                return;

            if (!File.Exists(streamingPath))
                return;

            string directory = Path.GetDirectoryName(bytesPath);
            if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
                Directory.CreateDirectory(directory);

            File.Copy(streamingPath, bytesPath, overwrite: false);

            if (_config.VerboseLogging)
                Debug.Log($"[ExcelExporter] 已从首包初始化热更数据库: {bytesPath}");
        }

        /// <summary>
        /// 单表导出结束后，按输出目标同步指定片的热更资源。
        /// </summary>
        private void FinalizeOutputIfNeeded(string shardFileName)
        {
            switch (_config.OutputTarget)
            {
                case DatabaseOutputTarget.StreamingAssetsOnly:
                    ImportAssetIfNeeded(StreamingShardDbPath(shardFileName));
                    break;

                case DatabaseOutputTarget.HotUpdateOnly:
                    ImportAssetIfNeeded(AddressableShardBytesPath(shardFileName));
                    break;

                case DatabaseOutputTarget.Both:
                    SyncAddressableBytes(shardFileName);
                    break;
            }
        }

        /// <summary>
        /// 批量导出收尾：把本轮写入/清理过的每个片的首包库同步为 Addressables 热更 .bytes。
        /// </summary>
        private void SyncAddressableBytesIfNeeded()
        {
            foreach (string shardFileName in _touchedShardFileNames)
                SyncAddressableBytes(shardFileName);
        }

        /// <summary>
        /// 将指定片的首包整库同步为 Addressables 热更 .bytes。
        /// </summary>
        private void SyncAddressableBytes(string shardFileName)
        {
            if (string.IsNullOrEmpty(_config.AddressableBytesOutputPath))
                throw new ArgumentException("热更数据库路径不能为空");

            string streamingPath = StreamingShardDbPath(shardFileName);
            string bytesPath = AddressableShardBytesPath(shardFileName);

            if (!File.Exists(streamingPath))
                throw new FileNotFoundException("首包配置数据库不存在，无法同步热更文件", streamingPath);

            string directory = Path.GetDirectoryName(bytesPath);
            if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
                Directory.CreateDirectory(directory);

            File.Copy(streamingPath, bytesPath, overwrite: true);
            ImportAssetIfNeeded(bytesPath);

            if (_config.VerboseLogging)
                Debug.Log($"[ExcelExporter] 已同步热更配置数据库: {bytesPath}");
        }

        private static void ImportAssetIfNeeded(string assetPath)
        {
            if (string.IsNullOrEmpty(assetPath) || !assetPath.Replace('\\', '/').StartsWith("Assets/"))
                return;

            AssetDatabase.ImportAsset(assetPath.Replace('\\', '/'), ImportAssetOptions.ForceUpdate);
        }

        /// <summary>
        /// 批量导出后按片清理数据库中不再由 Excel 源目录声明的旧表（ADR-006）。
        /// 每个片只保留本轮路由到该片的表——表迁片后会自动从旧片删除，杜绝两片各留一份的漂移。
        /// </summary>
        private void PruneMissingTables(IEnumerable<string> exportedTableNames)
        {
            // 按片分组本轮导出的表名：片 → 应保留表集合。
            var keepTablesByShard = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
            foreach (string tableName in exportedTableNames.Where(t => !string.IsNullOrEmpty(t)))
            {
                string shardFileName = ConfigShardCatalog.ResolveFileNameByTable(tableName);
                if (!keepTablesByShard.TryGetValue(shardFileName, out HashSet<string> keep))
                {
                    keep = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    keepTablesByShard[shardFileName] = keep;
                }

                keep.Add(tableName);
            }

            foreach (string shardFileName in ConfigShardCatalog.GetAllShardFileNames())
            {
                string pruneDbPath = _config.OutputTarget == DatabaseOutputTarget.HotUpdateOnly
                    ? AddressableShardBytesPath(shardFileName)
                    : StreamingShardDbPath(shardFileName);

                if (!File.Exists(pruneDbPath))
                {
                    continue;
                }

                keepTablesByShard.TryGetValue(shardFileName, out HashSet<string> keepTables);

                using (var connection = new SQLiteConnection(pruneDbPath))
                {
                    var tableNames = connection.Query<SqliteTableInfo>(
                            "SELECT name FROM sqlite_master WHERE type='table' AND name NOT LIKE 'sqlite_%'")
                        .Select(t => t.Name)
                        .Where(t => !string.IsNullOrEmpty(t))
                        .ToList();

                    foreach (string tableName in tableNames)
                    {
                        if (keepTables != null && keepTables.Contains(tableName))
                        {
                            continue;
                        }

                        connection.Execute($"DROP TABLE IF EXISTS {QuoteSqlIdentifier(tableName)}");
                        _touchedShardFileNames.Add(shardFileName); // 清理过的片同样要同步 .bytes
                        Debug.Log($"[ExcelExporter] 已清理旧配置表 [{shardFileName}]: {tableName}");
                    }
                }
            }
        }

        /// <summary>
        /// 读取 sqlite_master 表名时使用的临时映射类型。
        /// </summary>
        private class SqliteTableInfo
        {
            /// <summary>
            /// SQLite 表名。
            /// </summary>
            public string Name { get; set; }
        }

        /// <summary>
        /// 创建表
        /// </summary>
        private static string QuoteSqlIdentifier(string name)
        {
            if (string.IsNullOrEmpty(name))
                throw new ArgumentException("SQL 标识符不能为空", nameof(name));

            return $"[{name.Replace("]", "]]", StringComparison.Ordinal)}]";
        }

        private void CreateTable(SQLiteConnection connection, ExcelReader.ExcelSheetData sheetData)
        {
            if (sheetData.SheetKind == ExcelReader.ExcelSheetKind.General)
            {
                CreateGeneralTable(connection, sheetData);
                return;
            }

            var sb = new StringBuilder();
            sb.Append($"CREATE TABLE IF NOT EXISTS {QuoteSqlIdentifier(sheetData.SheetName)} (");

            for (int i = 0; i < sheetData.FieldNames.Count; i++)
            {
                var fieldName = sheetData.FieldNames[i];
                var typeName = i < sheetData.TypeDefinitions.Count ? sheetData.TypeDefinitions[i] : "string";
                var sqlType = ConvertToSQLiteType(typeName);

                sb.Append($"{QuoteSqlIdentifier(fieldName)} {sqlType}");

                // 普通表第一个字段作为主键。
                if (i == 0)
                {
                    sb.Append(" PRIMARY KEY");
                }

                if (i < sheetData.FieldNames.Count - 1)
                {
                    sb.Append(", ");
                }
            }

            sb.Append(")");

            var createTableSql = sb.ToString();
            if (_config.VerboseLogging)
            {
                Debug.Log($"[ExcelExporter] 创建表 SQL: {createTableSql}");
            }

            connection.Execute(createTableSql);
        }

        /// <summary>
        /// 创建 general 配置表的纵向 Key/ValueType/Value/Comment 结构。
        /// </summary>
        private void CreateGeneralTable(SQLiteConnection connection, ExcelReader.ExcelSheetData sheetData)
        {
            string createTableSql =
                $"CREATE TABLE IF NOT EXISTS {QuoteSqlIdentifier(sheetData.SheetName)} (" +
                $"{QuoteSqlIdentifier("Key")} TEXT PRIMARY KEY, " +
                $"{QuoteSqlIdentifier("ValueType")} TEXT, " +
                $"{QuoteSqlIdentifier("Value")} TEXT, " +
                $"{QuoteSqlIdentifier("Comment")} TEXT)";

            if (_config.VerboseLogging)
            {
                Debug.Log($"[ExcelExporter] 创建 general 表 SQL: {createTableSql}");
            }

            connection.Execute(createTableSql);
        }

        /// <summary>
        /// 插入数据
        /// </summary>
        private void InsertData(SQLiteConnection connection, ExcelReader.ExcelSheetData sheetData, ExportResult result)
        {
            if (sheetData.SheetKind == ExcelReader.ExcelSheetKind.General)
            {
                InsertGeneralData(connection, sheetData, result);
                return;
            }

            // 构建插入 SQL（字段名加 []，避免 Key 等保留字导致建表/插入失败）
            var fieldList = string.Join(", ", sheetData.FieldNames.Select(QuoteSqlIdentifier));
            var paramList = string.Join(", ", sheetData.FieldNames.Select(_ => "?"));
            var insertSql =
                $"INSERT INTO {QuoteSqlIdentifier(sheetData.SheetName)} ({fieldList}) VALUES ({paramList})";

            if (_config.VerboseLogging)
            {
                Debug.Log($"[ExcelExporter] 插入数据 SQL: {insertSql}");
            }

            // 开始事务
            connection.BeginTransaction();
            try
            {
                foreach (var row in sheetData.DataRows)
                {
                    // 准备参数值
                    var values = new object[sheetData.FieldNames.Count];
                    for (int i = 0; i < sheetData.FieldNames.Count; i++)
                    {
                        var fieldName = sheetData.FieldNames[i];
                        var typeName = i < sheetData.TypeDefinitions.Count ? sheetData.TypeDefinitions[i] : "string";
                        var value = row.ContainsKey(fieldName) ? row[fieldName] : null;
                        values[i] = ConvertToSQLiteValue(value, typeName);
                    }

                    connection.Execute(insertSql, values);
                    result.RowCount++;
                }

                connection.Commit();
            }
            catch (Exception ex)
            {
                connection.Rollback();
                throw new Exception($"插入数据失败: {ex.Message}", ex);
            }
        }

        /// <summary>
        /// 按 Key/ValueType/Value/Comment 纵向写入 general 配置。
        /// </summary>
        private void InsertGeneralData(SQLiteConnection connection, ExcelReader.ExcelSheetData sheetData, ExportResult result)
        {
            const string keyField = "Key";
            const string typeField = "ValueType";
            const string valueField = "Value";
            const string commentField = "Comment";

            var insertSql =
                $"INSERT INTO {QuoteSqlIdentifier(sheetData.SheetName)} " +
                $"({QuoteSqlIdentifier(keyField)}, {QuoteSqlIdentifier(typeField)}, {QuoteSqlIdentifier(valueField)}, {QuoteSqlIdentifier(commentField)}) " +
                "VALUES (?, ?, ?, ?)";

            if (_config.VerboseLogging)
            {
                Debug.Log($"[ExcelExporter] 插入 general 数据 SQL: {insertSql}");
            }

            connection.BeginTransaction();
            try
            {
                foreach (var row in sheetData.GeneralRows)
                {
                    connection.Execute(
                        insertSql,
                        row.Key,
                        row.ValueType,
                        ConvertToString(row.Value),
                        row.Comment ?? string.Empty);
                    result.RowCount++;
                }

                connection.Commit();
            }
            catch (Exception ex)
            {
                connection.Rollback();
                throw new Exception($"插入 general 数据失败: {ex.Message}", ex);
            }
        }

        /// <summary>
        /// 转换为 SQLite 类型
        /// </summary>
        private string ConvertToSQLiteType(string typeName)
        {
            switch (typeName.ToLower())
            {
                case "int":
                case "long":
                case "short":
                case "byte":
                case "bool":
                    return "INTEGER";

                case "float":
                case "double":
                case "decimal":
                    return "REAL";

                default:
                    return "TEXT";
            }
        }

        /// <summary>
        /// 按字段类型转换写入 SQLite 的参数值，避免 general 数值列被统一写成文本。
        /// </summary>
        private object ConvertToSQLiteValue(object value, string typeName)
        {
            if (value == null || value == DBNull.Value)
            {
                return string.Empty;
            }

            string text = value.ToString();
            if (string.IsNullOrWhiteSpace(text))
            {
                return string.Empty;
            }

            switch ((typeName ?? "string").Trim().ToLowerInvariant())
            {
                case "int":
                case "short":
                case "byte":
                    return Convert.ToInt32(value, CultureInfo.InvariantCulture);

                case "long":
                    return Convert.ToInt64(value, CultureInfo.InvariantCulture);

                case "bool":
                    return ParseBoolForSQLite(value) ? 1 : 0;

                case "float":
                case "double":
                case "decimal":
                    return Convert.ToDouble(value, CultureInfo.InvariantCulture);

                default:
                    return ConvertToString(value);
            }
        }

        /// <summary>
        /// 将 Excel 常见布尔值写成 SQLite 整型 1/0。
        /// </summary>
        private bool ParseBoolForSQLite(object value)
        {
            if (value is bool boolValue)
            {
                return boolValue;
            }

            string text = value.ToString().Trim().ToLowerInvariant();
            return text == "true" || text == "1" || text == "yes" || text == "y" || text == "是";
        }

        /// <summary>
        /// 转换为字符串
        /// </summary>
        private string ConvertToString(object value)
        {
            if (value == null || value == DBNull.Value)
            {
                return string.Empty;
            }

            // 数组类型转换为逗号分隔的字符串
            if (value is Array array)
            {
                var items = new List<string>();
                foreach (var item in array)
                {
                    items.Add(item?.ToString() ?? string.Empty);
                }
                return string.Join(",", items);
            }

            return value.ToString();
        }
    }
}
