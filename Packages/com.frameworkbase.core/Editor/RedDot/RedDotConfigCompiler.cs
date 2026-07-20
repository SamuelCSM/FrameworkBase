using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using Editor.ExcelTool;
using Framework.Foundation;
using UnityEditor;
using UnityEngine;

namespace Framework.Editor.RedDot
{
    /// <summary>
    /// 校验 RedDot.xlsx 的跨表拓扑，并生成确定性的业务 ID 常量。
    /// 运行时数据由标准 ConfigData 管线导出和加载，本编译器不再生成第二份目录数据。
    /// </summary>
    public static class RedDotConfigCompiler
    {
        public const string WorkbookPath = "Assets/RefData_Excel/RedDot.xlsx";
        public const string GeneratedIdsPath = "Assets/Scripts/HotUpdate/Generated/RedDotIds.g.cs";

        private const string ModuleSheet = "red_dot_module_ref";
        private const string NodeSheet = "red_dot_node_ref";
        private const string EdgeSheet = "red_dot_edge_ref";
        private const string SeenSheet = "red_dot_seen_policy_ref";
        private const string RetiredSheet = "red_dot_retired_ref";

        [MenuItem("Tools/Framework/Red Dot/Import Configuration")]
        public static void ImportMenu() => ImportConfiguration(interactive: true);

        internal static bool ImportConfiguration(bool interactive)
        {
            if (!TryCompile(out RedDotCatalog catalog, out string report))
            {
                Debug.LogError("[RedDotConfig] 导入失败：\n" + report);
                if (interactive) EditorUtility.DisplayDialog("红点配置导入失败", report, "确定");
                return false;
            }

            try
            {
                WriteArtifacts(catalog);
                AssetDatabase.Refresh();
                Debug.Log("[RedDotConfig] 导入完成。\n" + report);
                if (interactive) EditorUtility.DisplayDialog("红点配置导入完成", report, "确定");
                return true;
            }
            catch (Exception ex)
            {
                Debug.LogException(ex);
                if (interactive) EditorUtility.DisplayDialog("红点配置产物写入失败", ex.Message, "确定");
                return false;
            }
        }

        public static bool TryCompile(out RedDotCatalog catalog, out string report)
        {
            catalog = null;
            if (!File.Exists(WorkbookPath))
            {
                report = $"缺少配置文件：{WorkbookPath}";
                return false;
            }

            try
            {
                List<ExcelReader.ExcelSheetData> sheets = new ExcelReader().ReadExcel(WorkbookPath);
                var byName = sheets.ToDictionary(sheet => sheet.SheetName, StringComparer.OrdinalIgnoreCase);
                string[] required = { ModuleSheet, NodeSheet, EdgeSheet, SeenSheet, RetiredSheet };
                var missing = required.Where(name => !byName.ContainsKey(name)).ToArray();
                if (missing.Length > 0)
                {
                    report = "缺少工作表：" + string.Join(", ", missing);
                    return false;
                }

                ValidateSheetSchema(byName[ModuleSheet], new Dictionary<string, string>
                {
                    ["Id"] = "int",
                    ["CodeName"] = "string",
                    ["Description"] = "string",
                    ["IdMin"] = "int",
                    ["IdMax"] = "int",
                });
                ValidateSheetSchema(byName[NodeSheet], new Dictionary<string, string>
                {
                    ["Id"] = "int",
                    ["ModuleId"] = "int",
                    ["CodeName"] = "string",
                    ["Type"] = nameof(RedDotNodeKind),
                    ["Aggregation"] = nameof(RedDotAggregation),
                    ["Description"] = "string",
                });
                ValidateSheetSchema(byName[EdgeSheet], new Dictionary<string, string>
                {
                    ["ParentId"] = "int",
                    ["ChildId"] = "int",
                    ["Description"] = "string",
                });
                ValidateSheetSchema(byName[SeenSheet], new Dictionary<string, string>
                {
                    ["SignalId"] = "int",
                    ["Trigger"] = nameof(RedDotAcknowledgeTrigger),
                    ["SaveMode"] = nameof(RedDotSeenSaveMode),
                    ["Version"] = "int",
                });
                ValidateSheetSchema(byName[RetiredSheet], new Dictionary<string, string>
                {
                    ["Id"] = "int",
                    ["FormerKey"] = "string",
                    ["RetiredVersion"] = "string",
                    ["Reason"] = "string",
                });

                RedDotModuleDefinition[] modules = ParseModules(byName[ModuleSheet]);
                catalog = new RedDotCatalog
                {
                    SchemaVersion = 1,
                    Modules = modules,
                    Nodes = ParseNodes(byName[NodeSheet], modules),
                    Edges = ParseEdges(byName[EdgeSheet]),
                    SeenPolicies = ParseSeenPolicies(byName[SeenSheet]),
                    RetiredIds = ParseRetired(byName[RetiredSheet]),
                };

                RedDotCatalogValidationResult validation = RedDotCatalogValidator.Validate(catalog);
                var builder = new StringBuilder();
                builder.Append("模块 ").Append(catalog.Modules.Length)
                    .Append("，节点 ").Append(catalog.Nodes.Length)
                    .Append("，关系 ").Append(catalog.Edges.Length)
                    .Append("，已看策略 ").Append(catalog.SeenPolicies.Length)
                    .Append("，退休 ID ").Append(catalog.RetiredIds.Length).Append('。');
                if (validation.Warnings.Count > 0)
                {
                    builder.AppendLine().AppendLine("警告：");
                    for (int i = 0; i < validation.Warnings.Count; i++)
                        builder.Append("- ").AppendLine(validation.Warnings[i]);
                }
                if (!validation.IsValid)
                {
                    builder.AppendLine().AppendLine("错误：");
                    for (int i = 0; i < validation.Errors.Count; i++)
                        builder.Append("- ").AppendLine(validation.Errors[i]);
                }
                report = builder.ToString().TrimEnd();
                return validation.IsValid;
            }
            catch (Exception ex)
            {
                catalog = null;
                report = ex.Message;
                return false;
            }
        }

        public static void WriteArtifacts(RedDotCatalog catalog)
        {
            if (catalog == null) throw new ArgumentNullException(nameof(catalog));
            EnsureDirectory(GeneratedIdsPath);
            ConfigExportFileWriter.WriteAllTextIfChanged(
                GeneratedIdsPath, GenerateIdsCode(catalog), new UTF8Encoding(false));
        }

        private static RedDotModuleDefinition[] ParseModules(ExcelReader.ExcelSheetData sheet)
            => sheet.DataRows.Select((row, index) => new RedDotModuleDefinition
            {
                Id = Int(row, "Id", sheet, index),
                Key = CodeName(row, "CodeName", sheet, index),
                Description = String(row, "Description"),
                IdMin = Int(row, "IdMin", sheet, index),
                IdMax = Int(row, "IdMax", sheet, index),
            }).ToArray();

        private static RedDotNodeDefinition[] ParseNodes(
            ExcelReader.ExcelSheetData sheet,
            IEnumerable<RedDotModuleDefinition> modules)
        {
            var moduleKeys = modules.ToDictionary(module => module.Id, module => module.Key);
            return sheet.DataRows.Select((row, index) =>
            {
                int moduleId = Int(row, "ModuleId", sheet, index);
                string codeName = CodeName(row, "CodeName", sheet, index);
                string moduleKey = moduleKeys.TryGetValue(moduleId, out string value)
                    ? value
                    : "MissingModule" + moduleId;
                return new RedDotNodeDefinition
                {
                    Id = Int(row, "Id", sheet, index),
                    Key = moduleKey + "." + codeName,
                    ModuleId = moduleId,
                    Kind = EnumValue<RedDotNodeKind>(row, "Type", sheet, index),
                    Aggregation = EnumValue<RedDotAggregation>(row, "Aggregation", sheet, index),
                    Description = String(row, "Description"),
                };
            }).ToArray();
        }

        private static RedDotEdgeDefinition[] ParseEdges(ExcelReader.ExcelSheetData sheet)
        {
            return sheet.DataRows.Select((row, index) =>
            {
                int parentId = Int(row, "ParentId", sheet, index);
                int childId = Int(row, "ChildId", sheet, index);
                return new RedDotEdgeDefinition
                {
                    ParentId = parentId,
                    ChildId = childId,
                    Description = String(row, "Description"),
                };
            }).ToArray();
        }

        private static RedDotSeenPolicyDefinition[] ParseSeenPolicies(ExcelReader.ExcelSheetData sheet)
            => sheet.DataRows.Select((row, index) => new RedDotSeenPolicyDefinition
            {
                SignalId = Int(row, "SignalId", sheet, index),
                Trigger = EnumValue<RedDotAcknowledgeTrigger>(row, "Trigger", sheet, index),
                SaveMode = EnumValue<RedDotSeenSaveMode>(row, "SaveMode", sheet, index),
                Version = Int(row, "Version", sheet, index),
            }).ToArray();

        private static RedDotRetiredIdDefinition[] ParseRetired(ExcelReader.ExcelSheetData sheet)
            => sheet.DataRows.Select((row, index) => new RedDotRetiredIdDefinition
            {
                Id = Int(row, "Id", sheet, index),
                FormerKey = String(row, "FormerKey"),
                RetiredVersion = String(row, "RetiredVersion"),
                Reason = String(row, "Reason"),
            }).ToArray();

        /// <summary>生成业务侧模块枚举与红点 ID 常量。</summary>
        public static string GenerateIdsCode(RedDotCatalog catalog)
        {
            if (catalog == null) throw new ArgumentNullException(nameof(catalog));
            var modules = catalog.Modules.ToDictionary(module => module.Id);
            var builder = new StringBuilder(2048);
            AppendGeneratedHeader(builder)
                .AppendLine("namespace HotUpdate.RedDot.Generated")
                .AppendLine("{")
                .AppendLine("    public enum RedDotModuleId")
                .AppendLine("    {");
            foreach (RedDotModuleDefinition module in catalog.Modules.OrderBy(value => value.Id))
                builder.Append("        ").Append(Identifier(module.Key)).Append(" = ")
                    .Append(module.Id).AppendLine(",");
            builder.AppendLine("    }").AppendLine()
                .AppendLine("    public static class RedDotIds")
                .AppendLine("    {");

            foreach (IGrouping<int, RedDotNodeDefinition> group in catalog.Nodes
                         .OrderBy(node => node.ModuleId).ThenBy(node => node.Id).GroupBy(node => node.ModuleId))
            {
                string moduleKey = modules.TryGetValue(group.Key, out RedDotModuleDefinition module)
                    ? module.Key : "Module" + group.Key;
                string moduleIdentifier = Identifier(moduleKey);
                builder.Append("        public static class ").Append(moduleIdentifier).AppendLine()
                    .AppendLine("        {");
                var identifiers = new HashSet<string>(StringComparer.Ordinal);
                foreach (RedDotNodeDefinition node in group)
                {
                    string localKey = node.Key;
                    string prefix = moduleKey + ".";
                    if (localKey.StartsWith(prefix, StringComparison.Ordinal)) localKey = localKey.Substring(prefix.Length);
                    string identifier = Identifier(localKey);
                    if (!identifiers.Add(identifier))
                        throw new InvalidOperationException($"模块 {moduleKey} 内生成的常量名冲突：{identifier}。请调整节点 CodeName。");
                    builder.Append("            public const int ").Append(identifier).Append(" = ")
                        .Append(node.Id).AppendLine(";");
                }
                builder.AppendLine("        }");
            }

            builder.AppendLine("    }").AppendLine("}");
            return builder.ToString().Replace("\r\n", "\n");
        }

        private static StringBuilder AppendGeneratedHeader(StringBuilder builder)
            => builder.AppendLine("// <auto-generated>")
                .AppendLine("// 来源：Assets/RefData_Excel/RedDot.xlsx。请勿手改；相同输入生成完全相同内容。")
                .AppendLine("// </auto-generated>")
                .AppendLine();

        private static string Identifier(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return "Unnamed";
            var builder = new StringBuilder(value.Length);
            bool upper = true;
            for (int i = 0; i < value.Length; i++)
            {
                char c = value[i];
                if (!char.IsLetterOrDigit(c) && c != '_')
                {
                    upper = true;
                    continue;
                }
                if (builder.Length == 0 && char.IsDigit(c)) builder.Append('_');
                builder.Append(upper ? char.ToUpperInvariant(c) : c);
                upper = false;
            }
            return builder.Length == 0 ? "Unnamed" : builder.ToString();
        }

        private static int Int(Dictionary<string, object> row, string field, ExcelReader.ExcelSheetData sheet, int rowIndex)
        {
            string value = String(row, field);
            if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int result))
                throw RowError(sheet, rowIndex, $"字段 {field} 不是有效整数：'{value}'。");
            return result;
        }

        private static string String(Dictionary<string, object> row, string field)
        {
            if (!row.TryGetValue(field, out object value) || value == null || value == DBNull.Value) return string.Empty;
            return Convert.ToString(value, CultureInfo.InvariantCulture)?.Trim() ?? string.Empty;
        }

        private static string CodeName(
            Dictionary<string, object> row,
            string field,
            ExcelReader.ExcelSheetData sheet,
            int rowIndex)
        {
            string value = String(row, field);
            if (string.IsNullOrEmpty(value))
                throw RowError(sheet, rowIndex, $"字段 {field} 不能为空。");
            if (!char.IsLetter(value[0]) && value[0] != '_')
                throw RowError(sheet, rowIndex, $"字段 {field} 必须以字母或下划线开头：'{value}'。");
            for (int i = 1; i < value.Length; i++)
            {
                if (!char.IsLetterOrDigit(value[i]) && value[i] != '_')
                    throw RowError(sheet, rowIndex, $"字段 {field} 只能包含字母、数字和下划线：'{value}'。");
            }
            return value;
        }

        private static T EnumValue<T>(Dictionary<string, object> row, string field, ExcelReader.ExcelSheetData sheet, int rowIndex)
            where T : struct
        {
            string value = String(row, field);
            if (!Enum.TryParse(value, true, out T parsed) || !Enum.IsDefined(typeof(T), parsed))
                throw RowError(sheet, rowIndex, $"字段 {field} 不是有效 {typeof(T).Name}：'{value}'。");
            return (T)ExcelReader.ParseCellValue(value, typeof(T));
        }

        private static void ValidateSheetSchema(
            ExcelReader.ExcelSheetData sheet,
            IReadOnlyDictionary<string, string> expectedTypes)
        {
            foreach (KeyValuePair<string, string> expected in expectedTypes)
            {
                int index = sheet.FieldNames.FindIndex(
                    field => string.Equals(field, expected.Key, StringComparison.Ordinal));
                if (index < 0)
                    throw new InvalidDataException($"{sheet.SheetName} 缺少字段 {expected.Key}。");
                string actual = index < sheet.TypeDefinitions.Count
                    ? sheet.TypeDefinitions[index]?.Trim()
                    : string.Empty;
                if (!string.Equals(actual, expected.Value, StringComparison.Ordinal))
                    throw new InvalidDataException(
                        $"{sheet.SheetName}.{expected.Key} 的类型应为 {expected.Value}，当前为 '{actual}'。");
            }
        }

        private static Exception RowError(ExcelReader.ExcelSheetData sheet, int zeroBasedDataIndex, string message)
            => new InvalidDataException($"{sheet.SheetName} 第 {zeroBasedDataIndex + 4} 行：{message}");

        private static void EnsureDirectory(string path)
        {
            string directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
        }
    }

    /// <summary>RedDot.xlsx 重新导入后自动执行跨表校验并刷新 ID 代码；运行时数据仍由标准 ConfigPipeline 导出。</summary>
    internal sealed class RedDotWorkbookPostprocessor : AssetPostprocessor
    {
        private static bool _queued;

        private static void OnPostprocessAllAssets(
            string[] importedAssets,
            string[] deletedAssets,
            string[] movedAssets,
            string[] movedFromAssetPaths)
        {
            if (_queued || importedAssets == null ||
                !importedAssets.Any(path => string.Equals(
                    path, RedDotConfigCompiler.WorkbookPath, StringComparison.OrdinalIgnoreCase)))
                return;

            _queued = true;
            EditorApplication.delayCall += () =>
            {
                _queued = false;
                RedDotConfigCompiler.ImportConfiguration(interactive: false);
            };
        }
    }
}
