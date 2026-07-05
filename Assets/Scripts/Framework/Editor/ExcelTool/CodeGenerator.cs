using System;
using System.Collections.Generic;
using System.Text;
using UnityEngine;

namespace Editor.ExcelTool
{
    /// <summary>
    /// 根据解析后的 Excel 表结构生成强类型配置数据类和表加载类。
    /// </summary>
    public class CodeGenerator
    {
        /// <summary>
        /// 控制生成代码的命名空间、输出路径、特性和格式。
        /// </summary>
        public class GeneratorConfig
        {
            /// <summary>
            /// 生成配置代码使用的命名空间。
            /// </summary>
            public string Namespace { get; set; } = "HotUpdate.Config";

            /// <summary>
            /// 生成数据类的输出目录。
            /// </summary>
            public string DataOutputPath { get; set; } = "Assets/Scripts/HotUpdate/ConfigData/Data";

            /// <summary>
            /// 生成表加载类的输出目录。
            /// </summary>
            public string TableOutputPath { get; set; } = "Assets/Scripts/HotUpdate/ConfigData/Table";

            /// <summary>
            /// 是否为生成文件输出 XML 注释和文件头。
            /// </summary>
            public bool GenerateComments { get; set; } = true;

            /// <summary>
            /// 是否为生成的数据属性添加 SQLite 特性。
            /// </summary>
            public bool UseSQLiteAttributes { get; set; } = true;

            /// <summary>
            /// 是否为生成的数据类添加 Serializable 特性。
            /// </summary>
            public bool GenerateSerializable { get; set; } = true;

            /// <summary>
            /// 写入生成 C# 代码时使用的缩进。
            /// </summary>
            public string Indent { get; set; } = "    ";
        }

        /// <summary>
        /// 保存单个工作表的生成代码和目标文件路径。
        /// </summary>
        public class GenerateResult
        {
            /// <summary>
            /// 生成的数据类源码。
            /// </summary>
            public string DataClassCode { get; set; }

            /// <summary>
            /// 生成的表加载类源码；general 配置没有该内容。
            /// </summary>
            public string TableClassCode { get; set; }

            /// <summary>
            /// 生成数据类的目标文件路径。
            /// </summary>
            public string DataClassPath { get; set; }

            /// <summary>
            /// 生成表加载类的目标文件路径；general 配置没有该路径。
            /// </summary>
            public string TableClassPath { get; set; }

            /// <summary>
            /// 生成的服务端纯 POCO 数据类源码；仅服务端生成时有值。
            /// </summary>
            public string ServerDataClassCode { get; set; }

            /// <summary>
            /// 服务端数据类目标文件路径。
            /// </summary>
            public string ServerDataClassPath { get; set; }
        }

        /// <summary>
        /// 服务端代码生成配置。
        /// </summary>
        public class ServerGeneratorConfig
        {
            /// <summary>
            /// 服务端生成代码的命名空间。
            /// </summary>
            public string Namespace { get; set; } = "GameServer.ConfigData";

            /// <summary>
            /// 服务端数据类输出目录。
            /// </summary>
            public string OutputPath { get; set; } = "Server/src/GameServer/ConfigData/Generated";

            /// <summary>
            /// 是否生成 XML 注释。
            /// </summary>
            public bool GenerateComments { get; set; } = true;
        }

        private readonly GeneratorConfig _config;

        /// <summary>
        /// 使用默认配置或调用方传入的配置创建代码生成器。
        /// </summary>
        public CodeGenerator(GeneratorConfig config = null)
        {
            _config = config ?? new GeneratorConfig();
        }

        /// <summary>
        /// 为单个工作表生成代码；general 表只生成单例数据类。
        /// </summary>
        public GenerateResult GenerateConfigClass(ExcelReader.ExcelSheetData sheetData, string className = null)
        {
            if (sheetData == null)
            {
                throw new ArgumentNullException(nameof(sheetData));
            }

            className = string.IsNullOrEmpty(className) ? sheetData.SheetName : className;
            className = SanitizeClassName(className);

            var result = new GenerateResult
            {
                DataClassCode = GenerateDataClass(sheetData, className),
                DataClassPath = $"{_config.DataOutputPath}/{className}.cs"
            };

            // general 配置通过 ConfigManager.GetConfig<T>() 直接读取数据类，不需要额外的 Table 类。
            if (sheetData.SheetKind != ExcelReader.ExcelSheetKind.General)
            {
                result.TableClassCode = GenerateTableClass(sheetData, className);
                result.TableClassPath = $"{_config.TableOutputPath}/{className}Table.cs";
            }

            return result;
        }

        /// <summary>
        /// 为所有解析出的工作表生成配置代码，并按类名返回结果。
        /// </summary>
        public Dictionary<string, GenerateResult> GenerateConfigClasses(List<ExcelReader.ExcelSheetData> sheets)
        {
            var result = new Dictionary<string, GenerateResult>();

            foreach (var sheet in sheets)
            {
                try
                {
                    string className = SanitizeClassName(sheet.SheetName);
                    result[className] = GenerateConfigClass(sheet, className);
                    Debug.Log($"[CodeGenerator] Generated config: {className}");
                }
                catch (Exception ex)
                {
                    Debug.LogError($"[CodeGenerator] Failed to generate {sheet.SheetName}: {ex.Message}");
                }
            }

            return result;
        }

        // =============================================================================
        //  服务端纯 POCO 数据类生成
        // =============================================================================

        /// <summary>
        /// 为单个工作表生成服务端纯 POCO 数据类（不带 SQLite/Unity 依赖）。
        /// 每张表生成一个独立文件。
        /// </summary>
        /// <param name="sheetData">工作表数据。</param>
        /// <param name="serverConfig">服务端生成配置。</param>
        /// <param name="className">类名（为空时从表名推导）。</param>
        /// <returns>生成结果（仅 ServerDataClassCode / ServerDataClassPath 有效）。</returns>
        public GenerateResult GenerateServerDataClass(
            ExcelReader.ExcelSheetData sheetData,
            ServerGeneratorConfig serverConfig,
            string className = null)
        {
            if (sheetData == null)
                throw new ArgumentNullException(nameof(sheetData));
            if (serverConfig == null)
                throw new ArgumentNullException(nameof(serverConfig));

            className = string.IsNullOrEmpty(className) ? sheetData.SheetName : className;
            className = SanitizeClassName(className);

            var sb = new StringBuilder();

            // 文件头
            if (serverConfig.GenerateComments)
            {
                sb.AppendLine("// ==========================================");
                sb.AppendLine($"// 自动生成的服务端配置类: {className}");
                sb.AppendLine($"// 来源工作表: {sheetData.SheetName}");
                sb.AppendLine($"// 生成时间: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
                sb.AppendLine("// 请勿手动修改，重新导表后会覆盖。");
                sb.AppendLine("// ==========================================");
                sb.AppendLine();
            }

            sb.AppendLine($"namespace {serverConfig.Namespace};");
            sb.AppendLine();

            // 类注释
            if (serverConfig.GenerateComments)
            {
                string kindDesc = sheetData.SheetKind == ExcelReader.ExcelSheetKind.General
                    ? "单例配置" : "表数据行";
                sb.AppendLine($"/// <summary>");
                sb.AppendLine($"/// {sheetData.SheetName} {kindDesc}。");
                sb.AppendLine($"/// </summary>");
            }

            sb.AppendLine($"public class {className}");
            sb.AppendLine("{");

            // 属性
            string indent = _config.Indent;
            for (int i = 0; i < sheetData.FieldNames.Count; i++)
            {
                string fieldName = sheetData.FieldNames[i];
                string typeName = i < sheetData.TypeDefinitions.Count ? sheetData.TypeDefinitions[i] : "string";
                string comment = i < sheetData.Comments.Count ? sheetData.Comments[i] : string.Empty;
                string propertyName = SanitizePropertyName(fieldName);
                string csType = MapToServerCSharpType(typeName);
                string defaultValue = GetServerDefaultValue(csType);

                if (serverConfig.GenerateComments && !string.IsNullOrEmpty(comment))
                {
                    sb.AppendLine($"{indent}/// <summary>{comment}</summary>");
                }

                sb.AppendLine($"{indent}public {csType} {propertyName} {{ get; set; }}{defaultValue}");
            }

            sb.AppendLine("}");

            string filePath = $"{serverConfig.OutputPath}/{className}.cs";

            return new GenerateResult
            {
                ServerDataClassCode = sb.ToString(),
                ServerDataClassPath = filePath
            };
        }

        /// <summary>
        /// 批量为所有工作表生成服务端数据类。
        /// </summary>
        public List<GenerateResult> GenerateServerDataClasses(
            List<ExcelReader.ExcelSheetData> sheets,
            ServerGeneratorConfig serverConfig)
        {
            var results = new List<GenerateResult>();
            foreach (var sheet in sheets)
            {
                try
                {
                    var result = GenerateServerDataClass(sheet, serverConfig);
                    results.Add(result);
                    Debug.Log($"[CodeGenerator] 服务端代码生成: {result.ServerDataClassPath}");
                }
                catch (Exception ex)
                {
                    Debug.LogError($"[CodeGenerator] 服务端代码生成失败 {sheet.SheetName}: {ex.Message}");
                }
            }
            return results;
        }

        /// <summary>
        /// 将配表类型映射为服务端 C# 类型（不依赖 Unity）。
        /// </summary>
        private static string MapToServerCSharpType(string typeName)
        {
            switch ((typeName ?? "string").Trim().ToLowerInvariant())
            {
                case "int": return "int";
                case "long": return "long";
                case "short": return "short";
                case "byte": return "byte";
                case "float": return "float";
                case "double": return "double";
                case "decimal": return "decimal";
                case "bool": return "bool";
                case "string": return "string";
                case "int[]": return "string"; // 服务端按字符串存储，运行时自行解析
                case "float[]": return "string";
                case "string[]": return "string";
                default: return "string"; // 自定义类型统一按字符串传递
            }
        }

        /// <summary>
        /// 获取服务端类型的默认值后缀（string 需要 = string.Empty 避免 nullable 警告）。
        /// </summary>
        private static string GetServerDefaultValue(string csType)
        {
            if (csType == "string")
                return " = string.Empty;";
            return "";
        }

        /// <summary>
        /// 生成单个工作表对应的数据类源码。
        /// </summary>
        private string GenerateDataClass(ExcelReader.ExcelSheetData sheetData, string className)
        {
            var sb = new StringBuilder();

            GenerateFileHeader(sb, className, sheetData.SheetName);
            GenerateDataUsings(sb);

            sb.AppendLine($"namespace {_config.Namespace}.Data");
            sb.AppendLine("{");
            GenerateDataClassBody(sb, sheetData, className);
            sb.AppendLine("}");

            return sb.ToString();
        }

        /// <summary>
        /// 生成普通表工作表对应的表加载类源码。
        /// </summary>
        private string GenerateTableClass(ExcelReader.ExcelSheetData sheetData, string className)
        {
            var sb = new StringBuilder();

            if (_config.GenerateComments)
            {
                sb.AppendLine("// ==========================================");
                sb.AppendLine($"// 自动生成的表加载类: {className}Table");
                sb.AppendLine($"// 来源工作表: {sheetData.SheetName}");
                sb.AppendLine($"// 生成时间: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
                sb.AppendLine("// ==========================================");
                sb.AppendLine();
            }

            sb.AppendLine("using System;");
            sb.AppendLine("using Framework.Data;");
            string dataNamespace = $"{_config.Namespace}.Data";
            if (dataNamespace != "Framework.Data")
            {
                sb.AppendLine($"using {dataNamespace};");
            }
            sb.AppendLine();

            sb.AppendLine($"namespace {_config.Namespace}.Table");
            sb.AppendLine("{");
            GenerateTableClassBody(sb, sheetData, className);
            sb.AppendLine("}");

            return sb.ToString();
        }

        /// <summary>
        /// 在开启注释生成时写入生成文件头。
        /// </summary>
        private void GenerateFileHeader(StringBuilder sb, string className, string sheetName)
        {
            if (!_config.GenerateComments)
            {
                return;
            }

            sb.AppendLine("// ==========================================");
            sb.AppendLine($"// 自动生成的配置类: {className}");
            sb.AppendLine($"// 来源工作表: {sheetName}");
            sb.AppendLine($"// 生成时间: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            sb.AppendLine("// ==========================================");
            sb.AppendLine();
        }

        /// <summary>
        /// 写入生成数据类所需的 using 引用。
        /// </summary>
        private void GenerateDataUsings(StringBuilder sb)
        {
            sb.AppendLine("using System;");
            sb.AppendLine("using System.Collections.Generic;");
            if (_config.UseSQLiteAttributes)
            {
                sb.AppendLine("using SQLite;");
            }
            sb.AppendLine("using Framework;");
            sb.AppendLine("using Framework.Data;");
            sb.AppendLine();
        }

        /// <summary>
        /// 写入生成数据类主体，包括 SQLite 和 general 配置特性。
        /// </summary>
        private void GenerateDataClassBody(StringBuilder sb, ExcelReader.ExcelSheetData sheetData, string className)
        {
            string indent = _config.Indent;

            if (_config.GenerateComments)
            {
                sb.AppendLine($"{indent}/// <summary>");
                sb.AppendLine($"{indent}/// {className} 配置数据。");
                sb.AppendLine($"{indent}/// </summary>");
            }

            if (_config.UseSQLiteAttributes)
            {
                sb.AppendLine($"{indent}[Table(\"{sheetData.SheetName}\")]");
            }

            if (sheetData.SheetKind == ExcelReader.ExcelSheetKind.General)
            {
                // 该特性用于让 ConfigManager.GetConfig<T>() 走单例 general 配置加载逻辑。
                sb.AppendLine($"{indent}[GeneralConfig]");
            }

            if (_config.GenerateSerializable)
            {
                sb.AppendLine($"{indent}[Serializable]");
            }

            sb.AppendLine($"{indent}public class {className}");
            sb.AppendLine($"{indent}{{");

            for (int i = 0; i < sheetData.FieldNames.Count; i++)
            {
                string fieldName = sheetData.FieldNames[i];
                string typeName = i < sheetData.TypeDefinitions.Count ? sheetData.TypeDefinitions[i] : "string";
                string comment = i < sheetData.Comments.Count ? sheetData.Comments[i] : string.Empty;
                bool isPrimaryKey = sheetData.SheetKind != ExcelReader.ExcelSheetKind.General && i == 0;
                GenerateProperty(sb, fieldName, typeName, comment, isPrimaryKey, indent + _config.Indent);
            }

            sb.AppendLine($"{indent}}}");
            sb.AppendLine();
        }

        /// <summary>
        /// 写入一个生成属性及其 SQLite 列元数据。
        /// </summary>
        private void GenerateProperty(StringBuilder sb, string fieldName, string typeName, string comment, bool isPrimaryKey, string indent)
        {
            string propertyName = SanitizePropertyName(fieldName);

            if (_config.GenerateComments && !string.IsNullOrEmpty(comment))
            {
                sb.AppendLine($"{indent}/// <summary>");
                sb.AppendLine($"{indent}/// {comment}");
                sb.AppendLine($"{indent}/// </summary>");
            }

            if (_config.UseSQLiteAttributes)
            {
                if (isPrimaryKey)
                {
                    sb.AppendLine($"{indent}[PrimaryKey]");
                }

                sb.AppendLine($"{indent}[Column(\"{fieldName}\")]");
            }

            sb.AppendLine($"{indent}public {typeName} {propertyName} {{ get; set; }}");
            sb.AppendLine();
        }

        /// <summary>
        /// 为普通多行配置表写入 ConfigBase 派生的表加载类。
        /// </summary>
        private void GenerateTableClassBody(StringBuilder sb, ExcelReader.ExcelSheetData sheetData, string className)
        {
            string indent = _config.Indent;
            string primaryKeyType = sheetData.TypeDefinitions.Count > 0 ? sheetData.TypeDefinitions[0] : "int";
            string firstPropertyName = sheetData.FieldNames.Count > 0 ? SanitizePropertyName(sheetData.FieldNames[0]) : "Id";
            string loaderClassName = $"{className}Table";

            if (_config.GenerateComments)
            {
                sb.AppendLine($"{indent}/// <summary>");
                sb.AppendLine($"{indent}/// {className} 表加载器。");
                sb.AppendLine($"{indent}/// </summary>");
            }

            sb.AppendLine($"{indent}public class {loaderClassName} : ConfigBase<{primaryKeyType}, {className}>");
            sb.AppendLine($"{indent}{{");

            if (_config.GenerateComments)
            {
                sb.AppendLine($"{indent}{_config.Indent}/// <summary>");
                sb.AppendLine($"{indent}{_config.Indent}/// 构造函数。");
                sb.AppendLine($"{indent}{_config.Indent}/// </summary>");
            }

            sb.AppendLine($"{indent}{_config.Indent}public {loaderClassName}()");
            sb.AppendLine($"{indent}{_config.Indent}{{");
            sb.AppendLine($"{indent}{_config.Indent}{_config.Indent}// ConfigManager 会按需加载该配置表。");
            sb.AppendLine($"{indent}{_config.Indent}}}");
            sb.AppendLine();

            if (_config.GenerateComments)
            {
                sb.AppendLine($"{indent}{_config.Indent}/// <summary>");
                sb.AppendLine($"{indent}{_config.Indent}/// 返回单行配置数据的主键。");
                sb.AppendLine($"{indent}{_config.Indent}/// </summary>");
            }

            sb.AppendLine($"{indent}{_config.Indent}protected override {primaryKeyType} GetKey({className} item)");
            sb.AppendLine($"{indent}{_config.Indent}{{");
            sb.AppendLine($"{indent}{_config.Indent}{_config.Indent}return item.{firstPropertyName};");
            sb.AppendLine($"{indent}{_config.Indent}}}");
            sb.AppendLine($"{indent}}}");
        }

        /// <summary>
        /// 将工作表名称或用户输入转换为合法的 PascalCase 类名。
        /// </summary>
        private string SanitizeClassName(string name)
        {
            return ToPascalIdentifier(name, "Config");
        }

        /// <summary>
        /// 将字段名称转换为合法的 PascalCase 属性名。
        /// </summary>
        private string SanitizePropertyName(string name)
        {
            return ToPascalIdentifier(name, "Value");
        }

        /// <summary>
        /// 将任意文本规范化为 PascalCase C# 标识符，失败时使用兜底名称。
        /// </summary>
        private string ToPascalIdentifier(string name, string fallback)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                return fallback;
            }

            var sb = new StringBuilder();
            bool capitalizeNext = true;

            foreach (char c in name)
            {
                if (char.IsLetterOrDigit(c))
                {
                    if (sb.Length == 0 && char.IsDigit(c))
                    {
                        sb.Append('_');
                    }

                    sb.Append(capitalizeNext ? char.ToUpperInvariant(c) : c);
                    capitalizeNext = false;
                }
                else
                {
                    capitalizeNext = true;
                }
            }

            return sb.Length == 0 ? fallback : sb.ToString();
        }
    }
}
