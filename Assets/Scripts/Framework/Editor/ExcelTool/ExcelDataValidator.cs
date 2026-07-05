using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace Editor.ExcelTool
{
    /// <summary>
    /// Excel 数据校验器
    /// 用于校验 Excel 数据的正确性
    /// </summary>
    public class ExcelDataValidator
    {
        /// <summary>
        /// 校验结果
        /// </summary>
        public class ValidationResult
        {
            /// <summary>
            /// 是否通过校验
            /// </summary>
            public bool IsValid { get; set; } = true;

            /// <summary>
            /// 错误列表
            /// </summary>
            public List<string> Errors { get; set; } = new List<string>();

            /// <summary>
            /// 警告列表
            /// </summary>
            public List<string> Warnings { get; set; } = new List<string>();
        }

        /// <summary>
        /// 校验 Excel 表数据
        /// </summary>
        public ValidationResult ValidateSheet(ExcelReader.ExcelSheetData sheetData)
        {
            var result = new ValidationResult();

            if (sheetData == null)
            {
                result.IsValid = false;
                result.Errors.Add("表数据为空");
                return result;
            }

            // 1. 校验表结构
            ValidateStructure(sheetData, result);

            // 2. 校验主键，general 单例表不依赖主键索引。
            if (sheetData.SheetKind != ExcelReader.ExcelSheetKind.General)
            {
                ValidatePrimaryKey(sheetData, result);
            }

            // 3. 校验数据类型
            ValidateDataTypes(sheetData, result);

            // 4. 校验空值
            ValidateNullValues(sheetData, result);

            return result;
        }

        /// <summary>
        /// 校验表结构
        /// </summary>
        private void ValidateStructure(ExcelReader.ExcelSheetData sheetData, ValidationResult result)
        {
            // 检查是否有字段
            if (sheetData.FieldNames.Count == 0)
            {
                result.IsValid = false;
                result.Errors.Add("表中没有定义任何字段");
                return;
            }

            // 检查类型定义是否完整
            if (sheetData.TypeDefinitions.Count != sheetData.FieldNames.Count)
            {
                result.Warnings.Add($"类型定义数量({sheetData.TypeDefinitions.Count})与字段数量({sheetData.FieldNames.Count})不匹配");
            }

            // 检查注释是否完整
            if (sheetData.Comments.Count != sheetData.FieldNames.Count)
            {
                result.Warnings.Add($"注释数量({sheetData.Comments.Count})与字段数量({sheetData.FieldNames.Count})不匹配");
            }

            // 检查字段名是否重复
            var duplicateFields = sheetData.FieldNames
                .GroupBy(f => f)
                .Where(g => g.Count() > 1)
                .Select(g => g.Key)
                .ToList();

            if (duplicateFields.Count > 0)
            {
                result.IsValid = false;
                result.Errors.Add($"存在重复的字段名: {string.Join(", ", duplicateFields)}");
            }

            // 检查字段名是否为空
            for (int i = 0; i < sheetData.FieldNames.Count; i++)
            {
                if (string.IsNullOrWhiteSpace(sheetData.FieldNames[i]))
                {
                    result.IsValid = false;
                    result.Errors.Add($"第 {i + 1} 列的字段名为空");
                }
            }
        }

        /// <summary>
        /// 校验主键
        /// </summary>
        private void ValidatePrimaryKey(ExcelReader.ExcelSheetData sheetData, ValidationResult result)
        {
            if (sheetData.FieldNames.Count == 0 || sheetData.DataRows.Count == 0)
            {
                return;
            }

            // 假设第一个字段是主键
            var primaryKeyField = sheetData.FieldNames[0];
            var primaryKeys = new HashSet<string>();

            for (int i = 0; i < sheetData.DataRows.Count; i++)
            {
                var row = sheetData.DataRows[i];
                var keyValue = row.ContainsKey(primaryKeyField) ? row[primaryKeyField]?.ToString() : null;

                // 检查主键是否为空
                if (string.IsNullOrEmpty(keyValue))
                {
                    result.IsValid = false;
                    result.Errors.Add($"第 {i + 4} 行: 主键 '{primaryKeyField}' 为空");
                    continue;
                }

                // 检查主键是否重复
                if (primaryKeys.Contains(keyValue))
                {
                    result.IsValid = false;
                    result.Errors.Add($"第 {i + 4} 行: 主键重复 ({primaryKeyField}={keyValue})");
                }
                else
                {
                    primaryKeys.Add(keyValue);
                }
            }
        }

        /// <summary>
        /// 校验数据类型
        /// </summary>
        private void ValidateDataTypes(ExcelReader.ExcelSheetData sheetData, ValidationResult result)
        {
            if (sheetData.TypeDefinitions.Count == 0 || sheetData.DataRows.Count == 0)
            {
                return;
            }

            for (int rowIndex = 0; rowIndex < sheetData.DataRows.Count; rowIndex++)
            {
                var row = sheetData.DataRows[rowIndex];

                for (int colIndex = 0; colIndex < sheetData.FieldNames.Count; colIndex++)
                {
                    var fieldName = sheetData.FieldNames[colIndex];
                    var typeName = colIndex < sheetData.TypeDefinitions.Count 
                        ? sheetData.TypeDefinitions[colIndex] 
                        : "string";

                    if (!row.ContainsKey(fieldName))
                    {
                        continue;
                    }

                    var value = row[fieldName];
                    if (value == null || value == DBNull.Value)
                    {
                        continue;
                    }

                    // 校验类型
                    if (!ValidateValueType(value, typeName, out string error))
                    {
                        result.Warnings.Add($"第 {GetExcelRowNumber(sheetData, rowIndex, colIndex)} 行, 列 '{fieldName}': {error}");
                    }
                }
            }
        }

        /// <summary>
        /// 校验单个值的类型
        /// </summary>
        private bool ValidateValueType(object value, string typeName, out string error)
        {
            error = null;
            var strValue = value.ToString().Trim();

            if (string.IsNullOrEmpty(strValue))
            {
                return true; // 空值跳过
            }

            try
            {
                switch (typeName.ToLower())
                {
                    case "int":
                        if (!int.TryParse(strValue, out _))
                        {
                            // 尝试从 double 转换（Excel 数字默认是 double）
                            if (value is double d)
                            {
                                return true;
                            }
                            error = $"无法转换为 int 类型，值: '{strValue}'";
                            return false;
                        }
                        break;

                    case "long":
                        if (!long.TryParse(strValue, out _))
                        {
                            if (value is double d)
                            {
                                return true;
                            }
                            error = $"无法转换为 long 类型，值: '{strValue}'";
                            return false;
                        }
                        break;

                    case "float":
                        if (!float.TryParse(strValue, out _))
                        {
                            error = $"无法转换为 float 类型，值: '{strValue}'";
                            return false;
                        }
                        break;

                    case "double":
                        if (!double.TryParse(strValue, out _))
                        {
                            error = $"无法转换为 double 类型，值: '{strValue}'";
                            return false;
                        }
                        break;

                    case "bool":
                        var lowerValue = strValue.ToLower();
                        if (lowerValue != "true" && lowerValue != "false" && 
                            lowerValue != "1" && lowerValue != "0" &&
                            lowerValue != "yes" && lowerValue != "no" &&
                            lowerValue != "是" && lowerValue != "否")
                        {
                            error = $"无法转换为 bool 类型，值: '{strValue}'";
                            return false;
                        }
                        break;

                    case "int[]":
                    case "float[]":
                    case "string[]":
                    case "List<int>":
                    case "List<float>":
                    case "List<string>":
                        // 数组/列表类型，检查是否可以分割
                        if (!strValue.Contains(",") && !strValue.Contains(";"))
                        {
                            // 单个元素也是有效的数组/列表
                        }
                        break;

                    case "string":
                        // 字符串类型总是有效的
                        break;

                    default:
                        // 自定义类型，暂时跳过校验
                        break;
                }

                return true;
            }
            catch (Exception ex)
            {
                error = $"类型校验异常: {ex.Message}";
                return false;
            }
        }

        /// <summary>
        /// 校验空值
        /// </summary>
        private void ValidateNullValues(ExcelReader.ExcelSheetData sheetData, ValidationResult result)
        {
            if (sheetData.DataRows.Count == 0)
            {
                return;
            }

            for (int rowIndex = 0; rowIndex < sheetData.DataRows.Count; rowIndex++)
            {
                var row = sheetData.DataRows[rowIndex];
                var emptyFields = new List<string>();

                foreach (var fieldName in sheetData.FieldNames)
                {
                    if (!row.ContainsKey(fieldName))
                    {
                        emptyFields.Add(fieldName);
                        continue;
                    }

                    var value = row[fieldName];
                    if (value == null || value == DBNull.Value || string.IsNullOrWhiteSpace(value.ToString()))
                    {
                        emptyFields.Add(fieldName);
                    }
                }

                // 如果整行都是空的，警告
                if (emptyFields.Count == sheetData.FieldNames.Count)
                {
                    result.Warnings.Add($"第 {GetExcelRowNumber(sheetData, rowIndex, 0)} 行: 整行数据为空");
                }
                // 如果有部分字段为空，记录警告（除了主键）
                else if (emptyFields.Count > 0)
                {
                    if (sheetData.SheetKind == ExcelReader.ExcelSheetKind.General)
                    {
                        foreach (var fieldName in emptyFields)
                        {
                            int fieldIndex = sheetData.FieldNames.IndexOf(fieldName);
                            result.Warnings.Add($"第 {GetExcelRowNumber(sheetData, rowIndex, fieldIndex)} 行: general 配置 '{fieldName}' 的值为空");
                        }

                        continue;
                    }

                    var nonPrimaryKeyEmpty = emptyFields.Where(f => f != sheetData.FieldNames[0]).ToList();
                    if (nonPrimaryKeyEmpty.Count > 0)
                    {
                        result.Warnings.Add($"第 {rowIndex + 4} 行: 以下字段为空: {string.Join(", ", nonPrimaryKeyEmpty)}");
                    }
                }
            }
        }

        /// <summary>
        /// 返回源 Excel 行号；general 表折叠为单行后，需要按字段索引还原到原始键值行。
        /// </summary>
        private int GetExcelRowNumber(ExcelReader.ExcelSheetData sheetData, int rowIndex, int colIndex)
        {
            if (sheetData != null && sheetData.SheetKind == ExcelReader.ExcelSheetKind.General)
            {
                return colIndex + 4;
            }

            return rowIndex + 4;
        }

        /// <summary>
        /// 校验数据范围（如果有 Range 定义）
        /// </summary>
        public void ValidateRanges(ExcelReader.ExcelSheetData sheetData, Dictionary<string, (double min, double max)> ranges, ValidationResult result)
        {
            if (ranges == null || ranges.Count == 0)
            {
                return;
            }

            for (int rowIndex = 0; rowIndex < sheetData.DataRows.Count; rowIndex++)
            {
                var row = sheetData.DataRows[rowIndex];

                foreach (var range in ranges)
                {
                    var fieldName = range.Key;
                    var (min, max) = range.Value;

                    if (!row.ContainsKey(fieldName))
                    {
                        continue;
                    }

                    var value = row[fieldName];
                    if (value == null || value == DBNull.Value)
                    {
                        continue;
                    }

                    if (double.TryParse(value.ToString(), out double numValue))
                    {
                        if (numValue < min || numValue > max)
                        {
                            result.Warnings.Add($"第 {rowIndex + 4} 行, 列 '{fieldName}': 值 {numValue} 超出范围 [{min}, {max}]");
                        }
                    }
                }
            }
        }

        /// <summary>
        /// 校验外键引用（如果有 ForeignKey 定义）
        /// </summary>
        public void ValidateForeignKeys(
            ExcelReader.ExcelSheetData sheetData, 
            Dictionary<string, HashSet<string>> foreignKeyReferences, 
            ValidationResult result)
        {
            if (foreignKeyReferences == null || foreignKeyReferences.Count == 0)
            {
                return;
            }

            for (int rowIndex = 0; rowIndex < sheetData.DataRows.Count; rowIndex++)
            {
                var row = sheetData.DataRows[rowIndex];

                foreach (var fk in foreignKeyReferences)
                {
                    var fieldName = fk.Key;
                    var validKeys = fk.Value;

                    if (!row.ContainsKey(fieldName))
                    {
                        continue;
                    }

                    var value = row[fieldName];
                    if (value == null || value == DBNull.Value)
                    {
                        continue;
                    }

                    var keyValue = value.ToString();
                    if (!string.IsNullOrEmpty(keyValue) && !validKeys.Contains(keyValue))
                    {
                        result.Warnings.Add($"第 {rowIndex + 4} 行, 列 '{fieldName}': 外键 '{keyValue}' 不存在");
                    }
                }
            }
        }
    }
}
