using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEngine;
using RangeAttribute = Framework.Data.RangeAttribute;
using ForeignKeyAttribute = Framework.Data.ForeignKeyAttribute;

namespace Editor.ExcelTool
{
    /// <summary>
    /// 数据校验器
    /// 负责校验Excel数据的完整性和正确性
    /// </summary>
    public class DataValidator
    {
        /// <summary>
        /// 校验错误信息
        /// </summary>
        public class ValidationError
        {
            /// <summary>
            /// 错误类型
            /// </summary>
            public ValidationErrorType ErrorType { get; set; }

            /// <summary>
            /// 表名
            /// </summary>
            public string SheetName { get; set; }

            /// <summary>
            /// 行号（从1开始，包含注释行和字段名行）
            /// </summary>
            public int RowIndex { get; set; }

            /// <summary>
            /// 字段名
            /// </summary>
            public string FieldName { get; set; }

            /// <summary>
            /// 错误值
            /// </summary>
            public object ErrorValue { get; set; }

            /// <summary>
            /// 错误消息
            /// </summary>
            public string Message { get; set; }

            public override string ToString()
            {
                return $"[{ErrorType}] 表:{SheetName}, 行:{RowIndex}, 字段:{FieldName}, 值:{ErrorValue}, 消息:{Message}";
            }
        }

        /// <summary>
        /// 校验错误类型
        /// </summary>
        public enum ValidationErrorType
        {
            /// <summary>
            /// 主键重复
            /// </summary>
            DuplicatePrimaryKey,

            /// <summary>
            /// 类型错误
            /// </summary>
            TypeError,

            /// <summary>
            /// 外键无效
            /// </summary>
            InvalidForeignKey,

            /// <summary>
            /// 范围错误
            /// </summary>
            RangeError
        }

        /// <summary>
        /// 校验Excel数据
        /// </summary>
        /// <param name="sheetData">Excel表数据</param>
        /// <param name="configType">配置类型</param>
        /// <param name="allSheetData">所有表数据（用于外键校验）</param>
        /// <returns>校验错误列表</returns>
        public List<ValidationError> Validate(
            ExcelReader.ExcelSheetData sheetData,
            Type configType,
            Dictionary<string, ExcelReader.ExcelSheetData> allSheetData = null)
        {
            var errors = new List<ValidationError>();

            if (sheetData == null || configType == null)
            {
                return errors;
            }

            // 获取配置类的所有属性
            var properties = configType.GetProperties(BindingFlags.Public | BindingFlags.Instance);

            // 查找主键属性
            var primaryKeyProperty = FindPrimaryKeyProperty(properties);

            // 1. 主键重复检查
            if (primaryKeyProperty != null)
            {
                errors.AddRange(ValidatePrimaryKey(sheetData, primaryKeyProperty));
            }

            // 2. 类型检查
            errors.AddRange(ValidateTypes(sheetData, properties));

            // 3. 范围校验
            errors.AddRange(ValidateRanges(sheetData, properties));

            // 4. 外键校验
            if (allSheetData != null)
            {
                errors.AddRange(ValidateForeignKeys(sheetData, properties, allSheetData));
            }

            return errors;
        }

        /// <summary>
        /// 查找主键属性
        /// </summary>
        private PropertyInfo FindPrimaryKeyProperty(PropertyInfo[] properties)
        {
            // 查找带有PrimaryKey特性的属性
            foreach (var prop in properties)
            {
                var pkAttr = prop.GetCustomAttribute<SQLite.PrimaryKeyAttribute>();
                if (pkAttr != null)
                {
                    return prop;
                }
            }

            // 如果没有找到，尝试查找名为"Id"的属性
            return properties.FirstOrDefault(p => 
                p.Name.Equals("Id", StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>
        /// 校验主键重复
        /// </summary>
        private List<ValidationError> ValidatePrimaryKey(
            ExcelReader.ExcelSheetData sheetData,
            PropertyInfo primaryKeyProperty)
        {
            var errors = new List<ValidationError>();
            var primaryKeyValues = new HashSet<object>();
            var primaryKeyFieldName = GetFieldName(primaryKeyProperty);

            // 数据起始行索引（注释行0 + 字段名行1 + 数据起始行2）
            int dataStartRowIndex = 2;

            for (int i = 0; i < sheetData.DataRows.Count; i++)
            {
                var row = sheetData.DataRows[i];
                
                if (!row.ContainsKey(primaryKeyFieldName))
                {
                    continue;
                }

                var cellValue = row[primaryKeyFieldName];
                
                // 跳过空值
                if (cellValue == null || cellValue == DBNull.Value)
                {
                    continue;
                }

                // 尝试解析主键值
                object primaryKeyValue;
                try
                {
                    primaryKeyValue = ExcelReader.ParseCellValue(cellValue, primaryKeyProperty.PropertyType);
                }
                catch
                {
                    // 类型解析错误会在类型检查中处理
                    continue;
                }

                // 检查主键是否重复
                if (primaryKeyValues.Contains(primaryKeyValue))
                {
                    errors.Add(new ValidationError
                    {
                        ErrorType = ValidationErrorType.DuplicatePrimaryKey,
                        SheetName = sheetData.SheetName,
                        RowIndex = dataStartRowIndex + i + 1, // +1转换为从1开始的行号
                        FieldName = primaryKeyFieldName,
                        ErrorValue = primaryKeyValue,
                        Message = $"主键值 '{primaryKeyValue}' 重复"
                    });
                }
                else
                {
                    primaryKeyValues.Add(primaryKeyValue);
                }
            }

            return errors;
        }

        /// <summary>
        /// 校验数据类型
        /// </summary>
        private List<ValidationError> ValidateTypes(
            ExcelReader.ExcelSheetData sheetData,
            PropertyInfo[] properties)
        {
            var errors = new List<ValidationError>();
            int dataStartRowIndex = 2;

            for (int i = 0; i < sheetData.DataRows.Count; i++)
            {
                var row = sheetData.DataRows[i];

                foreach (var prop in properties)
                {
                    var fieldName = GetFieldName(prop);
                    
                    if (!row.ContainsKey(fieldName))
                    {
                        continue;
                    }

                    var cellValue = row[fieldName];
                    
                    // 跳过空值（允许null）
                    if (cellValue == null || cellValue == DBNull.Value)
                    {
                        continue;
                    }

                    // 尝试解析值
                    try
                    {
                        ExcelReader.ParseCellValue(cellValue, prop.PropertyType);
                    }
                    catch (Exception ex)
                    {
                        errors.Add(new ValidationError
                        {
                            ErrorType = ValidationErrorType.TypeError,
                            SheetName = sheetData.SheetName,
                            RowIndex = dataStartRowIndex + i + 1,
                            FieldName = fieldName,
                            ErrorValue = cellValue,
                            Message = $"类型转换失败: 无法将 '{cellValue}' 转换为 {prop.PropertyType.Name}. {ex.Message}"
                        });
                    }
                }
            }

            return errors;
        }

        /// <summary>
        /// 校验数值范围
        /// </summary>
        private List<ValidationError> ValidateRanges(
            ExcelReader.ExcelSheetData sheetData,
            PropertyInfo[] properties)
        {
            var errors = new List<ValidationError>();
            int dataStartRowIndex = 2;

            for (int i = 0; i < sheetData.DataRows.Count; i++)
            {
                var row = sheetData.DataRows[i];

                foreach (var prop in properties)
                {
                    // 检查是否有Range特性
                    var rangeAttr = prop.GetCustomAttribute<RangeAttribute>();
                    if (rangeAttr == null)
                    {
                        continue;
                    }

                    var fieldName = GetFieldName(prop);
                    
                    if (!row.ContainsKey(fieldName))
                    {
                        continue;
                    }

                    var cellValue = row[fieldName];
                    
                    // 跳过空值
                    if (cellValue == null || cellValue == DBNull.Value)
                    {
                        continue;
                    }

                    // 尝试解析值并检查范围
                    try
                    {
                        var parsedValue = ExcelReader.ParseCellValue(cellValue, prop.PropertyType);
                        double numericValue = Convert.ToDouble(parsedValue);

                        if (numericValue < rangeAttr.Min || numericValue > rangeAttr.Max)
                        {
                            errors.Add(new ValidationError
                            {
                                ErrorType = ValidationErrorType.RangeError,
                                SheetName = sheetData.SheetName,
                                RowIndex = dataStartRowIndex + i + 1,
                                FieldName = fieldName,
                                ErrorValue = numericValue,
                                Message = $"值 {numericValue} 超出范围 [{rangeAttr.Min}, {rangeAttr.Max}]"
                            });
                        }
                    }
                    catch (Exception ex)
                    {
                        // 类型转换错误会在类型检查中处理
                        Debug.LogWarning($"[DataValidator] 范围校验时类型转换失败: {ex.Message}");
                    }
                }
            }

            return errors;
        }

        /// <summary>
        /// 校验外键引用
        /// </summary>
        private List<ValidationError> ValidateForeignKeys(
            ExcelReader.ExcelSheetData sheetData,
            PropertyInfo[] properties,
            Dictionary<string, ExcelReader.ExcelSheetData> allSheetData)
        {
            var errors = new List<ValidationError>();
            int dataStartRowIndex = 2;

            for (int i = 0; i < sheetData.DataRows.Count; i++)
            {
                var row = sheetData.DataRows[i];

                foreach (var prop in properties)
                {
                    // 检查是否有ForeignKey特性
                    var fkAttr = prop.GetCustomAttribute<ForeignKeyAttribute>();
                    if (fkAttr == null)
                    {
                        continue;
                    }

                    var fieldName = GetFieldName(prop);
                    
                    if (!row.ContainsKey(fieldName))
                    {
                        continue;
                    }

                    var cellValue = row[fieldName];
                    
                    // 跳过空值
                    if (cellValue == null || cellValue == DBNull.Value)
                    {
                        continue;
                    }

                    // 获取引用表的名称
                    var referenceTableName = GetTableName(fkAttr.ReferenceType);
                    
                    if (!allSheetData.ContainsKey(referenceTableName))
                    {
                        errors.Add(new ValidationError
                        {
                            ErrorType = ValidationErrorType.InvalidForeignKey,
                            SheetName = sheetData.SheetName,
                            RowIndex = dataStartRowIndex + i + 1,
                            FieldName = fieldName,
                            ErrorValue = cellValue,
                            Message = $"引用的表 '{referenceTableName}' 不存在"
                        });
                        continue;
                    }

                    // 获取引用表的主键属性
                    var referenceProperties = fkAttr.ReferenceType.GetProperties(BindingFlags.Public | BindingFlags.Instance);
                    var referencePrimaryKey = FindPrimaryKeyProperty(referenceProperties);

                    if (referencePrimaryKey == null)
                    {
                        errors.Add(new ValidationError
                        {
                            ErrorType = ValidationErrorType.InvalidForeignKey,
                            SheetName = sheetData.SheetName,
                            RowIndex = dataStartRowIndex + i + 1,
                            FieldName = fieldName,
                            ErrorValue = cellValue,
                            Message = $"引用的表 '{referenceTableName}' 没有定义主键"
                        });
                        continue;
                    }

                    // 解析外键值
                    object foreignKeyValue;
                    try
                    {
                        foreignKeyValue = ExcelReader.ParseCellValue(cellValue, prop.PropertyType);
                    }
                    catch
                    {
                        // 类型解析错误会在类型检查中处理
                        continue;
                    }

                    // 检查外键值是否存在于引用表中
                    var referenceSheet = allSheetData[referenceTableName];
                    var referencePrimaryKeyFieldName = GetFieldName(referencePrimaryKey);
                    bool found = false;

                    foreach (var refRow in referenceSheet.DataRows)
                    {
                        if (!refRow.ContainsKey(referencePrimaryKeyFieldName))
                        {
                            continue;
                        }

                        var refCellValue = refRow[referencePrimaryKeyFieldName];
                        if (refCellValue == null || refCellValue == DBNull.Value)
                        {
                            continue;
                        }

                        try
                        {
                            var refPrimaryKeyValue = ExcelReader.ParseCellValue(refCellValue, referencePrimaryKey.PropertyType);
                            
                            // 比较外键值和主键值
                            if (Equals(foreignKeyValue, refPrimaryKeyValue))
                            {
                                found = true;
                                break;
                            }
                        }
                        catch
                        {
                            // 忽略解析错误
                        }
                    }

                    if (!found)
                    {
                        errors.Add(new ValidationError
                        {
                            ErrorType = ValidationErrorType.InvalidForeignKey,
                            SheetName = sheetData.SheetName,
                            RowIndex = dataStartRowIndex + i + 1,
                            FieldName = fieldName,
                            ErrorValue = foreignKeyValue,
                            Message = $"外键值 '{foreignKeyValue}' 在引用表 '{referenceTableName}' 中不存在"
                        });
                    }
                }
            }

            return errors;
        }

        /// <summary>
        /// 获取字段名（从Column特性或属性名）
        /// </summary>
        private string GetFieldName(PropertyInfo property)
        {
            // 尝试从Column特性获取
            var columnAttr = property.GetCustomAttribute<SQLite.ColumnAttribute>();
            if (columnAttr != null && !string.IsNullOrEmpty(columnAttr.Name))
            {
                return columnAttr.Name;
            }

            // 使用属性名
            return property.Name;
        }

        /// <summary>
        /// 获取表名（从Table特性或类名）
        /// </summary>
        private string GetTableName(Type type)
        {
            // 尝试从Table特性获取
            var tableAttr = type.GetCustomAttribute<SQLite.TableAttribute>();
            if (tableAttr != null && !string.IsNullOrEmpty(tableAttr.Name))
            {
                return tableAttr.Name;
            }

            // 使用类名
            return type.Name;
        }
    }
}
