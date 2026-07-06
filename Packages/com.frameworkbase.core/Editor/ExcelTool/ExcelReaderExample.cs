using System;
using UnityEditor;
using UnityEngine;

namespace Editor.ExcelTool
{
    /// <summary>
    /// ExcelReader使用示例
    /// </summary>
    public class ExcelReaderExample
    {
        /// <summary>
        /// 测试读取Excel文件
        /// </summary>
        [MenuItem("Tools/Excel/Test Read Excel")]
        public static void TestReadExcel()
        {
            // 打开文件选择对话框
            string filePath = EditorUtility.OpenFilePanel("选择Excel文件", Application.dataPath, "xlsx");
            
            if (string.IsNullOrEmpty(filePath))
            {
                Debug.Log("[ExcelReaderExample] 未选择文件");
                return;
            }

            try
            {
                // 创建读取器
                var reader = new ExcelReader();
                
                // 读取Excel文件
                var sheets = reader.ReadExcel(filePath);
                
                Debug.Log($"[ExcelReaderExample] 成功读取Excel文件，共 {sheets.Count} 个工作表");
                
                // 遍历所有工作表
                foreach (var sheet in sheets)
                {
                    Debug.Log($"\n========== 工作表: {sheet.SheetName} ==========");
                    Debug.Log($"字段数: {sheet.FieldNames.Count}");
                    Debug.Log($"数据行数: {sheet.DataRows.Count}");
                    
                    // 打印字段名和注释
                    Debug.Log("\n字段列表:");
                    for (int i = 0; i < sheet.FieldNames.Count; i++)
                    {
                        var fieldName = sheet.FieldNames[i];
                        var comment = i < sheet.Comments.Count ? sheet.Comments[i] : "";
                        Debug.Log($"  {fieldName} ({comment})");
                    }
                    
                    // 打印前5行数据
                    Debug.Log("\n数据预览（前5行）:");
                    int rowCount = Math.Min(5, sheet.DataRows.Count);
                    for (int i = 0; i < rowCount; i++)
                    {
                        var row = sheet.DataRows[i];
                        Debug.Log($"  第{i + 1}行:");
                        foreach (var field in sheet.FieldNames)
                        {
                            var value = row.ContainsKey(field) ? row[field] : null;
                            Debug.Log($"    {field} = {value}");
                        }
                    }
                }
                
                Debug.Log("\n[ExcelReaderExample] 测试完成");
            }
            catch (Exception ex)
            {
                Debug.LogError($"[ExcelReaderExample] 测试失败: {ex.Message}\n{ex.StackTrace}");
            }
        }

        /// <summary>
        /// 测试类型解析
        /// </summary>
        [MenuItem("Tools/Excel/Test Type Parsing")]
        public static void TestTypeParsing()
        {
            Debug.Log("========== 测试类型解析 ==========\n");

            // 测试基础类型
            TestParse("123", typeof(int), "int");
            TestParse("123.45", typeof(float), "float");
            TestParse("123.456789", typeof(double), "double");
            TestParse("true", typeof(bool), "bool");
            TestParse("1", typeof(bool), "bool (1)");
            TestParse("Hello", typeof(string), "string");

            // 测试数组
            TestParse("[1,2,3]", typeof(int[]), "int[]");
            TestParse("1,2,3", typeof(int[]), "int[] (无括号)");
            TestParse("[1.1,2.2,3.3]", typeof(float[]), "float[]");
            TestParse("[a,b,c]", typeof(string[]), "string[]");

            // 测试枚举（需要定义枚举类型）
            // TestParse("1", typeof(ItemType), "enum");

            Debug.Log("\n测试完成");
        }

        private static void TestParse(object input, Type targetType, string description)
        {
            try
            {
                var result = ExcelReader.ParseCellValue(input, targetType);
                Debug.Log($"✓ {description}: '{input}' -> {result} ({result?.GetType().Name})");
            }
            catch (Exception ex)
            {
                Debug.LogError($"✗ {description}: '{input}' -> 解析失败: {ex.Message}");
            }
        }
    }
}
