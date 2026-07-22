using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using Editor.ExcelTool;
using UnityEditor;
using UnityEngine;

namespace Framework.Editor.UI
{
    /// <summary>校验 UIWindow.xlsx 的稳定 ID/跨表引用，并生成 UIWindowIds.g.cs 与 UITargetIds.g.cs。</summary>
    public static class UIWindowConfigCompiler
    {
        public const string WorkbookPath = "Assets/RefData_Excel/UIWindow.xlsx";
        public const string GeneratedIdsPath = "Assets/Scripts/HotUpdate/Generated/UIWindowIds.g.cs";

        private const string ModuleSheet = "ui_window_module_ref";
        private const string WindowSheet = "ui_window_ref";
        private const string TargetSheet = "ui_target_ref";
        private const string WindowRetiredSheet = "ui_window_retired_ref";
        private const string TargetRetiredSheet = "ui_target_retired_ref";

        [MenuItem("Tools/Framework/UI/Import Window Configuration")]
        public static void ImportMenu() => ImportConfiguration(interactive: true);

        internal static bool ImportConfiguration(bool interactive)
        {
            if (!TryCompile(out UIWindowCatalog catalog, out string report))
            {
                Debug.LogError("[UIWindowConfig] 导入失败：\n" + report);
                if (interactive) EditorUtility.DisplayDialog("窗口配置导入失败", report, "确定");
                return false;
            }

            try
            {
                WriteArtifacts(catalog);
                AssetDatabase.Refresh();
                Debug.Log("[UIWindowConfig] 导入完成。\n" + report);
                if (interactive) EditorUtility.DisplayDialog("窗口配置导入完成", report, "确定");
                return true;
            }
            catch (Exception ex)
            {
                Debug.LogException(ex);
                if (interactive) EditorUtility.DisplayDialog("窗口配置产物写入失败", ex.Message, "确定");
                return false;
            }
        }

        public static bool TryCompile(out UIWindowCatalog catalog, out string report)
        {
            catalog = null;
            if (!File.Exists(WorkbookPath))
            {
                report = $"缺少配置文件：{WorkbookPath}";
                return false;
            }

            try
            {
                Dictionary<string, ExcelReader.ExcelSheetData> sheets = new ExcelReader()
                    .ReadExcel(WorkbookPath)
                    .ToDictionary(sheet => sheet.SheetName, StringComparer.OrdinalIgnoreCase);
                string[] required =
                {
                    ModuleSheet, WindowSheet, TargetSheet, WindowRetiredSheet, TargetRetiredSheet,
                };
                string[] missing = required.Where(name => !sheets.ContainsKey(name)).ToArray();
                if (missing.Length > 0)
                {
                    report = "缺少工作表：" + string.Join(", ", missing);
                    return false;
                }

                ValidateSchema(sheets[ModuleSheet], new Dictionary<string, string>
                {
                    ["Id"] = "int", ["CodeName"] = "string", ["Description"] = "string",
                    ["WindowIdMin"] = "int", ["WindowIdMax"] = "int",
                    ["TargetIdMin"] = "int", ["TargetIdMax"] = "int",
                });
                ValidateSchema(sheets[WindowSheet], new Dictionary<string, string>
                {
                    ["Id"] = "int", ["ModuleId"] = "int", ["CodeName"] = "string",
                    ["LogicType"] = "string", ["RegistrationMode"] = nameof(UIWindowRegistrationMode),
                    ["Address"] = "string", ["Layer"] = nameof(UILayer), ["AllowMultiple"] = "bool",
                    ["StackBehavior"] = nameof(UIStackBehavior), ["BlockerMode"] = nameof(UIBlockerMode),
                    ["Description"] = "string",
                });
                ValidateSchema(sheets[TargetSheet], new Dictionary<string, string>
                {
                    ["Id"] = "int", ["ModuleId"] = "int", ["WindowId"] = "int",
                    ["CodeName"] = "string", ["Description"] = "string",
                });
                ValidateRetiredSchema(sheets[WindowRetiredSheet]);
                ValidateRetiredSchema(sheets[TargetRetiredSheet]);

                catalog = new UIWindowCatalog
                {
                    Modules = ParseModules(sheets[ModuleSheet]),
                    Windows = ParseWindows(sheets[WindowSheet]),
                    Targets = ParseTargets(sheets[TargetSheet]),
                    RetiredWindowIds = ParseRetired(sheets[WindowRetiredSheet]),
                    RetiredTargetIds = ParseRetired(sheets[TargetRetiredSheet]),
                };
                ValidateCatalog(catalog);
                report = $"模块 {catalog.Modules.Length}，窗口 {catalog.Windows.Length}，" +
                         $"Target {catalog.Targets.Length}，退休窗口 ID {catalog.RetiredWindowIds.Length}，" +
                         $"退休 TargetId {catalog.RetiredTargetIds.Length}。";
                return true;
            }
            catch (Exception ex)
            {
                catalog = null;
                report = ex.Message;
                return false;
            }
        }

        public static void WriteArtifacts(UIWindowCatalog catalog)
        {
            if (catalog == null) throw new ArgumentNullException(nameof(catalog));
            Directory.CreateDirectory(Path.GetDirectoryName(GeneratedIdsPath));
            ConfigExportFileWriter.WriteAllTextIfChanged(
                GeneratedIdsPath,
                GenerateIdsCode(catalog),
                new UTF8Encoding(false));
        }

        public static string GenerateIdsCode(UIWindowCatalog catalog)
        {
            if (catalog == null) throw new ArgumentNullException(nameof(catalog));
            var modules = catalog.Modules.ToDictionary(module => module.Id);
            var builder = new StringBuilder(4096);
            builder.AppendLine("// <auto-generated>")
                .AppendLine("// 来源：Assets/RefData_Excel/UIWindow.xlsx。请勿手改；相同输入生成完全相同内容。")
                .AppendLine("// </auto-generated>")
                .AppendLine()
                .AppendLine("namespace HotUpdate.UI.Generated")
                .AppendLine("{")
                .AppendLine("    public enum UIWindowModuleId")
                .AppendLine("    {");
            foreach (UIWindowModuleDefinition module in catalog.Modules.OrderBy(value => value.Id))
                builder.Append("        ").Append(Identifier(module.Key)).Append(" = ")
                    .Append(module.Id).AppendLine(",");
            builder.AppendLine("    }").AppendLine();
            AppendGroupedConstants(builder, "UIWindowIds", catalog.Windows
                .Select(window => new ConstantRow(window.ModuleId, window.Id, window.Key)), modules);
            builder.AppendLine();
            AppendGroupedConstants(builder, "UITargetIds", catalog.Targets
                .Select(target => new ConstantRow(target.ModuleId, target.Id, target.Key)), modules);
            builder.AppendLine("}");
            return builder.ToString().Replace("\r\n", "\n");
        }

        private static void AppendGroupedConstants(
            StringBuilder builder,
            string className,
            IEnumerable<ConstantRow> rows,
            IReadOnlyDictionary<int, UIWindowModuleDefinition> modules)
        {
            builder.Append("    public static class ").Append(className).AppendLine()
                .AppendLine("    {");
            foreach (IGrouping<int, ConstantRow> group in rows
                         .OrderBy(row => row.ModuleId).ThenBy(row => row.Id).GroupBy(row => row.ModuleId))
            {
                string moduleName = modules.TryGetValue(group.Key, out UIWindowModuleDefinition module)
                    ? Identifier(module.Key)
                    : "Module" + group.Key;
                builder.Append("        public static class ").Append(moduleName).AppendLine()
                    .AppendLine("        {");
                var identifiers = new HashSet<string>(StringComparer.Ordinal);
                foreach (ConstantRow row in group)
                {
                    string identifier = Identifier(row.Key);
                    if (!identifiers.Add(identifier))
                        throw new InvalidOperationException($"{className}.{moduleName} 常量名冲突：{identifier}。");
                    builder.Append("            public const int ").Append(identifier).Append(" = ")
                        .Append(row.Id).AppendLine(";");
                }
                builder.AppendLine("        }");
            }
            builder.AppendLine("    }");
        }

        private readonly struct ConstantRow
        {
            public ConstantRow(int moduleId, int id, string key)
            {
                ModuleId = moduleId;
                Id = id;
                Key = key;
            }
            public int ModuleId { get; }
            public int Id { get; }
            public string Key { get; }
        }

        private static UIWindowModuleDefinition[] ParseModules(ExcelReader.ExcelSheetData sheet)
            => sheet.DataRows.Select((row, index) => new UIWindowModuleDefinition
            {
                Id = Int(row, "Id", sheet, index),
                Key = CodeName(row, "CodeName", sheet, index),
                Description = String(row, "Description"),
                WindowIdMin = Int(row, "WindowIdMin", sheet, index),
                WindowIdMax = Int(row, "WindowIdMax", sheet, index),
                TargetIdMin = Int(row, "TargetIdMin", sheet, index),
                TargetIdMax = Int(row, "TargetIdMax", sheet, index),
            }).ToArray();

        private static UIWindowDefinition[] ParseWindows(ExcelReader.ExcelSheetData sheet)
            => sheet.DataRows.Select((row, index) => new UIWindowDefinition
            {
                Id = Int(row, "Id", sheet, index),
                ModuleId = Int(row, "ModuleId", sheet, index),
                Key = CodeName(row, "CodeName", sheet, index),
                LogicType = Required(row, "LogicType", sheet, index),
                RegistrationMode = EnumValue<UIWindowRegistrationMode>(row, "RegistrationMode", sheet, index),
                Address = String(row, "Address"),
                Layer = EnumValue<UILayer>(row, "Layer", sheet, index),
                AllowMultiple = Bool(row, "AllowMultiple"),
                StackBehavior = EnumValue<UIStackBehavior>(row, "StackBehavior", sheet, index),
                BlockerMode = EnumValue<UIBlockerMode>(row, "BlockerMode", sheet, index),
                Description = String(row, "Description"),
            }).ToArray();

        private static UITargetDefinition[] ParseTargets(ExcelReader.ExcelSheetData sheet)
            => sheet.DataRows.Select((row, index) => new UITargetDefinition
            {
                Id = Int(row, "Id", sheet, index),
                ModuleId = Int(row, "ModuleId", sheet, index),
                WindowId = Int(row, "WindowId", sheet, index),
                Key = CodeName(row, "CodeName", sheet, index),
                Description = String(row, "Description"),
            }).ToArray();

        private static UIStableIdRetiredDefinition[] ParseRetired(ExcelReader.ExcelSheetData sheet)
            => sheet.DataRows.Select((row, index) => new UIStableIdRetiredDefinition
            {
                Id = Int(row, "Id", sheet, index),
                FormerKey = String(row, "FormerKey"),
                RetiredVersion = String(row, "RetiredVersion"),
                Reason = String(row, "Reason"),
            }).ToArray();

        private static void ValidateCatalog(UIWindowCatalog catalog)
        {
            Dictionary<int, UIWindowModuleDefinition> modules = Unique(
                catalog.Modules, value => value.Id, "Module Id");
            Unique(catalog.Modules, value => value.Key, "Module CodeName", StringComparer.Ordinal);
            foreach (UIWindowModuleDefinition module in catalog.Modules)
            {
                if (module.Id <= 0) throw new InvalidDataException("Module Id 必须大于 0。");
                if (module.WindowIdMin <= 0 || module.WindowIdMin > module.WindowIdMax)
                    throw new InvalidDataException($"模块 {module.Key} WindowId 号段非法。");
                if (module.TargetIdMin <= 0 || module.TargetIdMin > module.TargetIdMax)
                    throw new InvalidDataException($"模块 {module.Key} TargetId 号段非法。");
            }
            ValidateNonOverlappingRanges(catalog.Modules, true);
            ValidateNonOverlappingRanges(catalog.Modules, false);

            Dictionary<int, UIWindowDefinition> windows = Unique(catalog.Windows, value => value.Id, "Window Id");
            var windowKeys = new HashSet<string>(StringComparer.Ordinal);
            foreach (UIWindowDefinition window in catalog.Windows)
            {
                if (!modules.TryGetValue(window.ModuleId, out UIWindowModuleDefinition module))
                    throw new InvalidDataException($"Window {window.Id} ModuleId 不存在：{window.ModuleId}。");
                if (window.Id < module.WindowIdMin || window.Id > module.WindowIdMax)
                    throw new InvalidDataException($"Window {window.Id} 不在模块 {module.Key} WindowId 号段内。");
                if (!windowKeys.Add(window.ModuleId + ":" + window.Key))
                    throw new InvalidDataException($"模块 {module.Key} Window CodeName 重复：{window.Key}。");
                if (window.RegistrationMode == UIWindowRegistrationMode.Addressable
                    && string.IsNullOrWhiteSpace(window.Address))
                    throw new InvalidDataException($"Addressable Window {window.Id} Address 不能为空。");
            }

            Unique(catalog.Targets, value => value.Id, "Target Id");
            var targetKeys = new HashSet<string>(StringComparer.Ordinal);
            foreach (UITargetDefinition target in catalog.Targets)
            {
                if (!modules.TryGetValue(target.ModuleId, out UIWindowModuleDefinition module))
                    throw new InvalidDataException($"Target {target.Id} ModuleId 不存在：{target.ModuleId}。");
                if (!windows.TryGetValue(target.WindowId, out UIWindowDefinition window))
                    throw new InvalidDataException($"Target {target.Id} WindowId 不存在：{target.WindowId}。");
                if (window.ModuleId != target.ModuleId)
                    throw new InvalidDataException($"Target {target.Id} 与 Window {target.WindowId} 不属于同一模块。");
                if (target.Id < module.TargetIdMin || target.Id > module.TargetIdMax)
                    throw new InvalidDataException($"Target {target.Id} 不在模块 {module.Key} TargetId 号段内。");
                if (!targetKeys.Add(target.ModuleId + ":" + target.Key))
                    throw new InvalidDataException($"模块 {module.Key} Target CodeName 重复：{target.Key}。");
            }

            Dictionary<int, UIStableIdRetiredDefinition> retiredWindows = Unique(
                catalog.RetiredWindowIds, value => value.Id, "退休 WindowId");
            Dictionary<int, UIStableIdRetiredDefinition> retiredTargets = Unique(
                catalog.RetiredTargetIds, value => value.Id, "退休 TargetId");
            foreach (int id in retiredWindows.Keys)
                if (windows.ContainsKey(id)) throw new InvalidDataException($"WindowId {id} 已退休，不得复用。");
            var targetIds = new HashSet<int>(catalog.Targets.Select(target => target.Id));
            foreach (int id in retiredTargets.Keys)
                if (targetIds.Contains(id)) throw new InvalidDataException($"TargetId {id} 已退休，不得复用。");
        }

        private static void ValidateNonOverlappingRanges(UIWindowModuleDefinition[] modules, bool windows)
        {
            UIWindowModuleDefinition[] sorted = modules.OrderBy(module => windows ? module.WindowIdMin : module.TargetIdMin).ToArray();
            for (int i = 1; i < sorted.Length; i++)
            {
                int previousMax = windows ? sorted[i - 1].WindowIdMax : sorted[i - 1].TargetIdMax;
                int currentMin = windows ? sorted[i].WindowIdMin : sorted[i].TargetIdMin;
                if (currentMin <= previousMax)
                    throw new InvalidDataException(
                        $"模块 {sorted[i - 1].Key} 与 {sorted[i].Key} 的 {(windows ? "WindowId" : "TargetId")} 号段重叠。");
            }
        }

        private static Dictionary<TKey, TValue> Unique<TValue, TKey>(
            IEnumerable<TValue> values,
            Func<TValue, TKey> keySelector,
            string name,
            IEqualityComparer<TKey> comparer = null)
        {
            var result = new Dictionary<TKey, TValue>(comparer ?? EqualityComparer<TKey>.Default);
            foreach (TValue value in values)
            {
                TKey key = keySelector(value);
                if (!result.TryAdd(key, value)) throw new InvalidDataException($"{name} 重复：{key}。");
            }
            return result;
        }

        private static void ValidateRetiredSchema(ExcelReader.ExcelSheetData sheet)
            => ValidateSchema(sheet, new Dictionary<string, string>
            {
                ["Id"] = "int", ["FormerKey"] = "string",
                ["RetiredVersion"] = "string", ["Reason"] = "string",
            });

        private static void ValidateSchema(
            ExcelReader.ExcelSheetData sheet,
            IReadOnlyDictionary<string, string> expected)
        {
            foreach (KeyValuePair<string, string> pair in expected)
            {
                int index = sheet.FieldNames.FindIndex(field => string.Equals(field, pair.Key, StringComparison.Ordinal));
                if (index < 0) throw new InvalidDataException($"{sheet.SheetName} 缺少字段 {pair.Key}。");
                string actual = index < sheet.TypeDefinitions.Count ? sheet.TypeDefinitions[index]?.Trim() : string.Empty;
                if (!string.Equals(actual, pair.Value, StringComparison.Ordinal))
                    throw new InvalidDataException($"{sheet.SheetName}.{pair.Key} 类型应为 {pair.Value}，当前为 '{actual}'。");
            }
        }

        private static int Int(
            Dictionary<string, object> row,
            string field,
            ExcelReader.ExcelSheetData sheet,
            int rowIndex)
        {
            string value = String(row, field);
            if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed))
                throw RowError(sheet, rowIndex, $"字段 {field} 不是有效整数：'{value}'。");
            return parsed;
        }

        private static bool Bool(Dictionary<string, object> row, string field)
            => (bool)ExcelReader.ParseCellValue(row.TryGetValue(field, out object value) ? value : null, typeof(bool));

        private static T EnumValue<T>(
            Dictionary<string, object> row,
            string field,
            ExcelReader.ExcelSheetData sheet,
            int rowIndex) where T : struct
        {
            string value = String(row, field);
            if (!Enum.TryParse(value, true, out T parsed) || !Enum.IsDefined(typeof(T), parsed))
                throw RowError(sheet, rowIndex, $"字段 {field} 不是有效 {typeof(T).Name}：'{value}'。");
            return parsed;
        }

        private static string Required(
            Dictionary<string, object> row,
            string field,
            ExcelReader.ExcelSheetData sheet,
            int rowIndex)
        {
            string value = String(row, field);
            if (string.IsNullOrWhiteSpace(value)) throw RowError(sheet, rowIndex, $"字段 {field} 不能为空。");
            return value;
        }

        private static string CodeName(
            Dictionary<string, object> row,
            string field,
            ExcelReader.ExcelSheetData sheet,
            int rowIndex)
        {
            string value = Required(row, field, sheet, rowIndex);
            if (!char.IsLetter(value[0]) && value[0] != '_')
                throw RowError(sheet, rowIndex, $"字段 {field} 必须以字母或下划线开头：'{value}'。");
            for (int i = 1; i < value.Length; i++)
                if (!char.IsLetterOrDigit(value[i]) && value[i] != '_')
                    throw RowError(sheet, rowIndex, $"字段 {field} 只能包含字母、数字和下划线：'{value}'。");
            return value;
        }

        private static string String(Dictionary<string, object> row, string field)
            => row.TryGetValue(field, out object value) && value != null && value != DBNull.Value
                ? Convert.ToString(value, CultureInfo.InvariantCulture)?.Trim() ?? string.Empty
                : string.Empty;

        private static Exception RowError(ExcelReader.ExcelSheetData sheet, int index, string message)
            => new InvalidDataException($"{sheet.SheetName} 第 {index + 4} 行：{message}");

        private static string Identifier(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return "Unnamed";
            var builder = new StringBuilder(value.Length);
            bool upper = true;
            for (int i = 0; i < value.Length; i++)
            {
                char c = value[i];
                if (!char.IsLetterOrDigit(c) && c != '_') { upper = true; continue; }
                if (builder.Length == 0 && char.IsDigit(c)) builder.Append('_');
                builder.Append(upper ? char.ToUpperInvariant(c) : c);
                upper = false;
            }
            return builder.Length == 0 ? "Unnamed" : builder.ToString();
        }
    }

    internal sealed class UIWindowWorkbookPostprocessor : AssetPostprocessor
    {
        private static bool _queued;

        private static void OnPostprocessAllAssets(
            string[] importedAssets,
            string[] deletedAssets,
            string[] movedAssets,
            string[] movedFromAssetPaths)
        {
            if (_queued || importedAssets == null || !importedAssets.Any(path => string.Equals(
                    path, UIWindowConfigCompiler.WorkbookPath, StringComparison.OrdinalIgnoreCase)))
                return;
            _queued = true;
            EditorApplication.delayCall += () =>
            {
                _queued = false;
                UIWindowConfigCompiler.ImportConfiguration(interactive: false);
            };
        }
    }
}
