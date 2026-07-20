using System;
using System.Collections;
using System.Collections.Generic;
using System.Data;
using System.IO;
using System.Reflection;
using ExcelDataReader;
using UnityEngine;

namespace Editor.ExcelTool
{
    /// <summary>
    /// 将 Excel 工作表读取为导出和代码生成共用的中间配置模型。
    /// </summary>
    public class ExcelReader
    {
        /// <summary>
        /// 描述工作表在配置流水线中的解析类型。
        /// </summary>
        public enum ExcelSheetKind
        {
            /// <summary>首列为唯一主键、运行时按键查询的普通配置表。</summary>
            Table,

            /// <summary>单例纵向 Key/Value 配置。</summary>
            General,

            /// <summary>不要求主键、按行完整保留的关系/多对多配置列表。</summary>
            List
        }

        /// <summary>
        /// 定义普通表工作表使用的行索引。
        /// </summary>
        public class ExcelFormat
        {
            /// <summary>
            /// 注释行的零基索引。
            /// </summary>
            public int CommentRowIndex { get; set; } = 0;

            /// <summary>
            /// 字段名行的零基索引。
            /// </summary>
            public int FieldNameRowIndex { get; set; } = 1;

            /// <summary>
            /// 类型定义行的零基索引。
            /// </summary>
            public int TypeRowIndex { get; set; } = 2;

            /// <summary>
            /// 首行数据的零基索引。
            /// </summary>
            public int DataStartRowIndex { get; set; } = 3;
        }

        /// <summary>
        /// 保存单个已解析工作表的字段、类型、注释和行数据。
        /// </summary>
        public class ExcelSheetData
        {
            /// <summary>
            /// 来源工作表名称。
            /// </summary>
            public string SheetName { get; set; }

            /// <summary>
            /// 解析后的工作表类型，用于决定代码生成和导出行为。
            /// </summary>
            public ExcelSheetKind SheetKind { get; set; } = ExcelSheetKind.Table;

            /// <summary>
            /// 字段名列表，会生成 C# 属性和 SQLite 列。
            /// </summary>
            public List<string> FieldNames { get; set; } = new List<string>();

            /// <summary>
            /// 与 FieldNames 对齐的 C# 类型定义。
            /// </summary>
            public List<string> TypeDefinitions { get; set; } = new List<string>();

            /// <summary>
            /// 与 FieldNames 对齐的人类可读注释。
            /// </summary>
            public List<string> Comments { get; set; } = new List<string>();

            /// <summary>
            /// 按字段名存储的已解析行数据。
            /// </summary>
            public List<Dictionary<string, object>> DataRows { get; set; } = new List<Dictionary<string, object>>();

            /// <summary>
            /// general 工作表的原始 Key/ValueType/Value/Comment 行，用于按纵向结构导出 SQLite。
            /// </summary>
            public List<GeneralConfigRow> GeneralRows { get; set; } = new List<GeneralConfigRow>();
        }

        /// <summary>
        /// 保存 general 工作表中一条原始键值配置。
        /// </summary>
        public class GeneralConfigRow
        {
            /// <summary>
            /// 配置字段键，会对应生成数据类属性名和运行时反射赋值目标。
            /// </summary>
            public string Key { get; set; }

            /// <summary>
            /// 配置值类型，保留原始 Excel 类型文本。
            /// </summary>
            public string ValueType { get; set; }

            /// <summary>
            /// 配置值，导出时保持纵向 Value 列。
            /// </summary>
            public object Value { get; set; }

            /// <summary>
            /// 配置说明文本，仅用于工具展示和生成注释。
            /// </summary>
            public string Comment { get; set; }
        }

        private readonly ExcelFormat _format;

        /// <summary>
        /// 使用默认行布局或调用方传入的行布局创建读取器。
        /// </summary>
        public ExcelReader(ExcelFormat format = null)
        {
            _format = format ?? new ExcelFormat();
        }

        /// <summary>
        /// 从 Excel 文件中读取所有有效工作表。
        /// </summary>
        public List<ExcelSheetData> ReadExcel(string filePath)
        {
            if (string.IsNullOrEmpty(filePath))
            {
                throw new ArgumentNullException(nameof(filePath));
            }

            if (!File.Exists(filePath))
            {
                throw new FileNotFoundException($"Excel file does not exist: {filePath}");
            }

            var result = new List<ExcelSheetData>();

            try
            {
                System.Text.Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance);

                using (var stream = File.Open(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                using (var reader = ExcelReaderFactory.CreateReader(stream))
                {
                    var dataSet = reader.AsDataSet(new ExcelDataSetConfiguration
                    {
                        ConfigureDataTable = _ => new ExcelDataTableConfiguration
                        {
                            UseHeaderRow = false
                        }
                    });

                    foreach (DataTable table in dataSet.Tables)
                    {
                        var sheetData = ReadSheet(table);
                        if (sheetData != null)
                        {
                            result.Add(sheetData);
                        }
                        else
                        {
                            Debug.LogWarning($"[ExcelReader] Skipped worksheet {table.TableName}; no valid structure was found.");
                        }
                    }
                }

                Debug.Log($"[ExcelReader] Read Excel file successfully: {filePath}, worksheets: {result.Count}");
                return result;
            }
            catch (Exception ex)
            {
                Debug.LogError($"[ExcelReader] Failed to read Excel file: {filePath}, error: {ex.Message}");
                throw;
            }
        }

        /// <summary>
        /// 判断工作表是否应按单例键值 general 配置解析。
        /// </summary>
        public static bool IsGeneralSheet(string sheetName)
        {
            return !string.IsNullOrEmpty(sheetName) &&
                   sheetName.EndsWith("_general", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// 根据工作表名称将其分发给普通表解析器或 general 解析器。
        /// </summary>
        private ExcelSheetData ReadSheet(DataTable table)
        {
            if (table == null || table.Rows.Count == 0)
            {
                Debug.LogWarning("[ExcelReader] Worksheet is empty.");
                return null;
            }

            try
            {
                return IsGeneralSheet(table.TableName)
                    ? ReadGeneralSheet(table)
                    : ReadTableSheet(table);
            }
            catch (Exception ex)
            {
                Debug.LogError($"[ExcelReader] Failed to read worksheet {table.TableName}: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// 读取普通表工作表：列表示字段，后续行表示多条记录。
        /// </summary>
        private ExcelSheetData ReadTableSheet(DataTable table)
        {
            var sheetData = new ExcelSheetData
            {
                SheetName = table.TableName,
                SheetKind = ExcelSheetKind.Table
            };

            ReadCommentRow(table, sheetData);
            ReadFieldNameRow(table, sheetData);
            ReadTypeRow(table, sheetData);

            if (sheetData.FieldNames.Count == 0)
            {
                Debug.LogWarning($"[ExcelReader] Worksheet {table.TableName} has no valid field names; skipped.");
                return null;
            }

            for (int row = _format.DataStartRowIndex; row < table.Rows.Count; row++)
            {
                var rowData = new Dictionary<string, object>();
                bool hasData = false;

                for (int col = 0; col < sheetData.FieldNames.Count && col < table.Columns.Count; col++)
                {
                    string fieldName = sheetData.FieldNames[col];
                    object cellValue = table.Rows[row][col];
                    if (cellValue != null && cellValue != DBNull.Value && !string.IsNullOrWhiteSpace(cellValue.ToString()))
                    {
                        hasData = true;
                    }

                    rowData[fieldName] = cellValue;
                }

                if (hasData)
                {
                    sheetData.DataRows.Add(rowData);
                }
            }

            Debug.Log($"[ExcelReader] Read table worksheet {table.TableName}, fields: {sheetData.FieldNames.Count}, rows: {sheetData.DataRows.Count}");
            return sheetData;
        }

        /// <summary>
        /// 读取 general 工作表，并将 Key/ValueType/Value 行折叠成一条单例数据行。
        /// </summary>
        private ExcelSheetData ReadGeneralSheet(DataTable table)
        {
            var sheetData = new ExcelSheetData
            {
                SheetName = table.TableName,
                SheetKind = ExcelSheetKind.General
            };

            if (_format.FieldNameRowIndex >= table.Rows.Count)
            {
                throw new InvalidOperationException("general worksheet is missing the field definition row.");
            }

            // 第二行声明键值结构列：Key、ValueType、Value，以及可选的 Comment。
            var fieldNameRow = table.Rows[_format.FieldNameRowIndex];
            var columnMap = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            for (int col = 0; col < table.Columns.Count; col++)
            {
                string fieldName = fieldNameRow[col]?.ToString()?.Trim();
                if (!string.IsNullOrEmpty(fieldName) && !columnMap.ContainsKey(fieldName))
                {
                    columnMap[fieldName] = col;
                }
            }

            if (!columnMap.TryGetValue("Key", out int keyCol) ||
                !columnMap.TryGetValue("ValueType", out int typeCol) ||
                !columnMap.TryGetValue("Value", out int valueCol))
            {
                throw new InvalidOperationException($"general worksheet {table.TableName} is missing required columns: Key / ValueType / Value.");
            }

            bool hasCommentCol = columnMap.TryGetValue("Comment", out int commentCol);

            var rowData = new Dictionary<string, object>();
            var fieldNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            for (int row = _format.DataStartRowIndex; row < table.Rows.Count; row++)
            {
                string key = table.Rows[row][keyCol]?.ToString()?.Trim();
                if (string.IsNullOrEmpty(key))
                {
                    continue;
                }

                if (!fieldNames.Add(key))
                {
                    throw new InvalidOperationException($"general worksheet {table.TableName} contains a duplicate Key: {key}");
                }

                string valueType = table.Rows[row][typeCol]?.ToString()?.Trim();
                string comment = hasCommentCol ? table.Rows[row][commentCol]?.ToString() ?? string.Empty : string.Empty;
                object value = table.Rows[row][valueCol];

                // 每一行键值配置都会变成一个生成属性，同时保留原始行用于 SQLite 纵向导出。
                sheetData.FieldNames.Add(key);
                sheetData.TypeDefinitions.Add(string.IsNullOrEmpty(valueType) ? "string" : valueType);
                sheetData.Comments.Add(comment);
                rowData[key] = value;
                sheetData.GeneralRows.Add(new GeneralConfigRow
                {
                    Key = key,
                    ValueType = string.IsNullOrEmpty(valueType) ? "string" : valueType,
                    Value = value,
                    Comment = comment
                });
            }

            if (sheetData.FieldNames.Count == 0)
            {
                throw new InvalidOperationException($"general worksheet {table.TableName} has no exportable fields.");
            }

            sheetData.DataRows.Add(rowData);
            Debug.Log($"[ExcelReader] Read general worksheet {table.TableName}, fields: {sheetData.FieldNames.Count}");
            return sheetData;
        }

        /// <summary>
        /// 读取配置的注释行并写入工作表元数据。
        /// </summary>
        private void ReadCommentRow(DataTable table, ExcelSheetData sheetData)
        {
            if (_format.CommentRowIndex >= table.Rows.Count)
            {
                return;
            }

            var commentRow = table.Rows[_format.CommentRowIndex];
            for (int col = 0; col < table.Columns.Count; col++)
            {
                sheetData.Comments.Add(commentRow[col]?.ToString() ?? string.Empty);
            }
        }

        /// <summary>
        /// 读取配置的字段名行，并忽略空白列。
        /// </summary>
        private void ReadFieldNameRow(DataTable table, ExcelSheetData sheetData)
        {
            if (_format.FieldNameRowIndex >= table.Rows.Count)
            {
                return;
            }

            var fieldRow = table.Rows[_format.FieldNameRowIndex];
            for (int col = 0; col < table.Columns.Count; col++)
            {
                string fieldName = fieldRow[col]?.ToString() ?? string.Empty;
                if (!string.IsNullOrWhiteSpace(fieldName))
                {
                    sheetData.FieldNames.Add(fieldName.Trim());
                }
            }
        }

        /// <summary>
        /// 读取配置的类型行，并将类型与字段列表对齐。
        /// </summary>
        private void ReadTypeRow(DataTable table, ExcelSheetData sheetData)
        {
            if (_format.TypeRowIndex >= table.Rows.Count)
            {
                return;
            }

            var typeRow = table.Rows[_format.TypeRowIndex];
            for (int col = 0; col < sheetData.FieldNames.Count && col < table.Columns.Count; col++)
            {
                string typeName = typeRow[col]?.ToString() ?? "string";
                sheetData.TypeDefinitions.Add(typeName.Trim());
            }
        }

        /// <summary>
        /// 将单个 Excel 单元格值转换为请求的运行时类型。
        /// </summary>
        public static object ParseCellValue(object cellValue, Type targetType)
        {
            if (targetType == null)
            {
                throw new ArgumentNullException(nameof(targetType));
            }

            if (cellValue == null || cellValue == DBNull.Value)
            {
                return GetDefaultValue(targetType);
            }

            try
            {
                Type actualType = Nullable.GetUnderlyingType(targetType) ?? targetType;

                if (actualType == typeof(string))
                {
                    return cellValue.ToString();
                }

                if (actualType == typeof(bool))
                {
                    return ParseBool(cellValue);
                }

                if (actualType == typeof(int))
                {
                    return ParseInt(cellValue);
                }

                if (actualType == typeof(long))
                {
                    return ParseLong(cellValue);
                }

                if (actualType == typeof(short))
                {
                    return ParseShort(cellValue);
                }

                if (actualType == typeof(byte))
                {
                    return ParseByte(cellValue);
                }

                if (actualType == typeof(float))
                {
                    return ParseFloat(cellValue);
                }

                if (actualType == typeof(double))
                {
                    return ParseDouble(cellValue);
                }

                if (actualType == typeof(decimal))
                {
                    return ParseDecimal(cellValue);
                }

                if (actualType.IsEnum)
                {
                    return ParseEnum(cellValue, actualType);
                }

                if (actualType.IsArray)
                {
                    return ParseArray(cellValue, actualType);
                }

                if (actualType.IsGenericType && actualType.GetGenericTypeDefinition() == typeof(List<>))
                {
                    return ParseList(cellValue, actualType);
                }

                if (actualType.IsClass || actualType.IsValueType)
                {
                    return ParseCustomType(cellValue, actualType);
                }

                return Convert.ChangeType(cellValue, actualType);
            }
            catch
            {
                return GetDefaultValue(targetType);
            }
        }

        /// <summary>
        /// 从 Excel 常见的真假值表示中解析 bool。
        /// </summary>
        private static bool ParseBool(object value)
        {
            if (value is bool boolValue)
            {
                return boolValue;
            }

            string str = value.ToString().Trim().ToLowerInvariant();
            return str == "true" || str == "1" || str == "yes" || str == "y" || str == "是";
        }

        /// <summary>
        /// 解析 int，并处理 Excel 数值单元格以 double 形式传入的情况。
        /// </summary>
        private static int ParseInt(object value)
        {
            if (value is double d)
            {
                return Convert.ToInt32(d);
            }

            return Convert.ToInt32(value);
        }

        /// <summary>
        /// 解析 long，并处理 Excel 数值单元格以 double 形式传入的情况。
        /// </summary>
        private static long ParseLong(object value)
        {
            if (value is double d)
            {
                return Convert.ToInt64(d);
            }

            return Convert.ToInt64(value);
        }

        /// <summary>
        /// 解析 short，并处理 Excel 数值单元格以 double 形式传入的情况。
        /// </summary>
        private static short ParseShort(object value)
        {
            if (value is double d)
            {
                return Convert.ToInt16(d);
            }

            return Convert.ToInt16(value);
        }

        /// <summary>
        /// 解析 byte，并处理 Excel 数值单元格以 double 形式传入的情况。
        /// </summary>
        private static byte ParseByte(object value)
        {
            if (value is double d)
            {
                return Convert.ToByte(d);
            }

            return Convert.ToByte(value);
        }

        /// <summary>
        /// 从 Excel 单元格中解析 float。
        /// </summary>
        private static float ParseFloat(object value)
        {
            return Convert.ToSingle(value);
        }

        /// <summary>
        /// 从 Excel 单元格中解析 double。
        /// </summary>
        private static double ParseDouble(object value)
        {
            return Convert.ToDouble(value);
        }

        /// <summary>
        /// 从 Excel 单元格中解析 decimal。
        /// </summary>
        private static decimal ParseDecimal(object value)
        {
            return Convert.ToDecimal(value);
        }

        /// <summary>
        /// 通过枚举数值或枚举名称解析枚举值。
        /// </summary>
        private static object ParseEnum(object value, Type enumType)
        {
            string str = value.ToString().Trim();
            if (int.TryParse(str, out int intValue))
            {
                return Enum.ToObject(enumType, intValue);
            }

            return Enum.Parse(enumType, str, ignoreCase: true);
        }

        /// <summary>
        /// 将逗号或分号分隔的值解析为数组。
        /// </summary>
        private static Array ParseArray(object value, Type arrayType)
        {
            Type elementType = arrayType.GetElementType();
            string[] parts = SplitCollection(value?.ToString());
            Array array = Array.CreateInstance(elementType, parts.Length);

            for (int i = 0; i < parts.Length; i++)
            {
                array.SetValue(ParseCellValue(parts[i], elementType), i);
            }

            return array;
        }

        /// <summary>
        /// 将逗号或分号分隔的值解析为泛型列表。
        /// </summary>
        private static object ParseList(object value, Type listType)
        {
            Type elementType = listType.GetGenericArguments()[0];
            string[] parts = SplitCollection(value?.ToString());
            var list = (IList)Activator.CreateInstance(listType);

            for (int i = 0; i < parts.Length; i++)
            {
                list.Add(ParseCellValue(parts[i], elementType));
            }

            return list;
        }

        /// <summary>
        /// 拆分集合文本，并兼容可选的方括号。
        /// </summary>
        private static string[] SplitCollection(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return Array.Empty<string>();
            }

            string normalized = value.Trim();
            if (normalized.StartsWith("[", StringComparison.Ordinal) && normalized.EndsWith("]", StringComparison.Ordinal))
            {
                normalized = normalized.Substring(1, normalized.Length - 2);
            }

            return normalized.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries);
        }

        /// <summary>
        /// 通过特性解析器、静态 Parse 方法或 JSON 兜底解析自定义配置类型。
        /// </summary>
        private static object ParseCustomType(object value, Type targetType)
        {
            if (value == null)
            {
                return GetDefaultValue(targetType);
            }

            string str = value.ToString().Trim();
            if (string.IsNullOrEmpty(str))
            {
                return GetDefaultValue(targetType);
            }

            var parserAttr = targetType.GetCustomAttribute<CustomTypeParserAttribute>();
            if (parserAttr != null)
            {
                var parser = Activator.CreateInstance(parserAttr.ParserType) as ICustomTypeParser;
                if (parser != null && parser.CanParse(targetType))
                {
                    return parser.Parse(str, targetType);
                }
            }

            MethodInfo parseMethod = targetType.GetMethod(
                "Parse",
                BindingFlags.Public | BindingFlags.Static,
                null,
                new[] { typeof(string) },
                null);

            if (parseMethod != null)
            {
                return parseMethod.Invoke(null, new object[] { str });
            }

            return JsonUtility.FromJson(str, targetType);
        }

        /// <summary>
        /// 返回指定类型的默认值，并兼容 Nullable 包装类型。
        /// </summary>
        private static object GetDefaultValue(Type type)
        {
            Type actualType = Nullable.GetUnderlyingType(type) ?? type;
            if (!actualType.IsValueType)
            {
                return null;
            }

            return Activator.CreateInstance(actualType);
        }
    }
}
