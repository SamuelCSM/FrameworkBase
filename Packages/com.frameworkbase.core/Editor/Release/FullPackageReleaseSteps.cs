using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Editor.ExcelTool;
using Framework.Core;
using Framework.HotUpdate;
using SQLite;
using UnityEditor;
using UnityEditor.AddressableAssets;
using UnityEditor.Build.Reporting;
using UnityEngine;

namespace Framework.Editor.Release
{
    /// <summary>
    /// 整包发布流水线上下文：在通用 <see cref="ReleaseContext"/> 之上补充整包特有的
    /// 输入参数与产出（RefData 导出配置、遥测门禁、Profile 现场、校验结果）。
    /// </summary>
    public class FullPackageReleaseContext : ReleaseContext
    {
        // ── 输入 ──────────────────────────────────────────────────────────────
        /// <summary>Excel 配表源目录（批量导出首包 RefData）。</summary>
        public string ExcelFolder;
        /// <summary>首包数据库路径（须位于 Assets/StreamingAssets 下）。</summary>
        public string StreamingDbPath;
        public bool OverwriteTables = true;
        public bool EnableValidation = true;
        /// <summary>要求最近一次启动埋点通过，否则阻断整包（可恢复失败除外）。</summary>
        public bool RequireHealthyTelemetry = true;
        /// <summary>Player 输出路径；仅编排含 BuildPlayer 步骤时必填。</summary>
        public string BuildOutputPath;

        // ── 产出（步骤回写，供窗口写报告）────────────────────────────────────
        /// <summary>流程开始时的 Addressables Profile（补偿恢复与报告用）。</summary>
        public string ProfileBefore = string.Empty;
        public string TelemetryEndReason = string.Empty;
        public bool TelemetrySuccess;
        /// <summary>各校验项结论（rule: PASS/FAIL）。</summary>
        public List<string> ValidationResults = new List<string>();

        /// <summary>登记一条校验结论并输出日志。</summary>
        public void AddValidationResult(string rule, bool passed, string detail)
        {
            string line = $"{rule}: {(passed ? "PASS" : "FAIL")} ({detail})";
            ValidationResults.Add(line);
            Log($"[Check] {line}");
        }
    }

    /// <summary>
    /// 整包发布流水线的标准步骤集。整包=客户端发布轨道：与热更（内容发布轨道）共用
    /// 环境门禁与清单契约，额外负责 StreamingAssets 内置与 BuildPlayer。
    /// </summary>
    public static class FullPackageReleaseSteps
    {
        /// <summary>首包启动依赖表（缺失阻断）。</summary>
        internal static readonly string[] BootstrapRequiredTables = { "language", "loading_tips" };

        private static string LaunchMetricPath =>
            Path.Combine(Application.persistentDataPath, "launch_metrics_last.json");

        // ─────────────────────────────────────────────────────────────────────
        /// <summary>整包输入校验：路径、Excel、IL2CPP 工具链、启动埋点门禁。</summary>
        public class ValidateFullPackageInputs : IReleaseStep
        {
            private readonly bool _requireBuildOutputPath;

            public ValidateFullPackageInputs(bool requireBuildOutputPath)
            {
                _requireBuildOutputPath = requireBuildOutputPath;
            }

            public string Name => "ValidateFullPackageInputs";
            public string Description => "校验整包输入（Excel/首包库/输出路径/IL2CPP 工具链/启动埋点门禁）";

            public void Execute(ReleaseContext context)
            {
                var ctx = (FullPackageReleaseContext)context;

                if (string.IsNullOrEmpty(ctx.AppVersion))
                    throw new Exception("AppVersion 不能为空。");

                if (string.IsNullOrEmpty(ctx.ExcelFolder) || !Directory.Exists(ctx.ExcelFolder))
                    throw new Exception($"Excel 源目录无效：{ctx.ExcelFolder}");

                if (!HasExcelFiles(ctx.ExcelFolder))
                    throw new Exception($"Excel 源目录下未找到 .xlsx：{ctx.ExcelFolder}");

                if (string.IsNullOrEmpty(ctx.StreamingDbPath))
                    throw new Exception("首包数据库路径不能为空。");

                if (!ctx.StreamingDbPath.Replace('\\', '/').StartsWith("Assets/StreamingAssets/", StringComparison.OrdinalIgnoreCase))
                    throw new Exception("首包数据库路径必须位于 Assets/StreamingAssets 下。");

                if (_requireBuildOutputPath && string.IsNullOrEmpty(ctx.BuildOutputPath))
                    throw new Exception("请先设置 Build 输出路径（用于一键整包构建）。");

                Il2CppToolchainValidator.ValidateForActiveBuildTarget();

                ValidateLaunchTelemetry(ctx);
            }

            private static bool HasExcelFiles(string excelFolder)
            {
                return Directory.GetFiles(excelFolder, "*.xlsx", SearchOption.AllDirectories)
                    .Any(f => !Path.GetFileName(f).StartsWith("~$", StringComparison.Ordinal));
            }

            private static void ValidateLaunchTelemetry(FullPackageReleaseContext ctx)
            {
                if (!File.Exists(LaunchMetricPath))
                {
                    string msg = "[Telemetry] 未找到 launch_metrics_last.json，建议先跑一次启动流程冒烟。";
                    ctx.Log(msg);
                    if (ctx.RequireHealthyTelemetry)
                        throw new Exception(msg + "\n请先启动一次客户端并完成 LaunchFlow。");
                    return;
                }

                LaunchRunMetricSnapshot metric;
                try
                {
                    metric = JsonUtility.FromJson<LaunchRunMetricSnapshot>(File.ReadAllText(LaunchMetricPath));
                }
                catch (Exception ex)
                {
                    ctx.Log($"[Telemetry] 读取启动埋点失败: {ex.Message}");
                    if (ctx.RequireHealthyTelemetry) throw;
                    return;
                }

                if (metric == null)
                {
                    const string msg = "[Telemetry] launch_metrics_last.json 解析失败。";
                    ctx.Log(msg);
                    if (ctx.RequireHealthyTelemetry)
                        throw new Exception(msg);
                    return;
                }

                long totalMs = 0;
                string failedPhase = string.Empty;
                foreach (var phase in metric.Phases)
                {
                    totalMs += phase.DurationMs;
                    if (!phase.Success && string.IsNullOrEmpty(failedPhase))
                        failedPhase = string.IsNullOrEmpty(phase.DisplayName) ? phase.Phase : phase.DisplayName;
                }

                ctx.Log($"[Telemetry] 最近启动: success={metric.Success}, reason={metric.EndReason}, total={totalMs}ms");
                if (!string.IsNullOrEmpty(failedPhase))
                    ctx.Log($"[Telemetry] 失败阶段: {failedPhase}");
                ctx.TelemetryEndReason = metric.EndReason ?? string.Empty;
                ctx.TelemetrySuccess = metric.Success;

                if (ctx.RequireHealthyTelemetry && !metric.Success)
                {
                    if (IsRecoverableFailure(metric.EndReason, failedPhase))
                    {
                        ctx.Log("[Telemetry] 最近一次失败属于旧整包缺热更程序集导致的可恢复失败，" +
                                "本次将重新同步完整热更程序集后继续构建。");
                        return;
                    }

                    throw new Exception(
                        "[Telemetry] 最近一次启动埋点为失败，已阻断整包构建。\n" +
                        $"reason={metric.EndReason}, failed_phase={failedPhase}");
                }
            }

            /// <summary>最近一次启动失败是否可通过重新整包自愈。</summary>
            private static bool IsRecoverableFailure(string endReason, string failedPhase)
            {
                if (string.Equals(endReason, TelemetryErrorCodes.Launch.HotUpdateAssemblyLoadFailed, StringComparison.Ordinal))
                    return true;

                return string.Equals(failedPhase, "step08_hotupdate_assembly_load", StringComparison.OrdinalIgnoreCase);
            }

            [Serializable]
            private class LaunchPhaseMetricSnapshot
            {
                // 字段由 JsonUtility.FromJson 反射赋值，编译器无法静态识别，故局部关闭 CS0649 误报。
#pragma warning disable CS0649
                public string Phase;
                public string DisplayName;
                public bool Success;
                public long DurationMs;
#pragma warning restore CS0649
            }

            [Serializable]
            private class LaunchRunMetricSnapshot
            {
#pragma warning disable CS0649
                public bool Success;
                public string EndReason;
#pragma warning restore CS0649
                public List<LaunchPhaseMetricSnapshot> Phases = new List<LaunchPhaseMetricSnapshot>();
            }
        }

        // ─────────────────────────────────────────────────────────────────────
        /// <summary>写整包版本：PlayerSettings.bundleVersion + version.json（统一契约、按环境签名）+ StreamingAssets 出厂副本。</summary>
        public class WriteFullPackageVersion : IReleaseStep
        {
            private readonly bool _writeVersionJson;

            public WriteFullPackageVersion(bool writeVersionJson)
            {
                _writeVersionJson = writeVersionJson;
            }

            public string Name => "WriteFullPackageVersion";
            public string Description => "写 PlayerSettings 版本号与整包 version.json（ForceUpdate=true，Res/Code 归 1）";

            public void Execute(ReleaseContext context)
            {
                var ctx = (FullPackageReleaseContext)context;

                PlayerSettings.bundleVersion = ctx.AppVersion;
                ctx.Log($"完成：PlayerSettings.bundleVersion = {ctx.AppVersion}");

                if (!_writeVersionJson)
                {
                    ctx.Log("跳过：未写入 version.json（按当前开关设置）");
                    return;
                }

                long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                var appConfig = AppConfig.Load();
                string json = ReleaseManifestWriter.ToJson(new UpdateInfo
                {
                    ManifestVersion = FrameworkRuntimeInfo.UpdateManifestVersion,
                    ManifestId = string.IsNullOrWhiteSpace(ctx.ReleaseId) ? Guid.NewGuid().ToString("D") : ctx.ReleaseId,
                    IssuedAtUnixSeconds = now,
                    ExpiresAtUnixSeconds = now + 30L * 24 * 60 * 60,
                    KeyId = string.IsNullOrWhiteSpace(ctx.Profile?.SigningKeyRef)
                        ? "development"
                        : ctx.Profile.SigningKeyRef,
                    Platform = HotUpdateReleaseSteps.GetPlatformId(ctx.BuildTarget == BuildTarget.NoTarget
                        ? EditorUserBuildSettings.activeBuildTarget
                        : ctx.BuildTarget),
                    Channel = string.IsNullOrWhiteSpace(appConfig?.AppChannel) ? "default" : appConfig.AppChannel,
                    MinFrameworkVersion = FrameworkRuntimeInfo.Version,
                    AppVersion = ctx.AppVersion,
                    ResourceVersion = 1,
                    CodeVersion = 1,
                    ForceUpdate = true,
                    MinCompatibleVersion = string.IsNullOrEmpty(ctx.MinCompatibleVersion) ? ctx.AppVersion : ctx.MinCompatibleVersion,
                    Description = string.Empty,
                    PatchFiles = new List<PatchFile>(),
                    UpdateUrl = ctx.UpdateUrl ?? string.Empty,
                    GrayPercent = 0
                });

                // ServerData 侧清单会被部署到更新服务器，签名伴生下发；
                // StreamingAssets 侧仅作为出厂版本随包内置，客户端不对其验签，无需签名。
                Directory.CreateDirectory(ctx.ServerDataDir);
                string serverDataManifest = Path.Combine(ctx.ServerDataDir, "version.json");
                File.WriteAllText(serverDataManifest, json, System.Text.Encoding.UTF8);
                if (!UpdateManifestSigner.SignManifestForPublish(serverDataManifest, ctx.Log, required: true))
                    throw new Exception($"清单签名失败（环境 {ctx.Profile?.Name} 要求签名），已中止发布");

                string streamingDir = Path.Combine(Application.dataPath, "StreamingAssets");
                Directory.CreateDirectory(streamingDir);
                File.WriteAllText(Path.Combine(streamingDir, "version.json"), json, System.Text.Encoding.UTF8);

                AssetDatabase.Refresh();
                ctx.Log($"完成：整包 version.json 已写入（App={ctx.AppVersion}, ForceUpdate=true）");
            }
        }

        // ─────────────────────────────────────────────────────────────────────
        /// <summary>
        /// 准备整包资源：切 FullPackageLocal 并 Build Addressables。
        /// 可补偿步骤：流水线后续失败时自动切回执行前的 Profile，避免发布环境残留在 FullPackageLocal。
        /// </summary>
        public class PrepareFullPackageAddressables : ICompensableStep
        {
            public string Name => "PrepareFullPackageAddressables";
            public string Description => "切换 FullPackageLocal 并构建 Addressables（整包内置全部 remote 资源）；失败自动切回原 Profile";

            public void Execute(ReleaseContext context)
            {
                var ctx = (FullPackageReleaseContext)context;
                ctx.ProfileBefore = GetActiveProfileName();
                ctx.Log($"当前 Profile={ctx.ProfileBefore}");

                AddressablesSetup.PrepareFullPackage();
                ctx.Log("完成：Prepare Full Package");
            }

            public void Compensate(ReleaseContext context)
            {
                var ctx = (FullPackageReleaseContext)context;
                if (string.IsNullOrEmpty(ctx.ProfileBefore))
                    return;

                string current = GetActiveProfileName();
                if (string.Equals(current, ctx.ProfileBefore, StringComparison.Ordinal))
                    return;

                if (string.Equals(ctx.ProfileBefore, "HotUpdateRemote", StringComparison.Ordinal))
                    AddressablesSetup.SwitchToHotUpdateRemote();
                else if (string.Equals(ctx.ProfileBefore, "FullPackageLocal", StringComparison.Ordinal))
                    AddressablesSetup.SwitchToFullPackageLocal();

                ctx.Log($"已回滚 Profile: {current} -> {ctx.ProfileBefore}");
            }

            internal static string GetActiveProfileName()
            {
                var settings = AddressableAssetSettingsDefaultObject.Settings;
                if (settings == null || settings.profileSettings == null)
                    return string.Empty;

                string activeId = settings.activeProfileId;
                return settings.profileSettings.GetProfileName(activeId) ?? string.Empty;
            }
        }

        // ─────────────────────────────────────────────────────────────────────
        /// <summary>批量导出首包 RefData（含首包依赖表补导与关键表校验）。</summary>
        public class ExportRefData : IReleaseStep
        {
            public string Name => "ExportRefData";
            public string Description => "按 Excel 目录批量导出首包 RefData/config.db，并补导/校验首包启动依赖表";

            public void Execute(ReleaseContext context)
            {
                var ctx = (FullPackageReleaseContext)context;

                if (string.IsNullOrEmpty(ctx.ExcelFolder) || !Directory.Exists(ctx.ExcelFolder))
                    throw new Exception($"Excel 文件夹无效：{ctx.ExcelFolder}");

                var excelFiles = Directory.GetFiles(ctx.ExcelFolder, "*.xlsx", SearchOption.AllDirectories)
                    .Where(f => !Path.GetFileName(f).StartsWith("~$", StringComparison.Ordinal))
                    .ToList();

                if (excelFiles.Count == 0)
                    throw new Exception($"目录中未找到 xlsx：{ctx.ExcelFolder}");

                var config = new ExcelExporter.ExportConfig
                {
                    OutputDbPath = ctx.StreamingDbPath,
                    AddressableBytesOutputPath = "Assets/ResourcesOut/RefData/config.db.bytes",
                    OutputTarget = ExcelExporter.DatabaseOutputTarget.StreamingAssetsOnly,
                    OverwriteExistingTables = ctx.OverwriteTables,
                    PruneMissingTablesOnBatch = true,
                    EnableValidation = ctx.EnableValidation,
                    VerboseLogging = false
                };

                var exporter = new ExcelExporter(config);
                var results = exporter.ExportBatch(excelFiles);

                int success = results.Count(r => r.Success);
                int fail = results.Count(r => !r.Success);
                if (fail > 0)
                {
                    string errors = string.Join("\n\n", results.Where(r => !r.Success).Select(r => $"{r.TableName}: {r.ErrorMessage}"));
                    throw new Exception($"RefData 导出失败 {fail} 项：\n{errors}");
                }

                var exportedTables = results.Where(r => r.Success).Select(r => r.TableName).ToList();
                ctx.Log($"批量导出 {success} 张表：{string.Join(", ", exportedTables)}");

                EnsureBootstrapConfigTables(ctx, exporter, excelFiles);

                string dbPath = ToAbsoluteProjectPath(ctx.StreamingDbPath);
                ValidateRequiredConfigTables(ctx, dbPath);

                ctx.Log($"完成：RefData 已写入 {ctx.StreamingDbPath}");
            }

            /// <summary>首包启动依赖表缺失时，从「首包配表」工作簿按表名显式补导（避免仅导出第一个 sheet）。</summary>
            private static void EnsureBootstrapConfigTables(
                FullPackageReleaseContext ctx, ExcelExporter exporter, List<string> excelFiles)
            {
                string dbPath = ToAbsoluteProjectPath(ctx.StreamingDbPath);
                var missing = GetMissingRequiredTables(dbPath, BootstrapRequiredTables);
                if (missing.Count == 0)
                    return;

                string bootstrapPath = excelFiles.FirstOrDefault(IsBootstrapWorkbookPath);
                if (string.IsNullOrEmpty(bootstrapPath))
                {
                    throw new Exception(
                        $"首包 config.db 缺少表：{string.Join(", ", missing)}。\n" +
                        $"请在 Excel 目录放置含 language、loading_tips 工作表的「首包配表.xlsx」。");
                }

                ctx.Log($"缺少首包表 {string.Join(", ", missing)}，从 {Path.GetFileName(bootstrapPath)} 按工作表名补导…");

                foreach (string table in missing)
                {
                    var result = exporter.ExportExcel(bootstrapPath, table);
                    if (!result.Success)
                        throw new Exception($"补导首包表「{table}」失败：{result.ErrorMessage}");

                    ctx.Log($"补导成功：{table}（{result.RowCount} 行）");
                }
            }

            private static bool IsBootstrapWorkbookPath(string path)
            {
                string name = Path.GetFileNameWithoutExtension(path) ?? string.Empty;
                return name.Contains("首包", StringComparison.Ordinal) && name.Contains("配表", StringComparison.Ordinal);
            }
        }

        // ─────────────────────────────────────────────────────────────────────
        /// <summary>同步热更程序集 + HybridCLR AOT + version.json 到 StreamingAssets，并校验关键产物与 UI 地址映射。</summary>
        public class SyncStreamingAssetsAndVerify : IReleaseStep
        {
            public string Name => "SyncStreamingAssetsAndVerify";
            public string Description => "同步热更程序集/AOT 元数据/version.json 到 StreamingAssets 并校验关键产物与 UI 地址映射";

            public void Execute(ReleaseContext context)
            {
                var ctx = (FullPackageReleaseContext)context;

                HotUpdatePublisher.SyncToStreamingAssetsForBuild(showDialog: false);
                ctx.Log("完成：热更程序集 / HybridCLR AOT / version.json 已同步到 StreamingAssets");

                VerifyRequiredOutputs(ctx);

                string localDbPath = ToAbsoluteProjectPath(ctx.StreamingDbPath);
                ValidateRequiredConfigTables(ctx, localDbPath);
                ValidateUiAddressMappings(ctx, localDbPath);
            }

            private static void VerifyRequiredOutputs(FullPackageReleaseContext ctx)
            {
                string streamingDir = Path.Combine(Application.dataPath, "StreamingAssets");
                string localDbPath = ToAbsoluteProjectPath(ctx.StreamingDbPath);

                var missing = new List<string>();

                foreach (string file in new[]
                         {
                             localDbPath,
                             Path.Combine(streamingDir, "version.json"),
                             Path.Combine(ctx.ServerDataDir, "version.json")
                         })
                {
                    if (!File.Exists(file))
                        missing.Add(file);
                }

                foreach (string bytesFileName in VersionManager.HotUpdateAssemblyFileNames)
                {
                    string file = Path.Combine(streamingDir, bytesFileName);
                    if (!File.Exists(file))
                        missing.Add(file);
                }

                foreach (string file in HybridCLRStreamingAssetsSync.GetRequiredStreamingAssetsMetadataPaths())
                {
                    if (!File.Exists(file))
                        missing.Add(file);
                }

                if (missing.Count > 0)
                    throw new Exception("关键产物缺失：\n" + string.Join("\n", missing));
            }

            private static void ValidateUiAddressMappings(FullPackageReleaseContext ctx, string dbPath)
            {
                var settings = AddressableAssetSettingsDefaultObject.Settings;
                if (settings == null)
                    throw new Exception("Addressables Settings 不存在，无法校验 UI 地址映射。");

                var addressSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var group in settings.groups)
                {
                    if (group == null) continue;
                    foreach (var entry in group.entries)
                    {
                        if (!string.IsNullOrEmpty(entry.address))
                            addressSet.Add(entry.address);
                    }
                }

                var missingRows = new List<UiWndResAddressRow>();
                using (var db = new SQLiteConnection(dbPath))
                {
                    int tableExists = db.ExecuteScalar<int>(
                        "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='ui_wnd_res'");

                    if (tableExists == 0)
                    {
                        ctx.AddValidationResult("ui_address_mapping", true, "skip(ui_wnd_res missing)");
                        return;
                    }

                    var rows = db.Query<UiWndResAddressRow>("SELECT Id, Address, Desc FROM ui_wnd_res");
                    foreach (var row in rows)
                    {
                        if (string.IsNullOrEmpty(row.Address)) continue;
                        if (!addressSet.Contains(row.Address))
                            missingRows.Add(row);
                    }
                }

                if (missingRows.Count > 0)
                {
                    string distinct = string.Join(
                        "\n",
                        missingRows
                            .GroupBy(row => row.Address, StringComparer.OrdinalIgnoreCase)
                            .Select(group => FormatMissingUiAddressRow(group.First())));
                    ctx.AddValidationResult("ui_address_mapping", false, "missing");
                    throw new Exception(
                        "UI 配表引用了不存在的 Addressables 资源：\n" +
                        distinct +
                        "\n\n造成原因：config.db 的 ui_wnd_res 表仍保留这些 UI 地址，但对应 Prefab 已删除或没有注册到 Addressables。\n" +
                        $"配置来源：{ctx.ExcelFolder}/通用UI资源表.xlsx -> ui_wnd_res。\n\n" +
                        "处理方式：\n" +
                        "1) 如果这是已删除的测试资源，请从通用UI资源表.xlsx 的 ui_wnd_res 工作表删除对应行；\n" +
                        "2) 如果这是正式资源，请恢复 Prefab 到 Assets/ResourcesOut 下并重新同步 Addressables；\n" +
                        "3) 关闭 Excel 后重新点击「一键整包构建」，让 RefData/config.db 重新导出。");
                }

                ctx.AddValidationResult("ui_address_mapping", true, "all addresses resolved");
            }

            private static string FormatMissingUiAddressRow(UiWndResAddressRow row)
            {
                string desc = string.IsNullOrEmpty(row.Desc) ? string.Empty : $"，Desc={row.Desc}";
                return $"Id={row.Id}，Address={row.Address}{desc}";
            }

            /// <summary>ui_wnd_res 表中用于校验 Addressables 映射的最小字段集合。</summary>
            private class UiWndResAddressRow
            {
                public int Id { get; set; }
                public string Address { get; set; }
                public string Desc { get; set; }
            }
        }

        // ─────────────────────────────────────────────────────────────────────
        /// <summary>BuildPlayer：按 Build Settings 勾选场景构建整包。</summary>
        public class BuildPlayer : IReleaseStep
        {
            public string Name => "BuildPlayer";
            public string Description => "BuildPipeline.BuildPlayer 构建整包（Build Settings 勾选场景，当前 ActiveBuildTarget）";

            public void Execute(ReleaseContext context)
            {
                var ctx = (FullPackageReleaseContext)context;

                string[] scenes = EditorBuildSettings.scenes
                    .Where(s => s.enabled)
                    .Select(s => s.path)
                    .ToArray();
                if (scenes.Length == 0)
                    throw new Exception("Build Settings 中没有启用的场景，无法构建整包。");

                var options = new BuildPlayerOptions
                {
                    scenes = scenes,
                    locationPathName = ctx.BuildOutputPath,
                    target = EditorUserBuildSettings.activeBuildTarget,
                    options = BuildOptions.None
                };

                ctx.Log($"开始 BuildPlayer -> {ctx.BuildOutputPath}");
                BuildReport report = BuildPipeline.BuildPlayer(options);
                if (report.summary.result != BuildResult.Succeeded)
                    throw new Exception($"BuildPlayer 失败：{report.summary.result}");

                ctx.Log($"完成：BuildPlayer 成功，输出：{ctx.BuildOutputPath}");
            }
        }

        // ─────────────────────────────────────────────────────────────────────
        /// <summary>成功路径收尾：切回 HotUpdateRemote（编排按需加入）。</summary>
        public class SwitchBackHotUpdateRemote : IReleaseStep
        {
            public string Name => "SwitchBackHotUpdateRemote";
            public string Description => "整包完成后切回 HotUpdateRemote，恢复日常热更 Profile";

            public void Execute(ReleaseContext context)
            {
                AddressablesSetup.SwitchToHotUpdateRemote();
                context.Log("完成：已切回 HotUpdateRemote");
            }
        }

        // ── 步骤共用工具 ──────────────────────────────────────────────────────

        internal static string ToAbsoluteProjectPath(string projectPath)
        {
            if (Path.IsPathRooted(projectPath))
                return projectPath;

            string normalized = projectPath.Replace('\\', '/');
            if (normalized.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase))
                return Path.Combine(Directory.GetParent(Application.dataPath).FullName, normalized);

            return Path.GetFullPath(projectPath);
        }

        internal static List<string> GetMissingRequiredTables(string dbPath, string[] requiredTables)
        {
            var missing = new List<string>();
            using (var db = new SQLiteConnection(dbPath))
            {
                foreach (string table in requiredTables)
                {
                    int count = db.ExecuteScalar<int>(
                        "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name=?",
                        table);
                    if (count == 0)
                        missing.Add(table);
                }
            }

            return missing;
        }

        internal static void ValidateRequiredConfigTables(FullPackageReleaseContext ctx, string dbPath)
        {
            var missing = GetMissingRequiredTables(dbPath, BootstrapRequiredTables);

            if (missing.Count > 0)
            {
                ctx.AddValidationResult("critical_tables", false, string.Join(",", missing));
                throw new Exception(
                    "首包 config.db 缺少关键表：\n" + string.Join("\n", missing) +
                    "\n\n请确认：\n" +
                    "1) 首包配表.xlsx 含 loading_tips 工作表且已保存；\n" +
                    "2) 关闭 Excel 后重新点「一键整包构建」；\n" +
                    "3) 查看 Console 是否有 [ExcelReader] 跳过工作表 或 补导失败 日志。");
            }

            ctx.AddValidationResult("critical_tables", true, "language,loading_tips");
        }
    }
}
