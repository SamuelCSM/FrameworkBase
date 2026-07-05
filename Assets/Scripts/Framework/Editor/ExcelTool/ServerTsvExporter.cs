using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using UnityEngine;

namespace Editor.ExcelTool
{
    /// <summary>
    /// 服务端 TSV 导出器
    /// 将 Excel 数据导出为制表符分隔的纯文本文件，供服务端 TsvConfigLoader 加载。
    /// </summary>
    public class ServerTsvExporter
    {
        /// <summary>
        /// 导出配置
        /// </summary>
        public class TsvExportConfig
        {
            /// <summary>
            /// 服务端配表输出目录。
            /// </summary>
            public string OutputDirectory { get; set; } = "Server/ConfigData";

            /// <summary>
            /// 是否覆盖已存在的文件。
            /// </summary>
            public bool OverwriteExisting { get; set; } = true;

            /// <summary>
            /// 是否显示详细日志。
            /// </summary>
            public bool VerboseLogging { get; set; } = false;
        }

        /// <summary>
        /// 单张表的导出结果。
        /// </summary>
        public class TsvExportResult
        {
            /// <summary>
            /// 是否成功。
            /// </summary>
            public bool Success { get; set; }

            /// <summary>
            /// 导出的表名。
            /// </summary>
            public string TableName { get; set; }

            /// <summary>
            /// 导出的行数。
            /// </summary>
            public int RowCount { get; set; }

            /// <summary>
            /// 输出文件路径。
            /// </summary>
            public string OutputPath { get; set; }

            /// <summary>
            /// 本次导出是否实际写入文件；内容未变化时为 false。
            /// </summary>
            public bool ContentChanged { get; set; }

            /// <summary>
            /// 错误消息。
            /// </summary>
            public string ErrorMessage { get; set; }
        }

        private readonly TsvExportConfig _config;

        /// <summary>
        /// 构造函数。
        /// </summary>
        public ServerTsvExporter(TsvExportConfig config = null)
        {
            _config = config ?? new TsvExportConfig();
        }

        /// <summary>
        /// 导出单个工作表为 TSV 文件。
        /// </summary>
        public TsvExportResult ExportSheet(ExcelReader.ExcelSheetData sheetData)
        {
            if (sheetData == null)
                return FailResult("sheetData 为 null");

            var result = new TsvExportResult { TableName = sheetData.SheetName };

            try
            {
                if (!Directory.Exists(_config.OutputDirectory))
                    Directory.CreateDirectory(_config.OutputDirectory);

                string outputPath = Path.Combine(_config.OutputDirectory, $"{sheetData.SheetName}.txt");

                if (!_config.OverwriteExisting && File.Exists(outputPath))
                    return FailResult($"文件已存在且不允许覆盖: {outputPath}");

                string content = sheetData.SheetKind == ExcelReader.ExcelSheetKind.General
                    ? BuildGeneralTsv(sheetData)
                    : BuildTableTsv(sheetData);

                bool contentChanged = ConfigExportFileWriter.WriteAllTextIfChanged(
                    outputPath,
                    content,
                    new UTF8Encoding(false));

                result.Success = true;
                result.OutputPath = outputPath;
                result.ContentChanged = contentChanged;
                result.RowCount = sheetData.SheetKind == ExcelReader.ExcelSheetKind.General
                    ? sheetData.GeneralRows.Count
                    : sheetData.DataRows.Count;

                if (_config.VerboseLogging)
                {
                    string writeState = contentChanged ? "已写入" : "内容未变化，跳过写入";
                    Debug.Log($"[ServerTsvExporter] 导出成功: {sheetData.SheetName} -> {outputPath}, 行数: {result.RowCount}, {writeState}");
                }

                return result;
            }
            catch (Exception ex)
            {
                result.Success = false;
                result.ErrorMessage = $"导出失败: {ex.Message}";
                Debug.LogError($"[ServerTsvExporter] {sheetData.SheetName} {result.ErrorMessage}\n{ex.StackTrace}");
                return result;
            }
        }

        /// <summary>
        /// 批量导出多个工作表。
        /// </summary>
        public List<TsvExportResult> ExportBatch(List<ExcelReader.ExcelSheetData> sheets)
        {
            var results = new List<TsvExportResult>();
            foreach (var sheet in sheets)
            {
                results.Add(ExportSheet(sheet));
            }

            int successCount = results.Count(r => r.Success);
            int failCount = results.Count(r => !r.Success);
            Debug.Log($"[ServerTsvExporter] 批量导出完成: 成功 {successCount}, 失败 {failCount}");

            return results;
        }

        /// <summary>
        /// 构建普通表的 TSV 内容。
        /// 格式：第一行字段名，第二行类型，第三行起数据。
        /// </summary>
        private string BuildTableTsv(ExcelReader.ExcelSheetData sheetData)
        {
            var sb = new StringBuilder();

            // 第一行：字段名
            sb.AppendLine(string.Join("\t", sheetData.FieldNames));

            // 第二行：类型定义
            var types = new List<string>();
            for (int i = 0; i < sheetData.FieldNames.Count; i++)
            {
                types.Add(i < sheetData.TypeDefinitions.Count ? sheetData.TypeDefinitions[i] : "string");
            }
            sb.AppendLine(string.Join("\t", types));

            // 第三行起：数据行
            foreach (var row in sheetData.DataRows)
            {
                var values = new List<string>();
                for (int i = 0; i < sheetData.FieldNames.Count; i++)
                {
                    string fieldName = sheetData.FieldNames[i];
                    string typeName = i < sheetData.TypeDefinitions.Count ? sheetData.TypeDefinitions[i] : "string";
                    object rawValue = row.ContainsKey(fieldName) ? row[fieldName] : null;
                    values.Add(FormatValue(rawValue, typeName));
                }
                sb.AppendLine(string.Join("\t", values));
            }

            return sb.ToString();
        }

        /// <summary>
        /// 构建 general 表的 TSV 内容。
        /// 格式：第一行 Key/ValueType/Value/Comment，第二行起每行一个配置字段。
        /// </summary>
        private string BuildGeneralTsv(ExcelReader.ExcelSheetData sheetData)
        {
            var sb = new StringBuilder();

            // 第一行：固定表头
            sb.AppendLine("Key\tValueType\tValue\tComment");

            // 第二行起：键值数据
            foreach (var row in sheetData.GeneralRows)
            {
                string value = FormatValue(row.Value, row.ValueType);
                string comment = (row.Comment ?? string.Empty).Replace("\t", " ").Replace("\n", " ").Replace("\r", "");
                sb.AppendLine($"{row.Key}\t{row.ValueType}\t{value}\t{comment}");
            }

            return sb.ToString();
        }

        /// <summary>
        /// 将单元格值格式化为 TSV 安全的文本表示。
        /// </summary>
        private string FormatValue(object value, string typeName)
        {
            if (value == null || value == DBNull.Value)
                return string.Empty;

            string text = value.ToString();
            if (string.IsNullOrWhiteSpace(text))
                return string.Empty;

            // bool 统一用 true/false
            switch ((typeName ?? "string").Trim().ToLowerInvariant())
            {
                case "bool":
                    return ParseBool(value) ? "true" : "false";

                case "float":
                case "double":
                case "decimal":
                    // 保证小数点使用点号
                    return Convert.ToDouble(value, CultureInfo.InvariantCulture)
                        .ToString(CultureInfo.InvariantCulture);

                case "int":
                case "short":
                case "byte":
                    return Convert.ToInt32(value, CultureInfo.InvariantCulture).ToString();

                case "long":
                    return Convert.ToInt64(value, CultureInfo.InvariantCulture).ToString();

                default:
                    // 替换制表符和换行，防止破坏 TSV 格式
                    return text.Replace("\t", " ").Replace("\n", " ").Replace("\r", "");
            }
        }

        /// <summary>
        /// 解析布尔值，兼容 Excel 常见写法。
        /// </summary>
        private bool ParseBool(object value)
        {
            if (value is bool b)
                return b;

            string text = value.ToString().Trim().ToLowerInvariant();
            return text == "true" || text == "1" || text == "yes" || text == "y" || text == "是";
        }

        private static TsvExportResult FailResult(string message)
        {
            return new TsvExportResult
            {
                Success = false,
                ErrorMessage = message
            };
        }
    }
}
