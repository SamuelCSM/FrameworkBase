using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace Editor.ExcelTool
{
    /// <summary>
    /// 配置表编辑器窗口
    /// 用于创建和修改 Excel 配置表
    /// 注意：此版本仅支持生成代码和读取现有表，不支持直接创建/修改 Excel 文件
    /// 如需创建/修改 Excel，请使用外部工具（如 Microsoft Excel、WPS）
    /// </summary>
    public class ConfigTableEditorWindow : EditorWindow
    {
        private const string PrefsPrefix = "ClientBase.ConfigTableEditor.";
        private const string ModePrefsKey = PrefsPrefix + "Mode";
        private const string ExcelPathPrefsKey = PrefsPrefix + "ExcelPath";
        private const string TableNamePrefsKey = PrefsPrefix + "TableName";
        private const string ClassNamePrefsKey = PrefsPrefix + "ClassName";
        private const string CodeTargetPrefsKey = PrefsPrefix + "CodeTarget";
        /// <summary>
        /// 记录配置表类型选择的 EditorPrefs 键。
        /// </summary>
        private const string TableKindPrefsKey = PrefsPrefix + "TableKind";

        /// <summary>
        /// 字段列表各列固定宽度，保证表头和行内容对齐。
        /// </summary>
        private const float PrimaryKeyColumnWidth = 40f;
        private const float FieldNameColumnWidth = 100f;
        private const float TypeColumnWidth = 190f;
        private const float CommentColumnWidth = 150f;
        private const float OperationColumnWidth = 100f;

        /// <summary>
        /// 字段定义
        /// </summary>
        [Serializable]
        public class FieldDefinition
        {
            public string FieldName = "";
            public string TypeName = "string";
            public string Comment = "";
            public bool IsPrimaryKey = false;
        }

        private enum EditorMode
        {
            LoadAndGenerate,  // 加载现有表并生成代码
            CreateTemplate    // 创建模板定义（不创建 Excel 文件）
        }

        private enum CodeAssemblyTarget
        {
            FrameworkBootstrap, // 启动前可读（language / loading_tips 等）
            HotUpdate           // 业务期热更逻辑
        }

        /// <summary>
        /// 配置表模板和代码生成使用的表结构类型。
        /// </summary>
        private enum ConfigTableKind
        {
            Table,
            General
        }

        private EditorMode _mode = EditorMode.LoadAndGenerate;
        private CodeAssemblyTarget _codeTarget = CodeAssemblyTarget.FrameworkBootstrap;
        /// <summary>
        /// 当前模板或生成目标的表类型；general 会按 Key/ValueType/Value 纵向布局导出。
        /// </summary>
        private ConfigTableKind _tableKind = ConfigTableKind.Table;
        private string _excelPath = "";
        private string _tableName = "";
        private string _className = "";  // 新增：类名输入
        private List<FieldDefinition> _fields = new List<FieldDefinition>();
        private Vector2 _scrollPosition;

        // 常用类型选项（用于快速选择）
        private readonly string[] _commonTypes = new[]
        {
            "int", "long", "float", "double", "string", "bool",
            "int[]", "float[]", "string[]",
            "Vector2", "Vector3", "Color"
        };

        /// <summary>
        /// 从 Unity 菜单打开配置表编辑器窗口。
        /// </summary>
        [MenuItem("Tools/Excel/配置表编辑器")]
        public static void ShowWindow()
        {
            var window = GetWindow<ConfigTableEditorWindow>("配置表编辑器");
            window.minSize = new Vector2(600, 400);
            window.Show();
        }

        /// <summary>
        /// 窗口启用时恢复编辑器偏好，并在必要时准备默认字段。
        /// </summary>
        private void OnEnable()
        {
            LoadPrefs();

            // 初始化时添加一个默认字段
            if (_fields.Count == 0)
            {
                _fields.Add(new FieldDefinition
                {
                    FieldName = "Id",
                    TypeName = "int",
                    Comment = "主键ID",
                    IsPrimaryKey = true
                });
            }
        }

        /// <summary>
        /// 窗口禁用时保存编辑器偏好。
        /// </summary>
        private void OnDisable()
        {
            SavePrefs();
        }

        /// <summary>
        /// 绘制完整编辑器界面，用于加载现有表、定义字段和生成代码。
        /// </summary>
        private void OnGUI()
        {
            EditorGUILayout.Space(10);

            // 模式选择
            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField("操作模式:", GUILayout.Width(80));
            EditorGUI.BeginChangeCheck();
            _mode = (EditorMode)EditorGUILayout.EnumPopup(_mode, GUILayout.Width(150));
            if (EditorGUI.EndChangeCheck())
                SavePrefs();
            EditorGUILayout.EndHorizontal();

            EditorGUILayout.Space(5);

            // Excel 路径
            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField("Excel 路径:", GUILayout.Width(80));
            EditorGUI.BeginChangeCheck();
            _excelPath = EditorGUILayout.TextField(_excelPath);
            if (EditorGUI.EndChangeCheck())
                SavePrefs();
            if (GUILayout.Button("浏览", GUILayout.Width(60)))
            {
                var path = EditorUtility.OpenFilePanel("选择 Excel 文件", Application.dataPath, "xlsx");
                if (!string.IsNullOrEmpty(path))
                {
                    _excelPath = path;
                    SavePrefs();
                }
            }
            EditorGUILayout.EndHorizontal();

            // 表名
            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField("表名:", GUILayout.Width(80));
            EditorGUI.BeginChangeCheck();
            _tableName = EditorGUILayout.TextField(_tableName);
            if (EditorGUI.EndChangeCheck())
            {
                if (ExcelReader.IsGeneralSheet(_tableName))
                {
                    _tableKind = ConfigTableKind.General;
                }

                SavePrefs();
            }
            EditorGUILayout.EndHorizontal();

            // 表类型
            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField("表类型:", GUILayout.Width(80));
            EditorGUI.BeginChangeCheck();
            _tableKind = (ConfigTableKind)EditorGUILayout.EnumPopup(_tableKind, GUILayout.Width(150));
            if (EditorGUI.EndChangeCheck())
                SavePrefs();
            EditorGUILayout.EndHorizontal();

            // 类名
            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField("类名:", GUILayout.Width(80));
            EditorGUI.BeginChangeCheck();
            _className = EditorGUILayout.TextField(_className);
            if (EditorGUI.EndChangeCheck())
                SavePrefs();
            EditorGUILayout.EndHorizontal();

            // 代码归属
            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField("代码归属:", GUILayout.Width(80));
            EditorGUI.BeginChangeCheck();
            _codeTarget = (CodeAssemblyTarget)EditorGUILayout.EnumPopup(_codeTarget, GUILayout.Width(180));
            if (EditorGUI.EndChangeCheck())
                SavePrefs();
            EditorGUILayout.EndHorizontal();

            string targetTip = _codeTarget == CodeAssemblyTarget.FrameworkBootstrap
                ? "FrameworkBootstrap：生成到 Framework 目录，适合启动前就要读取的表（如 language / loading_tips）。"
                : "HotUpdate：生成到 HotUpdate 目录，适合进入热更逻辑后才会读取的业务表。";
            EditorGUILayout.HelpBox(targetTip, MessageType.Info);

            if (IsGeneralConfigKind())
            {
                EditorGUILayout.HelpBox("General 表会导出为 Key / ValueType / Value / Comment 纵向模板，工作表名必须以 _general 结尾。", MessageType.Info);
            }

            EditorGUILayout.Space(10);

            // 根据模式显示不同的按钮
            if (_mode == EditorMode.LoadAndGenerate)
            {
                EditorGUILayout.HelpBox(
                    "从现有 Excel 文件加载表结构并生成代码。\n" +
                    "如需创建新的 Excel 文件，请使用 Microsoft Excel 或 WPS 等工具。",
                    MessageType.Info);

                EditorGUILayout.Space(5);

                if (GUILayout.Button("加载 Excel 表结构", GUILayout.Height(30)))
                {
                    LoadExistingTable();
                }
            }
            else
            {
                EditorGUILayout.HelpBox(
                    "定义配置表结构，生成代码和 Excel 模板说明。\n" +
                    "然后使用 Excel 工具按照模板格式创建 Excel 文件。",
                    MessageType.Info);
            }

            EditorGUILayout.Space(10);

            // 字段列表
            EditorGUILayout.LabelField("字段定义:", EditorStyles.boldLabel);
            EditorGUILayout.Space(5);

            _scrollPosition = EditorGUILayout.BeginScrollView(_scrollPosition);

            // 表头
            EditorGUILayout.BeginHorizontal();
            if (!IsGeneralConfigKind())
            {
                EditorGUILayout.LabelField("主键", GUILayout.Width(PrimaryKeyColumnWidth));
            }

            EditorGUILayout.LabelField("字段名", GUILayout.Width(FieldNameColumnWidth));
            EditorGUILayout.LabelField("类型", GUILayout.Width(TypeColumnWidth));
            EditorGUILayout.LabelField("注释", GUILayout.Width(CommentColumnWidth));
            EditorGUILayout.LabelField("操作", GUILayout.Width(OperationColumnWidth));
            EditorGUILayout.EndHorizontal();

            EditorGUILayout.Space(3);

            // 字段列表
            for (int i = 0; i < _fields.Count; i++)
            {
                DrawFieldRow(i);
            }

            EditorGUILayout.EndScrollView();

            EditorGUILayout.Space(10);

            // 添加字段按钮
            if (GUILayout.Button("+ 添加字段", GUILayout.Height(25)))
            {
                _fields.Add(new FieldDefinition());
            }

            EditorGUILayout.Space(10);

            // 操作按钮
            EditorGUILayout.BeginHorizontal();
            
            if (GUILayout.Button("生成代码", GUILayout.Height(35)))
            {
                GenerateCode();
            }

            if (_mode == EditorMode.CreateTemplate)
            {
                EditorGUILayout.BeginHorizontal();
                
                if (GUILayout.Button("导出模板说明", GUILayout.Height(35)))
                {
                    ExportTemplateGuide();
                }
                
                if (GUILayout.Button("导出 CSV 模板", GUILayout.Height(35)))
                {
                    ExportCsvTemplate();
                }
                
                if (GUILayout.Button("导出 TSV 模板\n(推荐)", GUILayout.Height(35)))
                {
                    ExportTsvTemplate();
                }
                
                EditorGUILayout.EndHorizontal();
            }

            EditorGUILayout.EndHorizontal();
        }

        /// <summary>
        /// 绘制字段行
        /// </summary>
        private void DrawFieldRow(int index)
        {
            var field = _fields[index];

            EditorGUILayout.BeginHorizontal();

            // 普通表通过主键列构建 ConfigBase 索引，general 表不显示该控制项。
            if (!IsGeneralConfigKind())
            {
                field.IsPrimaryKey = EditorGUILayout.Toggle(field.IsPrimaryKey, GUILayout.Width(PrimaryKeyColumnWidth));
            }

            // 字段名
            field.FieldName = EditorGUILayout.TextField(field.FieldName, GUILayout.Width(FieldNameColumnWidth));

            // 类型输入框（支持自定义类型）
            EditorGUILayout.BeginVertical(GUILayout.Width(TypeColumnWidth));
            field.TypeName = EditorGUILayout.TextField(field.TypeName);
            
            // 常用类型快速选择按钮
            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Button("int", GUILayout.Width(35), GUILayout.Height(18)))
                field.TypeName = "int";
            if (GUILayout.Button("long", GUILayout.Width(35), GUILayout.Height(18)))
                field.TypeName = "long";
            if (GUILayout.Button("bool", GUILayout.Width(35), GUILayout.Height(18)))
	            field.TypeName = "bool";
			if (GUILayout.Button("string", GUILayout.Width(40), GUILayout.Height(18)))
                field.TypeName = "string";
            if (GUILayout.Button("[]", GUILayout.Width(25), GUILayout.Height(18)))
            {
                // 添加数组后缀
                if (!field.TypeName.EndsWith("[]"))
                    field.TypeName += "[]";
            }
            EditorGUILayout.EndHorizontal();
            EditorGUILayout.EndVertical();

            // 注释
            field.Comment = EditorGUILayout.TextField(field.Comment, GUILayout.Width(CommentColumnWidth));

            // 删除按钮
            if (GUILayout.Button("删除", GUILayout.Width(40)))
            {
                _fields.RemoveAt(index);
            }

            // 上移下移按钮
            if (GUILayout.Button("↑", GUILayout.Width(25)) && index > 0)
            {
                var temp = _fields[index];
                _fields[index] = _fields[index - 1];
                _fields[index - 1] = temp;
            }

            if (GUILayout.Button("↓", GUILayout.Width(25)) && index < _fields.Count - 1)
            {
                var temp = _fields[index];
                _fields[index] = _fields[index + 1];
                _fields[index + 1] = temp;
            }

            EditorGUILayout.EndHorizontal();
            EditorGUILayout.Space(2);
        }

        /// <summary>
        /// 加载现有表结构
        /// </summary>
        private void LoadExistingTable()
        {
            if (string.IsNullOrEmpty(_excelPath) || !File.Exists(_excelPath))
            {
                EditorUtility.DisplayDialog("错误", "请先选择有效的 Excel 文件", "确定");
                return;
            }

            try
            {
                Debug.Log($"[ConfigTableEditorWindow] 开始读取 Excel: {_excelPath}");
                
                var reader = new ExcelReader();
                var sheets = reader.ReadExcel(_excelPath);

                if (sheets == null)
                {
                    EditorUtility.DisplayDialog("错误", 
                        "读取 Excel 失败，返回 null。\n\n" +
                        "可能的原因：\n" +
                        "1. Excel 文件正在被其他程序打开（请关闭 Excel）\n" +
                        "2. 文件格式不正确\n" +
                        "3. 文件损坏\n\n" +
                        "请检查 Unity Console 查看详细错误信息。", 
                        "确定");
                    return;
                }

                if (sheets.Count == 0)
                {
                    EditorUtility.DisplayDialog("错误", 
                        "Excel 文件中没有有效的工作表。\n\n" +
                        "可能的原因：\n" +
                        "1. 所有工作表都是空的\n" +
                        "2. 工作表行数少于4行（需要：注释行、字段名行、类型行、数据行）\n" +
                        "3. 字段名行为空\n\n" +
                        "请检查 Unity Console 查看详细信息。", 
                        "确定");
                    return;
                }

                Debug.Log($"[ConfigTableEditorWindow] 成功读取 {sheets.Count} 个工作表");

                ExcelReader.ExcelSheetData targetSheet = null;

                if (string.IsNullOrEmpty(_tableName))
                {
                    // 如果没有指定表名，使用第一个表
                    targetSheet = sheets.FirstOrDefault();
                    Debug.Log($"[ConfigTableEditorWindow] 未指定表名，使用第一个工作表: {targetSheet?.SheetName}");
                }
                else
                {
                    // 查找指定的表
                    targetSheet = sheets.FirstOrDefault(s => s.SheetName == _tableName);
                    Debug.Log($"[ConfigTableEditorWindow] 查找指定工作表: {_tableName}, 结果: {(targetSheet != null ? "找到" : "未找到")}");
                }

                if (targetSheet == null)
                {
                    var availableSheets = string.Join(", ", sheets.Select(s => s.SheetName));
                    EditorUtility.DisplayDialog("错误", 
                        $"未找到指定的工作表: {_tableName}\n\n" +
                        $"可用的工作表：{availableSheets}", 
                        "确定");
                    return;
                }

                Debug.Log($"[ConfigTableEditorWindow] 工作表信息 - 名称: {targetSheet.SheetName}, 字段数: {targetSheet.FieldNames.Count}, 数据行数: {targetSheet.DataRows.Count}");
                _tableKind = targetSheet.SheetKind == ExcelReader.ExcelSheetKind.General
                    ? ConfigTableKind.General
                    : ConfigTableKind.Table;

                // 加载字段定义
                _fields.Clear();
                for (int i = 0; i < targetSheet.FieldNames.Count; i++)
                {
                    var field = new FieldDefinition
                    {
                        FieldName = targetSheet.FieldNames[i],
                        TypeName = i < targetSheet.TypeDefinitions.Count ? targetSheet.TypeDefinitions[i] : "string",
                        Comment = i < targetSheet.Comments.Count ? targetSheet.Comments[i] : "",
                        IsPrimaryKey = i == 0  // 默认第一个字段为主键
                    };
                    _fields.Add(field);
                    Debug.Log($"[ConfigTableEditorWindow] 字段 {i}: {field.FieldName} ({field.TypeName}) - {field.Comment}");
                }

                _tableName = targetSheet.SheetName;
                
                // 如果类名为空，使用表名作为类名
                if (string.IsNullOrEmpty(_className))
                {
                    _className = _tableName;
                }

                EditorUtility.DisplayDialog("成功", 
                    $"已加载表结构: {_tableName}\n" +
                    $"字段数: {_fields.Count}\n" +
                    $"数据行数: {targetSheet.DataRows.Count}", 
                    "确定");
            }
            catch (Exception ex)
            {
                Debug.LogError($"[ConfigTableEditorWindow] 加载表结构失败: {ex.Message}\n{ex.StackTrace}");
                EditorUtility.DisplayDialog("错误", 
                    $"加载表结构失败:\n\n{ex.Message}\n\n" +
                    "详细错误信息请查看 Unity Console。\n\n" +
                    "常见问题：\n" +
                    "1. Excel 文件正在被打开，请关闭后重试\n" +
                    "2. 文件格式不正确（需要 .xlsx 格式）\n" +
                    "3. ExcelDataReader 库未正确安装", 
                    "确定");
            }
        }

        /// <summary>
        /// 导出模板说明
        /// </summary>
        private void ExportTemplateGuide()
        {
            if (!ValidateInput())
            {
                return;
            }

            try
            {
                var guide = GenerateTemplateGuide();
                var guidePath = Path.Combine(Path.GetDirectoryName(_excelPath), $"{_tableName}_模板说明.txt");

                File.WriteAllText(guidePath, guide, System.Text.Encoding.UTF8);

                EditorUtility.DisplayDialog("成功", 
                    $"模板说明已导出: {guidePath}\n\n" +
                    "请按照说明在 Excel 中创建配置表。", 
                    "确定");

                // 打开文件所在目录
                EditorUtility.RevealInFinder(guidePath);
            }
            catch (Exception ex)
            {
                EditorUtility.DisplayDialog("错误", $"导出模板说明失败: {ex.Message}", "确定");
            }
        }

        /// <summary>
        /// 导出 CSV 模板
        /// </summary>
        private void ExportCsvTemplate()
        {
            if (!ValidateInput())
            {
                return;
            }

            try
            {
                var csv = GenerateCsvTemplate();
                var csvPath = Path.Combine(Path.GetDirectoryName(_excelPath), $"{_tableName}_模板.csv");

                File.WriteAllText(csvPath, csv, System.Text.Encoding.UTF8);

                EditorUtility.DisplayDialog("成功", 
                    $"CSV 模板已导出: {csvPath}\n\n" +
                    "使用方法：\n" +
                    "1. 用 Excel 打开此 CSV 文件\n" +
                    "2. 或复制文件内容粘贴到 Excel\n" +
                    "3. 填写数据后另存为 .xlsx 格式", 
                    "确定");

                // 打开文件所在目录
                EditorUtility.RevealInFinder(csvPath);
            }
            catch (Exception ex)
            {
                EditorUtility.DisplayDialog("错误", $"导出 CSV 模板失败: {ex.Message}", "确定");
            }
        }

        /// <summary>
        /// 导出 TSV 模板（推荐）
        /// </summary>
        private void ExportTsvTemplate()
        {
            if (!ValidateInput())
            {
                return;
            }

            try
            {
                var tsv = GenerateTsvTemplate();
                var tsvPath = Path.Combine(Path.GetDirectoryName(_excelPath), $"{_tableName}_模板.txt");

                File.WriteAllText(tsvPath, tsv, System.Text.Encoding.UTF8);

                EditorUtility.DisplayDialog("成功", 
                    $"TSV 模板已导出: {tsvPath}\n\n" +
                    "使用方法（推荐）：\n" +
                    "1. 用记事本打开此文件\n" +
                    "2. 全选并复制（Ctrl+A, Ctrl+C）\n" +
                    "3. 在 Excel 中粘贴（Ctrl+V）\n" +
                    "4. Excel 会自动识别制表符并分列\n" +
                    "5. 填写数据后另存为 .xlsx 格式", 
                    "确定");

                // 打开文件所在目录
                EditorUtility.RevealInFinder(tsvPath);
            }
            catch (Exception ex)
            {
                EditorUtility.DisplayDialog("错误", $"导出 TSV 模板失败: {ex.Message}", "确定");
            }
        }

        /// <summary>
        /// 生成 CSV 模板
        /// </summary>
        private string GenerateCsvTemplate()
        {
            if (IsGeneralConfigKind())
            {
                return GenerateGeneralCsvTemplate();
            }

            var csv = new System.Text.StringBuilder();

            // 行0: 注释行
            var comments = new List<string>();
            foreach (var field in _fields)
            {
                comments.Add(EscapeCsvValue(field.Comment));
            }
            csv.AppendLine(string.Join(",", comments));

            // 行1: 字段名行
            var fieldNames = new List<string>();
            foreach (var field in _fields)
            {
                fieldNames.Add(EscapeCsvValue(field.FieldName));
            }
            csv.AppendLine(string.Join(",", fieldNames));

            // 行2: 类型定义行
            var typeNames = new List<string>();
            foreach (var field in _fields)
            {
                typeNames.Add(EscapeCsvValue(field.TypeName));
            }
            csv.AppendLine(string.Join(",", typeNames));

            // 行3: 示例数据行
            var examples = new List<string>();
            foreach (var field in _fields)
            {
                var example = GetExampleValue(field.TypeName);
                examples.Add(EscapeCsvValue(example));
            }
            csv.AppendLine(string.Join(",", examples));

            return csv.ToString();
        }

        /// <summary>
        /// 生成 TSV 模板（制表符分隔，更适合复制粘贴）
        /// </summary>
        private string GenerateTsvTemplate()
        {
            if (IsGeneralConfigKind())
            {
                return GenerateGeneralTsvTemplate();
            }

            var tsv = new System.Text.StringBuilder();

            // 行0: 注释行
            var comments = new List<string>();
            foreach (var field in _fields)
            {
                comments.Add(field.Comment ?? "");
            }
            tsv.AppendLine(string.Join("\t", comments));

            // 行1: 字段名行
            var fieldNames = new List<string>();
            foreach (var field in _fields)
            {
                fieldNames.Add(field.FieldName ?? "");
            }
            tsv.AppendLine(string.Join("\t", fieldNames));

            // 行2: 类型定义行
            var typeNames = new List<string>();
            foreach (var field in _fields)
            {
                typeNames.Add(field.TypeName ?? "string");
            }
            tsv.AppendLine(string.Join("\t", typeNames));

            // 行3: 示例数据行
            var examples = new List<string>();
            foreach (var field in _fields)
            {
                var example = GetExampleValue(field.TypeName);
                examples.Add(example);
            }
            tsv.AppendLine(string.Join("\t", examples));

            return tsv.ToString();
        }

        /// <summary>
        /// 生成 general 配置使用的 CSV 纵向键值模板。
        /// </summary>
        private string GenerateGeneralCsvTemplate()
        {
            var csv = new System.Text.StringBuilder();
            csv.AppendLine("字段键,值类型,配置值,注释");
            csv.AppendLine("Key,ValueType,Value,Comment");
            csv.AppendLine("string,string,string,string");

            foreach (var field in _fields)
            {
                csv.AppendLine(string.Join(",",
                    EscapeCsvValue(field.FieldName),
                    EscapeCsvValue(field.TypeName),
                    EscapeCsvValue(GetExampleValue(field.TypeName)),
                    EscapeCsvValue(field.Comment)));
            }

            return csv.ToString();
        }

        /// <summary>
        /// 生成 general 配置使用的 TSV 纵向键值模板。
        /// </summary>
        private string GenerateGeneralTsvTemplate()
        {
            var tsv = new System.Text.StringBuilder();
            tsv.AppendLine("字段键\t值类型\t配置值\t注释");
            tsv.AppendLine("Key\tValueType\tValue\tComment");
            tsv.AppendLine("string\tstring\tstring\tstring");

            foreach (var field in _fields)
            {
                tsv.AppendLine(string.Join("\t",
                    field.FieldName ?? "",
                    field.TypeName ?? "string",
                    GetExampleValue(field.TypeName),
                    field.Comment ?? ""));
            }

            return tsv.ToString();
        }

        /// <summary>
        /// 转义 CSV 值
        /// </summary>
        private string EscapeCsvValue(string value)
        {
            if (string.IsNullOrEmpty(value))
            {
                return "";
            }

            // 如果包含逗号、引号或换行符，需要用引号包裹
            if (value.Contains(",") || value.Contains("\"") || value.Contains("\n") || value.Contains("\r"))
            {
                // 引号需要转义为两个引号
                value = value.Replace("\"", "\"\"");
                return $"\"{value}\"";
            }

            return value;
        }

        /// <summary>
        /// 生成模板说明
        /// </summary>
        private string GenerateTemplateGuide()
        {
            if (IsGeneralConfigKind())
            {
                return GenerateGeneralTemplateGuide();
            }

            var guide = new System.Text.StringBuilder();

            guide.AppendLine($"========================================");
            guide.AppendLine($"配置表模板说明: {_tableName}");
            guide.AppendLine($"========================================");
            guide.AppendLine();
            guide.AppendLine($"Excel 文件路径: {_excelPath}");
            guide.AppendLine($"工作表名称: {_tableName}");
            guide.AppendLine();
            guide.AppendLine("========================================");
            guide.AppendLine("Excel 格式规范");
            guide.AppendLine("========================================");
            guide.AppendLine();
            guide.AppendLine("行0: 注释行（字段说明）");
            guide.AppendLine("行1: 字段名行");
            guide.AppendLine("行2: 类型定义行");
            guide.AppendLine("行3+: 数据行");
            guide.AppendLine();
            guide.AppendLine("========================================");
            guide.AppendLine("字段定义");
            guide.AppendLine("========================================");
            guide.AppendLine();

            // 表头
            guide.AppendLine("注释行（第1行）:");
            guide.Append("| ");
            foreach (var field in _fields)
            {
                guide.Append($"{field.Comment,-15} | ");
            }
            guide.AppendLine();

            guide.AppendLine();
            guide.AppendLine("字段名行（第2行）:");
            guide.Append("| ");
            foreach (var field in _fields)
            {
                guide.Append($"{field.FieldName,-15} | ");
            }
            guide.AppendLine();

            guide.AppendLine();
            guide.AppendLine("类型定义行（第3行）:");
            guide.Append("| ");
            foreach (var field in _fields)
            {
                guide.Append($"{field.TypeName,-15} | ");
            }
            guide.AppendLine();

            guide.AppendLine();
            guide.AppendLine("数据示例行（第4行）:");
            guide.Append("| ");
            foreach (var field in _fields)
            {
                var example = GetExampleValue(field.TypeName);
                guide.Append($"{example,-15} | ");
            }
            guide.AppendLine();

            guide.AppendLine();
            guide.AppendLine("========================================");
            guide.AppendLine("字段详细说明");
            guide.AppendLine("========================================");
            guide.AppendLine();

            for (int i = 0; i < _fields.Count; i++)
            {
                var field = _fields[i];
                guide.AppendLine($"{i + 1}. {field.FieldName}");
                guide.AppendLine($"   - 类型: {field.TypeName}");
                guide.AppendLine($"   - 注释: {field.Comment}");
                guide.AppendLine($"   - 主键: {(field.IsPrimaryKey ? "是" : "否")}");
                guide.AppendLine();
            }

            guide.AppendLine("========================================");
            guide.AppendLine("创建步骤");
            guide.AppendLine("========================================");
            guide.AppendLine();
            guide.AppendLine("1. 使用 Microsoft Excel 或 WPS 打开/创建 Excel 文件");
            guide.AppendLine($"2. 创建名为 '{_tableName}' 的工作表");
            guide.AppendLine("3. 按照上面的格式填写前3行（注释、字段名、类型）");
            guide.AppendLine("4. 从第4行开始填写数据");
            guide.AppendLine("5. 保存文件");
            guide.AppendLine("6. 返回 Unity，使用配置表编辑器加载并生成代码");
            guide.AppendLine();

            return guide.ToString();
        }

        /// <summary>
        /// 生成 general 配置表的模板说明。
        /// </summary>
        private string GenerateGeneralTemplateGuide()
        {
            var guide = new System.Text.StringBuilder();

            guide.AppendLine("========================================");
            guide.AppendLine($"General 配置表模板说明: {_tableName}");
            guide.AppendLine("========================================");
            guide.AppendLine();
            guide.AppendLine($"Excel 文件路径: {_excelPath}");
            guide.AppendLine($"工作表名称: {_tableName}");
            guide.AppendLine("工作表名称必须以 _general 结尾。");
            guide.AppendLine();
            guide.AppendLine("========================================");
            guide.AppendLine("Excel 格式规范");
            guide.AppendLine("========================================");
            guide.AppendLine();
            guide.AppendLine("第1行: Key / ValueType / Value / Comment 四列的说明");
            guide.AppendLine("第2行: 固定填写 Key / ValueType / Value / Comment");
            guide.AppendLine("第3行: 固定填写 string / string / string / string");
            guide.AppendLine("第4行起: 每一行是一项单例配置字段");
            guide.AppendLine();
            guide.AppendLine("========================================");
            guide.AppendLine("字段定义");
            guide.AppendLine("========================================");
            guide.AppendLine();

            for (int i = 0; i < _fields.Count; i++)
            {
                var field = _fields[i];
                guide.AppendLine($"{i + 1}. {field.FieldName}");
                guide.AppendLine($"   - ValueType: {field.TypeName}");
                guide.AppendLine($"   - Value 示例: {GetExampleValue(field.TypeName)}");
                guide.AppendLine($"   - Comment: {field.Comment}");
                guide.AppendLine();
            }

            guide.AppendLine("生成代码时这些 Key 会变成 C# 属性，导出数据库时会折叠成一行强类型数据。");
            guide.AppendLine();

            return guide.ToString();
        }

        /// <summary>
        /// 获取示例值
        /// </summary>
        private string GetExampleValue(string typeName)
        {
            switch (typeName)
            {
                case "int":
                case "long":
                    return "1001";
                case "float":
                case "double":
                    return "1.5";
                case "bool":
                    return "true";
                case "string":
                    return "示例文本";
                case "int[]":
                    return "1,2,3";
                case "float[]":
                    return "1.0,2.0";
                case "string[]":
                    return "a,b,c";
                case "Vector2":
                    return "100,200";
                case "Vector3":
                    return "100,200,300";
                case "Color":
                    return "#FF0000";
                case "ItemReward":
                    return "1001:10";
                case "RewardList":
                    return "1001:10;1002:5";
                case "IntRange":
                    return "100-200";
                default:
                    return "";
            }
        }

        /// <summary>
        /// 生成代码
        /// </summary>
        private void GenerateCode()
        {
            if (!ValidateInput())
            {
                return;
            }

            try
            {
                // 创建 ExcelSheetData
                var sheetData = new ExcelReader.ExcelSheetData
                {
                    SheetName = _tableName,
                    SheetKind = IsGeneralConfigKind()
                        ? ExcelReader.ExcelSheetKind.General
                        : ExcelReader.ExcelSheetKind.Table
                };

                foreach (var field in _fields)
                {
                    sheetData.FieldNames.Add(field.FieldName);
                    sheetData.TypeDefinitions.Add(field.TypeName);
                    sheetData.Comments.Add(field.Comment);
                }

                // 使用类名（如果为空则使用表名）
                var className = string.IsNullOrEmpty(_className) ? _tableName : _className;

                // 按目标创建生成配置，避免启动关键表误生成到 HotUpdate
                var generatorConfig = CreateGeneratorConfigByTarget();
                var generator = new CodeGenerator(generatorConfig);
                var result = generator.GenerateConfigClass(sheetData, className);

                // 保存数据类文件
                var dataDirectory = Path.GetDirectoryName(result.DataClassPath);
                if (!Directory.Exists(dataDirectory))
                {
                    Directory.CreateDirectory(dataDirectory);
                }
                File.WriteAllText(result.DataClassPath, result.DataClassCode, System.Text.Encoding.UTF8);

                // general 表通过 GetConfig<T>() 直接读取数据类，不生成也不保存 Table 类文件。
                if (!string.IsNullOrEmpty(result.TableClassPath) && !string.IsNullOrEmpty(result.TableClassCode))
                {
                    var tableDirectory = Path.GetDirectoryName(result.TableClassPath);
                    if (!Directory.Exists(tableDirectory))
                    {
                        Directory.CreateDirectory(tableDirectory);
                    }

                    File.WriteAllText(result.TableClassPath, result.TableClassCode, System.Text.Encoding.UTF8);
                }

                //EditorUtility.DisplayDialog("成功", 
                //    $"代码已生成:\n" +
                //    $"数据类: {result.DataClassPath}\n" +
                //    $"Table类: {result.TableClassPath}", 
                //    "确定");
                AssetDatabase.Refresh();
            }
            catch (Exception ex)
            {
                EditorUtility.DisplayDialog("错误", $"生成代码失败: {ex.Message}", "确定");
            }
        }

        /// <summary>
        /// 根据 Framework 启动配置或 HotUpdate 业务配置创建代码生成设置。
        /// </summary>
        private CodeGenerator.GeneratorConfig CreateGeneratorConfigByTarget()
        {
            if (_codeTarget == CodeAssemblyTarget.FrameworkBootstrap)
            {
                // 框架启动表（language / loading_tips）生成到包内 Bootstrap 目录。
                // 注意：仅嵌入式包（Packages/ 下可编辑）时允许再生成；业务项目以只读方式
                // 引用本包（git URL / tarball）时，Bootstrap 表结构不可改，业务表一律走下方分支生成到 Assets。
                return new CodeGenerator.GeneratorConfig
                {
                    Namespace = "Framework",
                    DataOutputPath = "Packages/com.frameworkbase.core/ConfigData/Bootstrap/Data",
                    TableOutputPath = "Packages/com.frameworkbase.core/ConfigData/Bootstrap/Table",
                    GenerateComments = true,
                    UseSQLiteAttributes = true,
                    GenerateSerializable = true
                };
            }

            return new CodeGenerator.GeneratorConfig
            {
                Namespace = "HotUpdate.Config",
                DataOutputPath = "Assets/Scripts/HotUpdate/ConfigData/Data",
                TableOutputPath = "Assets/Scripts/HotUpdate/ConfigData/Table",
                GenerateComments = true,
                UseSQLiteAttributes = true,
                GenerateSerializable = true
            };
        }

        /// <summary>
        /// 从 Unity EditorPrefs 读取持久化的编辑器选项。
        /// </summary>
        private void LoadPrefs()
        {
            _mode = (EditorMode)EditorPrefs.GetInt(ModePrefsKey, (int)EditorMode.LoadAndGenerate);
            _excelPath = EditorPrefs.GetString(ExcelPathPrefsKey, _excelPath);
            _tableName = EditorPrefs.GetString(TableNamePrefsKey, _tableName);
            _className = EditorPrefs.GetString(ClassNamePrefsKey, _className);
            _tableKind = (ConfigTableKind)EditorPrefs.GetInt(TableKindPrefsKey, (int)_tableKind);
            _codeTarget = (CodeAssemblyTarget)EditorPrefs.GetInt(
                CodeTargetPrefsKey,
                (int)CodeAssemblyTarget.FrameworkBootstrap);
        }

        /// <summary>
        /// 将当前编辑器选项保存到 Unity EditorPrefs。
        /// </summary>
        private void SavePrefs()
        {
            EditorPrefs.SetInt(ModePrefsKey, (int)_mode);
            EditorPrefs.SetString(ExcelPathPrefsKey, _excelPath ?? string.Empty);
            EditorPrefs.SetString(TableNamePrefsKey, _tableName ?? string.Empty);
            EditorPrefs.SetString(ClassNamePrefsKey, _className ?? string.Empty);
            EditorPrefs.SetInt(TableKindPrefsKey, (int)_tableKind);
            EditorPrefs.SetInt(CodeTargetPrefsKey, (int)_codeTarget);
        }

        /// <summary>
        /// 验证输入；general 表是单例键值配置，不强制要求主键字段。
        /// </summary>
        private bool ValidateInput()
        {
            if (string.IsNullOrEmpty(_excelPath))
            {
                EditorUtility.DisplayDialog("错误", "请输入 Excel 路径", "确定");
                return false;
            }

            if (string.IsNullOrEmpty(_tableName))
            {
                EditorUtility.DisplayDialog("错误", "请输入表名", "确定");
                return false;
            }

            if (_fields.Count == 0)
            {
                EditorUtility.DisplayDialog("错误", "请至少添加一个字段", "确定");
                return false;
            }

            // 检查是否有主键
            // general 表会被折叠成单行单例配置，运行时不依赖 ConfigBase 的主键索引。
            if (IsGeneralConfigKind() && !ExcelReader.IsGeneralSheet(_tableName))
            {
                EditorUtility.DisplayDialog("错误", "General 表的工作表名必须以 _general 结尾", "确定");
                return false;
            }

            if (!IsGeneralConfigKind() && !_fields.Any(f => f.IsPrimaryKey))
            {
                EditorUtility.DisplayDialog("错误", "请至少指定一个主键字段", "确定");
                return false;
            }

            return true;
        }

        /// <summary>
        /// 判断当前编辑目标是否按 general 单例配置处理；表名只用于自动推荐，用户手动选择优先。
        /// </summary>
        private bool IsGeneralConfigKind()
        {
            return _tableKind == ConfigTableKind.General;
        }
    }
}
