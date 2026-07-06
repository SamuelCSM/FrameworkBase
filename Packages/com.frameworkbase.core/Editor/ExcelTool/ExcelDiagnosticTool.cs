using System;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace Editor.ExcelTool
{
    /// <summary>
    /// Excel 诊断工具
    /// 用于诊断 Excel 文件读取问题
    /// </summary>
    public class ExcelDiagnosticTool : EditorWindow
    {
        private string _excelPath = "";
        private Vector2 _scrollPosition;
        private string _diagnosticResult = "";

        [MenuItem("Tools/Excel/Excel 诊断工具")]
        public static void ShowWindow()
        {
            var window = GetWindow<ExcelDiagnosticTool>("Excel 诊断工具");
            window.minSize = new Vector2(600, 400);
            window.Show();
        }

        private void OnGUI()
        {
            EditorGUILayout.Space(10);
            EditorGUILayout.LabelField("Excel 诊断工具", EditorStyles.boldLabel);
            EditorGUILayout.HelpBox(
                "此工具用于诊断 Excel 文件读取问题。\n" +
                "它会检查文件是否存在、是否被占用、格式是否正确等。",
                MessageType.Info);

            EditorGUILayout.Space(10);

            // Excel 路径
            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField("Excel 路径:", GUILayout.Width(80));
            _excelPath = EditorGUILayout.TextField(_excelPath);
            if (GUILayout.Button("浏览", GUILayout.Width(60)))
            {
                var path = EditorUtility.OpenFilePanel("选择 Excel 文件", Application.dataPath, "xlsx");
                if (!string.IsNullOrEmpty(path))
                {
                    _excelPath = path;
                }
            }
            EditorGUILayout.EndHorizontal();

            EditorGUILayout.Space(10);

            if (GUILayout.Button("开始诊断", GUILayout.Height(30)))
            {
                RunDiagnostic();
            }

            EditorGUILayout.Space(10);

            // 显示诊断结果
            if (!string.IsNullOrEmpty(_diagnosticResult))
            {
                EditorGUILayout.LabelField("诊断结果:", EditorStyles.boldLabel);
                _scrollPosition = EditorGUILayout.BeginScrollView(_scrollPosition);
                EditorGUILayout.TextArea(_diagnosticResult, GUILayout.ExpandHeight(true));
                EditorGUILayout.EndScrollView();
            }
        }

        private void RunDiagnostic()
        {
            var result = new System.Text.StringBuilder();
            result.AppendLine("========================================");
            result.AppendLine("Excel 文件诊断报告");
            result.AppendLine("========================================");
            result.AppendLine($"时间: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            result.AppendLine($"文件路径: {_excelPath}");
            result.AppendLine();

            try
            {
                // 1. 检查路径是否为空
                result.AppendLine("【检查1】路径检查");
                if (string.IsNullOrEmpty(_excelPath))
                {
                    result.AppendLine("✗ 失败: 路径为空");
                    _diagnosticResult = result.ToString();
                    return;
                }
                result.AppendLine("✓ 通过: 路径不为空");
                result.AppendLine();

                // 2. 检查文件是否存在
                result.AppendLine("【检查2】文件存在性检查");
                if (!File.Exists(_excelPath))
                {
                    result.AppendLine("✗ 失败: 文件不存在");
                    result.AppendLine($"  请确认路径是否正确: {_excelPath}");
                    _diagnosticResult = result.ToString();
                    return;
                }
                result.AppendLine("✓ 通过: 文件存在");
                result.AppendLine();

                // 3. 检查文件扩展名
                result.AppendLine("【检查3】文件格式检查");
                var extension = Path.GetExtension(_excelPath).ToLower();
                if (extension != ".xlsx" && extension != ".xls")
                {
                    result.AppendLine($"✗ 警告: 文件扩展名为 {extension}，建议使用 .xlsx 格式");
                }
                else
                {
                    result.AppendLine($"✓ 通过: 文件格式为 {extension}");
                }
                result.AppendLine();

                // 4. 检查文件大小
                result.AppendLine("【检查4】文件大小检查");
                var fileInfo = new FileInfo(_excelPath);
                result.AppendLine($"  文件大小: {fileInfo.Length} 字节 ({fileInfo.Length / 1024.0:F2} KB)");
                if (fileInfo.Length == 0)
                {
                    result.AppendLine("✗ 失败: 文件大小为 0，文件可能损坏");
                    _diagnosticResult = result.ToString();
                    return;
                }
                result.AppendLine("✓ 通过: 文件大小正常");
                result.AppendLine();

                // 5. 检查文件是否被占用
                result.AppendLine("【检查5】文件占用检查");
                try
                {
                    using (var stream = File.Open(_excelPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                    {
                        result.AppendLine("✓ 通过: 文件可以正常打开");
                    }
                }
                catch (IOException ex)
                {
                    result.AppendLine("✗ 失败: 文件被占用或无法访问");
                    result.AppendLine($"  错误信息: {ex.Message}");
                    result.AppendLine("  解决方案: 请关闭所有打开此文件的程序（如 Excel、WPS）");
                    _diagnosticResult = result.ToString();
                    return;
                }
                result.AppendLine();

                // 6. 尝试读取 Excel
                result.AppendLine("【检查6】Excel 读取测试");
                try
                {
                    var reader = new ExcelReader();
                    var sheets = reader.ReadExcel(_excelPath);

                    if (sheets == null)
                    {
                        result.AppendLine("✗ 失败: ReadExcel 返回 null");
                        result.AppendLine("  这通常不应该发生，请检查 ExcelReader 实现");
                    }
                    else if (sheets.Count == 0)
                    {
                        result.AppendLine("✗ 失败: 没有读取到任何工作表");
                        result.AppendLine("  可能的原因:");
                        result.AppendLine("  1. 所有工作表都是空的");
                        result.AppendLine("  2. 工作表行数少于4行");
                        result.AppendLine("  3. 字段名行（第2行）为空");
                    }
                    else
                    {
                        result.AppendLine($"✓ 通过: 成功读取 {sheets.Count} 个工作表");
                        result.AppendLine();
                        result.AppendLine("【工作表详情】");
                        
                        for (int i = 0; i < sheets.Count; i++)
                        {
                            var sheet = sheets[i];
                            result.AppendLine($"\n工作表 {i + 1}: {sheet.SheetName}");
                            result.AppendLine($"  - 字段数: {sheet.FieldNames.Count}");
                            result.AppendLine($"  - 数据行数: {sheet.DataRows.Count}");
                            
                            if (sheet.FieldNames.Count > 0)
                            {
                                result.AppendLine($"  - 字段列表:");
                                for (int j = 0; j < sheet.FieldNames.Count; j++)
                                {
                                    var fieldName = sheet.FieldNames[j];
                                    var typeName = j < sheet.TypeDefinitions.Count ? sheet.TypeDefinitions[j] : "未定义";
                                    var comment = j < sheet.Comments.Count ? sheet.Comments[j] : "无";
                                    result.AppendLine($"    {j + 1}. {fieldName} ({typeName}) - {comment}");
                                }
                            }
                            else
                            {
                                result.AppendLine("  ✗ 警告: 没有字段定义");
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    result.AppendLine("✗ 失败: 读取 Excel 时发生异常");
                    result.AppendLine($"  异常类型: {ex.GetType().Name}");
                    result.AppendLine($"  错误信息: {ex.Message}");
                    result.AppendLine($"  堆栈跟踪:\n{ex.StackTrace}");
                }
                result.AppendLine();

                // 7. 总结
                result.AppendLine("========================================");
                result.AppendLine("诊断完成");
                result.AppendLine("========================================");
            }
            catch (Exception ex)
            {
                result.AppendLine();
                result.AppendLine("========================================");
                result.AppendLine("诊断过程中发生未预期的错误");
                result.AppendLine("========================================");
                result.AppendLine($"异常类型: {ex.GetType().Name}");
                result.AppendLine($"错误信息: {ex.Message}");
                result.AppendLine($"堆栈跟踪:\n{ex.StackTrace}");
            }

            _diagnosticResult = result.ToString();
            Debug.Log(_diagnosticResult);
        }
    }
}
