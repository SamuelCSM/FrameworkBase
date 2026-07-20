using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Editor.ExcelTool;
using Framework.Editor.RedDot;
using UnityEditor;
using UnityEngine;

namespace Framework.Editor
{
    /// <summary>
    /// 配表一键管线：Excel（Assets/RefData_Excel）→ 代码生成 + config.db 导出，单入口全链路。
    ///
    /// 背景：ExcelExporterWindow / ConfigTableEditorWindow 是交互式窗口，代码生成与数据导出
    /// 分散在两处、无法无人值守。本管线把「改表 → 生效」收敛为一步，提供菜单与 batchmode
    /// 双入口，供采用框架标准目录约定的项目日常改表与 CI 复用。
    ///
    /// 产物（沿用工具链既有默认约定）：
    ///   - 代码：Assets/Scripts/HotUpdate/ConfigData/{Data,Table}（命名空间 HotUpdate.Config，热更侧）
    ///   - 首包库：Assets/StreamingAssets/RefData/config.db
    ///   - 热更库：Assets/ResourcesOut/RefData/config.db.bytes（Addressables 同步规则去 .bytes）
    ///
    /// batchmode 入口 <see cref="ExportAllForBuilder"/> 失败抛异常（规避退出码坑），
    /// 成功打印哨兵 <c>CONFIG_PIPELINE_OK</c>。
    /// </summary>
    public static class ConfigPipeline
    {
        private const string ExcelFolder = "Assets/RefData_Excel";

        [MenuItem("Framework/Config/Export All (Excel→代码+config.db)")]
        public static void ExportAllMenu()
        {
            try
            {
                string summary = ExportAll();
                EditorUtility.DisplayDialog("Config Pipeline", summary, "确定");
            }
            catch (Exception ex)
            {
                EditorUtility.DisplayDialog("Config Pipeline 失败", ex.Message, "确定");
                throw;
            }
        }

        /// <summary>
        /// batchmode 入口：Unity.exe -batchmode -quit -executeMethod Framework.Editor.ConfigPipeline.ExportAllForBuilder
        /// </summary>
        public static void ExportAllForBuilder() => ExportAll();

        private static string ExportAll()
        {
            if (!Directory.Exists(ExcelFolder))
                throw new InvalidOperationException($"[ConfigPipeline] Excel 目录不存在：{ExcelFolder}");

            List<string> excelFiles = Directory.GetFiles(ExcelFolder, "*.xlsx", SearchOption.AllDirectories)
                .Where(f => !Path.GetFileName(f).StartsWith("~$")) // 跳过 Excel 打开时的锁文件
                .ToList();
            if (excelFiles.Count == 0)
                throw new InvalidOperationException($"[ConfigPipeline] {ExcelFolder} 下没有 .xlsx 配置表");

            // ── 1. 代码生成（数据类 + 表加载类，默认输出到热更侧 HotUpdate.Config）──
            var reader = new ExcelReader();
            var generator = new CodeGenerator();
            var exportRules = ConfigExportRuleResolver.LoadDefault();
            int generatedCount = 0;
            foreach (string file in excelFiles)
            {
                foreach (ExcelReader.ExcelSheetData sheet in reader.ReadExcel(file))
                {
                    sheet.SheetKind = exportRules.ResolveSheetKind(sheet.SheetName, sheet.SheetKind);

                    // 框架 Bootstrap 表（如 language）只导数据不生成代码：
                    // 生成类已预置于包内 ConfigData/Bootstrap/，再生成会在热更侧长出重复类（ADR-006）。
                    if (ConfigShardCatalog.IsFrameworkBootstrapTable(sheet.SheetName))
                    {
                        Debug.Log($"[ConfigPipeline] 跳过代码生成（框架 Bootstrap 表，生成类预置于 ConfigData/Bootstrap）：{sheet.SheetName}");
                        continue;
                    }

                    if (!exportRules.ShouldGenerateClientCode(sheet.SheetName))
                    {
                        Debug.Log($"[ConfigPipeline] 跳过通用客户端代码生成（专用/服务端表）：{sheet.SheetName}");
                        continue;
                    }

                    CodeGenerator.GenerateResult result = generator.GenerateConfigClass(sheet);
                    WriteGenerated(result.DataClassPath, result.DataClassCode);
                    WriteGenerated(result.TableClassPath, result.TableClassCode);
                    generatedCount++;
                    Debug.Log($"[ConfigPipeline] 代码生成：{sheet.SheetName} → {result.DataClassPath}");
                }
            }

            // 红点仍需要标准表生成之外的跨表 DAG 校验与稳定 ID 常量；把它纳入同一导出入口，
            // 避免只更新 config.db 却遗漏 RedDotIds.g.cs。
            if (File.Exists(RedDotConfigCompiler.WorkbookPath))
            {
                if (!RedDotConfigCompiler.TryCompile(out Framework.Foundation.RedDotCatalog redDotCatalog,
                        out string redDotReport))
                    throw new InvalidOperationException("[ConfigPipeline] 红点跨表校验失败：\n" + redDotReport);
                RedDotConfigCompiler.WriteArtifacts(redDotCatalog);
            }

            // ── 2. 数据导出（校验开启；首包 + 热更双目标；孤儿表清理保持库与 Excel 目录一致）──
            var exporter = new ExcelExporter(new ExcelExporter.ExportConfig
            {
                OutputTarget = ExcelExporter.DatabaseOutputTarget.Both,
                OverwriteExistingTables = true,
                PruneMissingTablesOnBatch = true,
                EnableValidation = true,
            });
            List<ExcelExporter.ExportResult> results = exporter.ExportBatch(excelFiles);

            List<ExcelExporter.ExportResult> failures = results.Where(r => !r.Success).ToList();
            if (failures.Count > 0)
            {
                string detail = string.Join("\n", failures.Select(f => $"  {f.TableName}: {f.ErrorMessage}"));
                throw new InvalidOperationException($"[ConfigPipeline] {failures.Count} 张表导出失败：\n{detail}");
            }

            AssetDatabase.Refresh();

            int rowTotal = results.Sum(r => r.RowCount);
            string summary = $"CONFIG_PIPELINE_OK 表={results.Count} 行={rowTotal} 生成类={generatedCount}\n" +
                             string.Join("\n", results.Select(r => $"  {r.TableName}: {r.RowCount} 行"));
            Debug.Log($"[ConfigPipeline] {summary}");
            return summary;
        }

        /// <summary>写生成代码到目标路径（general 表无 Table 类，路径可为空）。</summary>
        private static void WriteGenerated(string path, string code)
        {
            if (string.IsNullOrEmpty(path) || string.IsNullOrEmpty(code))
                return;
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            File.WriteAllText(path, code);
        }
    }
}
