using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace Editor.ExcelTool
{
    /// <summary>
    /// 数据校验器使用示例
    /// </summary>
    public class DataValidatorExample
    {
        /// <summary>
        /// 示例：校验单个Excel表
        /// </summary>
        [MenuItem("Tools/Excel/Validate Example")]
        public static void ValidateExample()
        {
            // 1. 读取Excel文件
            var excelReader = new ExcelReader();
            var excelPath = "Assets/RefData_Excel/ItemConfig.xlsx"; // 示例路径
            
            try
            {
                var sheets = excelReader.ReadExcel(excelPath);
                
                if (sheets.Count == 0)
                {
                    Debug.LogWarning("没有读取到任何工作表");
                    return;
                }

                // 2. 创建数据校验器
                var validator = new DataValidator();

                // 3. 校验每个工作表
                foreach (var sheet in sheets)
                {
                    Debug.Log($"开始校验工作表: {sheet.SheetName}");

                    // 假设配置类型为ItemConfig（需要根据实际情况替换）
                    // var configType = typeof(ItemConfig);
                    
                    // 校验数据（不包含外键校验）
                    // var errors = validator.Validate(sheet, configType);

                    // 如果需要外键校验，需要传入所有表数据
                    // var allSheetData = new Dictionary<string, ExcelReader.ExcelSheetData>();
                    // foreach (var s in sheets)
                    // {
                    //     allSheetData[s.SheetName] = s;
                    // }
                    // var errors = validator.Validate(sheet, configType, allSheetData);

                    // 输出校验结果
                    // if (errors.Count == 0)
                    // {
                    //     Debug.Log($"工作表 {sheet.SheetName} 校验通过");
                    // }
                    // else
                    // {
                    //     Debug.LogError($"工作表 {sheet.SheetName} 校验失败，共 {errors.Count} 个错误:");
                    //     foreach (var error in errors)
                    //     {
                    //         Debug.LogError(error.ToString());
                    //     }
                    // }
                }

                Debug.Log("校验完成");
            }
            catch (Exception ex)
            {
                Debug.LogError($"校验失败: {ex.Message}");
            }
        }

        /// <summary>
        /// 示例：批量校验多个Excel文件
        /// </summary>
        public static void ValidateBatch()
        {
            var excelFiles = new[]
            {
                "Assets/RefData_Excel/ItemConfig.xlsx",
                "Assets/RefData_Excel/SkillConfig.xlsx",
                // 添加更多文件...
            };

            var validator = new DataValidator();
            var allSheetData = new Dictionary<string, ExcelReader.ExcelSheetData>();
            var allErrors = new List<DataValidator.ValidationError>();

            // 第一遍：读取所有Excel文件
            foreach (var excelPath in excelFiles)
            {
                try
                {
                    var excelReader = new ExcelReader();
                    var sheets = excelReader.ReadExcel(excelPath);

                    foreach (var sheet in sheets)
                    {
                        allSheetData[sheet.SheetName] = sheet;
                    }
                }
                catch (Exception ex)
                {
                    Debug.LogError($"读取Excel文件失败: {excelPath}, 错误: {ex.Message}");
                }
            }

            // 第二遍：校验所有表（包含外键校验）
            foreach (var kvp in allSheetData)
            {
                var sheetName = kvp.Key;
                var sheetData = kvp.Value;

                Debug.Log($"开始校验工作表: {sheetName}");

                // 根据表名获取配置类型（需要实现映射逻辑）
                // var configType = GetConfigTypeBySheetName(sheetName);
                // if (configType == null)
                // {
                //     Debug.LogWarning($"未找到工作表 {sheetName} 对应的配置类型");
                //     continue;
                // }

                // 校验数据
                // var errors = validator.Validate(sheetData, configType, allSheetData);
                // allErrors.AddRange(errors);
            }

            // 输出总结
            if (allErrors.Count == 0)
            {
                Debug.Log("所有表校验通过");
            }
            else
            {
                Debug.LogError($"校验失败，共 {allErrors.Count} 个错误:");
                
                // 按错误类型分组
                var errorsByType = new Dictionary<DataValidator.ValidationErrorType, int>();
                foreach (var error in allErrors)
                {
                    if (!errorsByType.ContainsKey(error.ErrorType))
                    {
                        errorsByType[error.ErrorType] = 0;
                    }
                    errorsByType[error.ErrorType]++;
                }

                foreach (var kvp in errorsByType)
                {
                    Debug.LogError($"  {kvp.Key}: {kvp.Value} 个");
                }

                // 输出详细错误
                foreach (var error in allErrors)
                {
                    Debug.LogError(error.ToString());
                }
            }
        }

        /// <summary>
        /// 示例：根据表名获取配置类型
        /// </summary>
        private static Type GetConfigTypeBySheetName(string sheetName)
        {
            // 这里需要实现表名到配置类型的映射
            // 可以使用命名约定、配置文件或反射等方式
            
            // 示例：简单的命名约定
            // if (sheetName == "ItemConfig")
            // {
            //     return typeof(ItemConfig);
            // }
            // else if (sheetName == "SkillConfig")
            // {
            //     return typeof(SkillConfig);
            // }

            return null;
        }
    }
}
