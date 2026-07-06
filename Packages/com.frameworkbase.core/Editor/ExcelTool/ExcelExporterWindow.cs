using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace Editor.ExcelTool
{
    /// <summary>
    /// Excel 导出器窗口
    /// 用于将 Excel 数据导出到 SQLite 数据库
    /// </summary>
    public class ExcelExporterWindow : EditorWindow
    {
        private const string DefaultExcelFolder = "Assets/RefData_Excel";
        private const string ExcelPathPrefsKey = "ClientBase.ExcelExporterWindow.ExcelPath";
        /// <summary>
        /// 单文件导出时记忆工作表名的 EditorPrefs 键。
        /// </summary>
        private const string SheetNamePrefsKey = "ClientBase.ExcelExporterWindow.SheetName";
        private const string OutputTargetPrefsKey = "ClientBase.ExcelExporterWindow.OutputTarget";
        private const string FileScopePrefsKey = "ClientBase.ExcelExporterWindow.FileScope";
        private const string PruneMissingTablesPrefsKey = "ClientBase.ExcelExporterWindow.PruneMissingTables";
        private const string ServerExportPrefsKey = "ClientBase.ExcelExporterWindow.ServerExport";
        private const string ServerOutputDirPrefsKey = "ClientBase.ExcelExporterWindow.ServerOutputDir";

        private enum FileScopeMode
        {
            Single,
            Batch
        }

        private FileScopeMode _fileScope = FileScopeMode.Single;
        private ExcelExporter.DatabaseOutputTarget _outputTarget = ExcelExporter.DatabaseOutputTarget.HotUpdateOnly;
        private string _excelPath = "";
        /// <summary>
        /// 单文件导出时指定的工作表名；为空时保持兼容，导出首个有效工作表。
        /// </summary>
        private string _sheetName = "";
        /// <summary>
        /// 当前 Excel 文件中已读取到的工作表名缓存，用于快速选择 general 工作表。
        /// </summary>
        private List<string> _sheetNames = new List<string>();
        private string _excelFolder = DefaultExcelFolder;
        private string _outputDbPath = "Assets/StreamingAssets/RefData/config.db";
        private string _addressableBytesOutputPath = "Assets/ResourcesOut/RefData/config.db.bytes";
        private bool _overwriteExistingTables = true;
        /// <summary>
        /// 批量导出时是否清理已经从 Excel 目录移除的旧表。
        /// </summary>
        private bool _pruneMissingTables = true;
        private bool _enableValidation = true;
        private bool _verboseLogging = false;

        /// <summary>
        /// 是否同时导出服务端 TSV 配表文件。
        /// </summary>
        private bool _serverExport = false;
        /// <summary>
        /// 服务端配表输出目录。
        /// </summary>
        private string _serverOutputDir = "Server/ConfigData";
        private Vector2 _scrollPosition;
        private List<ExcelExporter.ExportResult> _lastResults = new List<ExcelExporter.ExportResult>();

        [MenuItem("Tools/Excel/Excel 导出器")]
        public static void ShowWindow()
        {
            var window = GetWindow<ExcelExporterWindow>("Excel 导出器");
            window.minSize = new Vector2(600, 400);
            window.Show();
        }

        private void OnEnable()
        {
            _excelPath = EditorPrefs.GetString(ExcelPathPrefsKey, _excelPath);
            _sheetName = EditorPrefs.GetString(SheetNamePrefsKey, _sheetName);
            _fileScope = (FileScopeMode)EditorPrefs.GetInt(FileScopePrefsKey, (int)FileScopeMode.Single);
            _outputTarget = (ExcelExporter.DatabaseOutputTarget)EditorPrefs.GetInt(
                OutputTargetPrefsKey,
                (int)ExcelExporter.DatabaseOutputTarget.HotUpdateOnly);
            _pruneMissingTables = EditorPrefs.GetBool(PruneMissingTablesPrefsKey, _pruneMissingTables);
            _serverExport = EditorPrefs.GetBool(ServerExportPrefsKey, _serverExport);
            _serverOutputDir = EditorPrefs.GetString(ServerOutputDirPrefsKey, _serverOutputDir);
        }

        private void OnDisable()
        {
            SaveExcelPath();
            SaveSheetName();
            EditorPrefs.SetInt(FileScopePrefsKey, (int)_fileScope);
            EditorPrefs.SetInt(OutputTargetPrefsKey, (int)_outputTarget);
            EditorPrefs.SetBool(PruneMissingTablesPrefsKey, _pruneMissingTables);
            EditorPrefs.SetBool(ServerExportPrefsKey, _serverExport);
            EditorPrefs.SetString(ServerOutputDirPrefsKey, _serverOutputDir);
        }

        private void OnGUI()
        {
            EditorGUILayout.Space(10);

            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField("文件范围:", GUILayout.Width(80));
            EditorGUI.BeginChangeCheck();
            _fileScope = (FileScopeMode)EditorGUILayout.EnumPopup(_fileScope, GUILayout.Width(150));
            if (EditorGUI.EndChangeCheck())
                ApplyRecommendedOutputTarget();

            EditorGUILayout.EndHorizontal();

            EditorGUILayout.Space(5);

            if (_fileScope == FileScopeMode.Single)
                DrawSingleModeUI();
            else
                DrawBatchModeUI();

            EditorGUILayout.Space(10);

            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField("输出目标:", GUILayout.Width(80));
            EditorGUI.BeginChangeCheck();
            _outputTarget = (ExcelExporter.DatabaseOutputTarget)EditorGUILayout.EnumPopup(_outputTarget);
            if (EditorGUI.EndChangeCheck())
                EditorPrefs.SetInt(OutputTargetPrefsKey, (int)_outputTarget);
            EditorGUILayout.EndHorizontal();

            DrawOutputTargetHelpBox();
            DrawOutputPathFields();

            EditorGUILayout.Space(10);

            EditorGUILayout.LabelField("导出选项:", EditorStyles.boldLabel);
            _overwriteExistingTables = EditorGUILayout.Toggle("覆盖已存在的表", _overwriteExistingTables);
            if (_fileScope == FileScopeMode.Batch)
            {
                _pruneMissingTables = EditorGUILayout.Toggle("清理已删除的旧表", _pruneMissingTables);
            }

            _enableValidation = EditorGUILayout.Toggle("启用数据校验", _enableValidation);
            _verboseLogging = EditorGUILayout.Toggle("显示详细日志", _verboseLogging);

            EditorGUILayout.Space(10);

            // 服务端导出选项
            EditorGUILayout.LabelField("服务端导出:", EditorStyles.boldLabel);
            _serverExport = EditorGUILayout.Toggle("同时导出服务端 TSV", _serverExport);

            if (_serverExport)
            {
                EditorGUILayout.BeginHorizontal();
                EditorGUILayout.LabelField("输出目录:", GUILayout.Width(80));
                _serverOutputDir = EditorGUILayout.TextField(_serverOutputDir);
                if (GUILayout.Button("浏览", GUILayout.Width(60)))
                {
                    var path = EditorUtility.OpenFolderPanel("选择服务端配表目录", GetPanelDirectory(_serverOutputDir), "");
                    if (!string.IsNullOrEmpty(path))
                        _serverOutputDir = ToProjectRelativePath(path);
                }
                EditorGUILayout.EndHorizontal();
                EditorGUILayout.HelpBox("将配表以 TSV（Tab 分隔纯文本）格式导出到服务端目录，每张表一个 .txt 文件。", MessageType.Info);
            }

            EditorGUILayout.Space(10);

            if (GUILayout.Button("开始导出", GUILayout.Height(35)))
                Export();

            EditorGUILayout.Space(10);

            if (_lastResults.Count > 0)
                DrawResults();
        }

        private void ApplyRecommendedOutputTarget()
        {
            _outputTarget = _fileScope == FileScopeMode.Batch
                ? ExcelExporter.DatabaseOutputTarget.StreamingAssetsOnly
                : ExcelExporter.DatabaseOutputTarget.HotUpdateOnly;
            EditorPrefs.SetInt(OutputTargetPrefsKey, (int)_outputTarget);
        }

        private void DrawOutputTargetHelpBox()
        {
            string message = _outputTarget switch
            {
                ExcelExporter.DatabaseOutputTarget.StreamingAssetsOnly =>
                    "仅更新首包 StreamingAssets/config.db，适合发版前批量定基线，不会改动热更资源。",
                ExcelExporter.DatabaseOutputTarget.HotUpdateOnly =>
                    "仅更新 ResourcesOut/config.db.bytes，适合日常改单表；首包保持不变。若热更库不存在会先拷贝首包作为基线。",
                _ =>
                    "先写入首包，再将整库同步到热更 .bytes；适合需要首包与热更同时更新的场景。"
            };
            EditorGUILayout.HelpBox(message, MessageType.Info);
        }

        private void DrawOutputPathFields()
        {
            bool showStreaming = _outputTarget != ExcelExporter.DatabaseOutputTarget.HotUpdateOnly;
            bool showHotUpdate = _outputTarget != ExcelExporter.DatabaseOutputTarget.StreamingAssetsOnly;

            if (showStreaming)
            {
                EditorGUILayout.BeginHorizontal();
                EditorGUILayout.LabelField("首包数据库:", GUILayout.Width(80));
                _outputDbPath = EditorGUILayout.TextField(_outputDbPath);
                if (GUILayout.Button("浏览", GUILayout.Width(60)))
                {
                    var path = EditorUtility.SaveFilePanel("选择首包数据库", GetPanelDirectory(_outputDbPath), "config", "db");
                    if (!string.IsNullOrEmpty(path))
                        _outputDbPath = ToProjectRelativePath(path);
                }
                EditorGUILayout.EndHorizontal();
            }

            if (showHotUpdate)
            {
                EditorGUILayout.BeginHorizontal();
                EditorGUILayout.LabelField("热更数据库:", GUILayout.Width(80));
                _addressableBytesOutputPath = EditorGUILayout.TextField(_addressableBytesOutputPath);
                if (GUILayout.Button("浏览", GUILayout.Width(60)))
                {
                    var path = EditorUtility.SaveFilePanel("选择热更数据库", GetPanelDirectory(_addressableBytesOutputPath), "config.db", "bytes");
                    if (!string.IsNullOrEmpty(path))
                        _addressableBytesOutputPath = ToProjectRelativePath(path);
                }
                EditorGUILayout.EndHorizontal();
            }
        }

        private void DrawSingleModeUI()
        {
            EditorGUILayout.HelpBox("导出单个 Excel 工作表。general 表请填写或选择以 _general 结尾的工作表，内容格式为 Key / ValueType / Value / Comment。", MessageType.None);

            EditorGUILayout.Space(5);

            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField("Excel 文件:", GUILayout.Width(80));
            EditorGUI.BeginChangeCheck();
            _excelPath = EditorGUILayout.TextField(_excelPath);
            if (EditorGUI.EndChangeCheck())
                SaveExcelPath();

            if (GUILayout.Button("浏览", GUILayout.Width(60)))
            {
                var path = EditorUtility.OpenFilePanel("选择 Excel 文件", GetPanelDirectory(_excelPath), "xlsx");
                if (!string.IsNullOrEmpty(path))
                {
                    _excelPath = ToProjectRelativePath(path);
                    _sheetNames.Clear();
                    SaveExcelPath();
                }
            }
            EditorGUILayout.EndHorizontal();

            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField("工作表:", GUILayout.Width(80));
            EditorGUI.BeginChangeCheck();
            _sheetName = EditorGUILayout.TextField(_sheetName);
            if (EditorGUI.EndChangeCheck())
                SaveSheetName();

            if (GUILayout.Button("读取", GUILayout.Width(60)))
                LoadSheetNames();
            EditorGUILayout.EndHorizontal();

            if (_sheetNames.Count > 0)
            {
                int selectedIndex = _sheetNames.IndexOf(_sheetName);
                if (selectedIndex < 0)
                {
                    selectedIndex = 0;
                    _sheetName = _sheetNames[selectedIndex];
                    SaveSheetName();
                }

                EditorGUI.BeginChangeCheck();
                selectedIndex = EditorGUILayout.Popup("可用工作表:", selectedIndex, _sheetNames.ToArray());
                if (EditorGUI.EndChangeCheck())
                {
                    _sheetName = _sheetNames[selectedIndex];
                    SaveSheetName();
                }
            }
        }

        private void SaveExcelPath()
        {
            EditorPrefs.SetString(ExcelPathPrefsKey, _excelPath ?? string.Empty);
        }

        /// <summary>
        /// 保存单文件导出指定的工作表名。
        /// </summary>
        private void SaveSheetName()
        {
            EditorPrefs.SetString(SheetNamePrefsKey, _sheetName ?? string.Empty);
        }

        /// <summary>
        /// 读取当前 Excel 文件中的有效工作表名，方便单表导出时选择 general 页。
        /// </summary>
        private void LoadSheetNames()
        {
            _sheetNames.Clear();

            if (string.IsNullOrEmpty(_excelPath) || !File.Exists(_excelPath))
            {
                EditorUtility.DisplayDialog("错误", "请先选择有效的 Excel 文件", "确定");
                return;
            }

            try
            {
                var reader = new ExcelReader();
                var sheets = reader.ReadExcel(_excelPath);
                _sheetNames = sheets.Select(s => s.SheetName).ToList();

                if (_sheetNames.Count == 0)
                {
                    EditorUtility.DisplayDialog("提示", "当前 Excel 文件没有可导出的工作表", "确定");
                    return;
                }

                if (string.IsNullOrEmpty(_sheetName))
                {
                    _sheetName = _sheetNames[0];
                    SaveSheetName();
                }
            }
            catch (Exception ex)
            {
                Debug.LogError($"[ExcelExporterWindow] 读取工作表失败: {ex.Message}\n{ex.StackTrace}");
                EditorUtility.DisplayDialog("错误", $"读取工作表失败:\n{ex.Message}", "确定");
            }
        }

        private void DrawBatchModeUI()
        {
            EditorGUILayout.HelpBox("批量导出 RefData_Excel 下全部表。发版定首包建议输出目标选「仅首包」。", MessageType.None);

            EditorGUILayout.Space(5);

            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField("Excel 文件夹:", GUILayout.Width(80));
            _excelFolder = EditorGUILayout.TextField(_excelFolder);
            if (GUILayout.Button("浏览", GUILayout.Width(60)))
            {
                var path = EditorUtility.OpenFolderPanel("选择 Excel 文件夹", GetPanelDirectory(_excelFolder), "");
                if (!string.IsNullOrEmpty(path))
                    _excelFolder = ToProjectRelativePath(path);
            }
            EditorGUILayout.EndHorizontal();
        }

        private static string ToProjectRelativePath(string path)
        {
            var relativePath = FileUtil.GetProjectRelativePath(path);
            return string.IsNullOrEmpty(relativePath) ? path : relativePath;
        }

        private static string GetPanelDirectory(string path)
        {
            if (string.IsNullOrEmpty(path))
                return Application.dataPath;

            var absolutePath = Path.IsPathRooted(path) ? path : Path.GetFullPath(path);
            if (Directory.Exists(absolutePath))
                return absolutePath;

            var directory = Path.GetDirectoryName(absolutePath);
            return !string.IsNullOrEmpty(directory) && Directory.Exists(directory)
                ? directory
                : Application.dataPath;
        }

        private bool ValidateOutputPaths(out string errorMessage)
        {
            errorMessage = null;

            switch (_outputTarget)
            {
                case ExcelExporter.DatabaseOutputTarget.StreamingAssetsOnly:
                    if (string.IsNullOrEmpty(_outputDbPath))
                        errorMessage = "请设置首包数据库路径";
                    break;

                case ExcelExporter.DatabaseOutputTarget.HotUpdateOnly:
                    if (string.IsNullOrEmpty(_addressableBytesOutputPath))
                        errorMessage = "请设置热更数据库路径";
                    break;

                case ExcelExporter.DatabaseOutputTarget.Both:
                    if (string.IsNullOrEmpty(_outputDbPath) || string.IsNullOrEmpty(_addressableBytesOutputPath))
                        errorMessage = "请设置首包与热更数据库路径";
                    break;
            }

            return string.IsNullOrEmpty(errorMessage);
        }

        private void Export()
        {
            _lastResults.Clear();

            if (!ValidateOutputPaths(out string pathError))
            {
                EditorUtility.DisplayDialog("错误", pathError, "确定");
                return;
            }

            try
            {
                var config = new ExcelExporter.ExportConfig
                {
                    OutputDbPath = _outputDbPath,
                    AddressableBytesOutputPath = _addressableBytesOutputPath,
                    OutputTarget = _outputTarget,
                    OverwriteExistingTables = _overwriteExistingTables,
                    PruneMissingTablesOnBatch = _fileScope == FileScopeMode.Batch && _pruneMissingTables,
                    EnableValidation = _enableValidation,
                    VerboseLogging = _verboseLogging
                };

                var exporter = new ExcelExporter(config);

                if (_fileScope == FileScopeMode.Single)
                {
                    if (string.IsNullOrEmpty(_excelPath) || !File.Exists(_excelPath))
                    {
                        EditorUtility.DisplayDialog("错误", "请选择有效的 Excel 文件", "确定");
                        return;
                    }

                    SaveExcelPath();
                    var sheetName = string.IsNullOrWhiteSpace(_sheetName) ? null : _sheetName;
                    var result = exporter.ExportExcel(_excelPath, sheetName);
                    _lastResults.Add(result);

                    // 服务端 TSV 导出（单表模式）
                    if (_serverExport && result.Success)
                    {
                        ExportServerTsvSingle(_excelPath, sheetName);
                    }

                    if (result.Success)
                    {
                        EditorUtility.DisplayDialog("成功",
                            $"导出成功!\n表名: {result.TableName}\n行数: {result.RowCount}",
                            "确定");
                    }
                    else
                    {
                        EditorUtility.DisplayDialog("失败",
                            $"导出失败:\n{result.ErrorMessage}",
                            "确定");
                    }
                }
                else
                {
                    if (string.IsNullOrEmpty(_excelFolder) || !Directory.Exists(_excelFolder))
                    {
                        EditorUtility.DisplayDialog("错误", "请选择有效的 Excel 文件夹", "确定");
                        return;
                    }

                    var excelFiles = Directory.GetFiles(_excelFolder, "*.xlsx", SearchOption.AllDirectories)
                        .Where(f => !Path.GetFileName(f).StartsWith("~$"))
                        .ToList();

                    if (excelFiles.Count == 0)
                    {
                        EditorUtility.DisplayDialog("错误", "文件夹中没有找到 Excel 文件", "确定");
                        return;
                    }

                    _lastResults = exporter.ExportBatch(excelFiles);

                    // 服务端 TSV 导出（批量模式）
                    if (_serverExport)
                    {
                        ExportServerTsvBatch(excelFiles);
                    }

                    var successCount = _lastResults.Count(r => r.Success);
                    var failCount = _lastResults.Count(r => !r.Success);

                    EditorUtility.DisplayDialog("完成",
                        $"批量导出完成!\n成功: {successCount}\n失败: {failCount}",
                        "确定");
                }

                AssetDatabase.Refresh();
            }
            catch (Exception ex)
            {
                EditorUtility.DisplayDialog("错误", $"导出过程中发生错误:\n{ex.Message}", "确定");
                Debug.LogError($"[ExcelExporterWindow] {ex.Message}\n{ex.StackTrace}");
            }
        }

        /// <summary>
        /// 单表模式下导出服务端 TSV 文件并生成服务端数据类。
        /// </summary>
        private void ExportServerTsvSingle(string excelPath, string sheetName)
        {
            try
            {
                var reader = new ExcelReader();
                var sheets = reader.ReadExcel(excelPath);
                if (sheets == null || sheets.Count == 0) return;

                ExcelReader.ExcelSheetData targetSheet;
                if (string.IsNullOrEmpty(sheetName))
                    targetSheet = sheets.FirstOrDefault();
                else
                    targetSheet = sheets.FirstOrDefault(s => s.SheetName == sheetName);

                if (targetSheet == null) return;

                var exportRules = ConfigExportRuleResolver.LoadDefault();
                if (!exportRules.ShouldExportToServer(targetSheet.SheetName))
                {
                    Debug.Log($"[ExcelExporterWindow] 跳过客户端专用表的服务端导出: {targetSheet.SheetName}");
                    return;
                }

                var tsvConfig = new ServerTsvExporter.TsvExportConfig
                {
                    OutputDirectory = _serverOutputDir,
                    OverwriteExisting = true,
                    VerboseLogging = _verboseLogging
                };
                var tsvExporter = new ServerTsvExporter(tsvConfig);
                var tsvResult = tsvExporter.ExportSheet(targetSheet);

                if (tsvResult.Success)
                    Debug.Log($"[ExcelExporterWindow] 服务端 TSV 导出成功: {tsvResult.OutputPath}");
                else
                    Debug.LogWarning($"[ExcelExporterWindow] 服务端 TSV 导出失败: {tsvResult.ErrorMessage}");

                // 生成服务端数据类
                GenerateServerCode(new List<ExcelReader.ExcelSheetData> { targetSheet });
            }
            catch (Exception ex)
            {
                Debug.LogError($"[ExcelExporterWindow] 服务端 TSV 导出异常: {ex.Message}");
            }
        }

        /// <summary>
        /// 批量模式下导出服务端 TSV 文件并生成服务端数据类。
        /// </summary>
        private void ExportServerTsvBatch(List<string> excelFiles)
        {
            try
            {
                var reader = new ExcelReader();
                var allSheets = new List<ExcelReader.ExcelSheetData>();
                var skippedClientOnlySheets = new List<string>();
                var exportRules = ConfigExportRuleResolver.LoadDefault();

                foreach (var file in excelFiles)
                {
                    var sheets = reader.ReadExcel(file);
                    if (sheets == null)
                        continue;

                    foreach (var sheet in sheets)
                    {
                        if (exportRules.ShouldExportToServer(sheet.SheetName))
                        {
                            allSheets.Add(sheet);
                        }
                        else
                        {
                            skippedClientOnlySheets.Add(sheet.SheetName);
                        }
                    }
                }

                if (skippedClientOnlySheets.Count > 0)
                {
                    Debug.Log(
                        "[ExcelExporterWindow] 已跳过客户端专用表的服务端导出: " +
                        string.Join(", ", skippedClientOnlySheets));
                }

                if (allSheets.Count == 0) return;

                var tsvConfig = new ServerTsvExporter.TsvExportConfig
                {
                    OutputDirectory = _serverOutputDir,
                    OverwriteExisting = true,
                    VerboseLogging = _verboseLogging
                };
                var tsvExporter = new ServerTsvExporter(tsvConfig);
                var results = tsvExporter.ExportBatch(allSheets);

                int success = results.Count(r => r.Success);
                int fail = results.Count(r => !r.Success);
                Debug.Log($"[ExcelExporterWindow] 服务端 TSV 批量导出: 成功 {success}, 失败 {fail}");

                // 生成服务端数据类
                GenerateServerCode(allSheets);
            }
            catch (Exception ex)
            {
                Debug.LogError($"[ExcelExporterWindow] 服务端 TSV 批量导出异常: {ex.Message}");
            }
        }

        /// <summary>
        /// 为工作表列表生成服务端纯 POCO 数据类代码文件（每张表一个 .cs）。
        /// </summary>
        private void GenerateServerCode(List<ExcelReader.ExcelSheetData> sheets)
        {
            try
            {
                var serverConfig = new CodeGenerator.ServerGeneratorConfig
                {
                    Namespace = "GameServer.ConfigData",
                    OutputPath = _serverOutputDir.Replace("/ConfigData", "/src/GameServer/ConfigData/Generated")
                                                 .Replace("\\ConfigData", "\\src\\GameServer\\ConfigData\\Generated"),
                    GenerateComments = true
                };

                // 如果无法推导路径，使用默认值
                if (!serverConfig.OutputPath.Contains("GameServer"))
                    serverConfig.OutputPath = "Server/src/GameServer/ConfigData/Generated";

                var generator = new CodeGenerator();
                var results = generator.GenerateServerDataClasses(sheets, serverConfig);
                int writeCount = 0;
                int skipCount = 0;

                foreach (var result in results)
                {
                    if (string.IsNullOrEmpty(result.ServerDataClassCode)) continue;

                    var dir = System.IO.Path.GetDirectoryName(result.ServerDataClassPath);
                    if (!string.IsNullOrEmpty(dir) && !System.IO.Directory.Exists(dir))
                        System.IO.Directory.CreateDirectory(dir);

                    bool contentChanged = ConfigExportFileWriter.WriteAllTextIfChanged(
                        result.ServerDataClassPath,
                        result.ServerDataClassCode,
                        System.Text.Encoding.UTF8,
                        ConfigExportFileWriter.RemoveGeneratedTimeLine);
                    if (contentChanged)
                        writeCount++;
                    else
                        skipCount++;
                }
                Debug.Log($"[ExcelExporterWindow] 服务端代码生成完成: {results.Count} 个文件，写入 {writeCount}，未变化跳过 {skipCount}");
            }
            catch (Exception ex)
            {
                Debug.LogError($"[ExcelExporterWindow] 服务端代码生成异常: {ex.Message}");
            }
        }

        private void DrawResults()
        {
            EditorGUILayout.Space(10);
            EditorGUILayout.LabelField("导出结果:", EditorStyles.boldLabel);

            _scrollPosition = EditorGUILayout.BeginScrollView(_scrollPosition, GUILayout.Height(200));

            foreach (var result in _lastResults)
            {
                EditorGUILayout.BeginVertical("box");

                EditorGUILayout.BeginHorizontal();
                EditorGUILayout.LabelField("表名:", GUILayout.Width(60));
                EditorGUILayout.LabelField(result.TableName ?? "未知", EditorStyles.boldLabel);
                EditorGUILayout.LabelField(result.SheetKind.ToString(), GUILayout.Width(70));

                var statusColor = result.Success ? Color.green : Color.red;
                var oldColor = GUI.color;
                GUI.color = statusColor;
                EditorGUILayout.LabelField(result.Success ? "✓ 成功" : "✗ 失败", GUILayout.Width(60));
                GUI.color = oldColor;

                EditorGUILayout.EndHorizontal();

                if (result.Success)
                {
                    EditorGUILayout.BeginHorizontal();
                    EditorGUILayout.LabelField("行数:", GUILayout.Width(60));
                    EditorGUILayout.LabelField(result.RowCount.ToString());
                    EditorGUILayout.EndHorizontal();
                }

                if (!result.Success && !string.IsNullOrEmpty(result.ErrorMessage))
                    EditorGUILayout.HelpBox(result.ErrorMessage, MessageType.Error);

                if (result.Warnings.Count > 0)
                {
                    foreach (var warning in result.Warnings)
                        EditorGUILayout.HelpBox(warning, MessageType.Warning);
                }

                EditorGUILayout.EndVertical();
                EditorGUILayout.Space(5);
            }

            EditorGUILayout.EndScrollView();
        }
    }
}
