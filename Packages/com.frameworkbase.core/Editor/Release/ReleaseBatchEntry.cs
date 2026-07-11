using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace Framework.Editor.Release
{
    /// <summary>
    /// CI/发布机统一命令行入口。Editor 窗口与批处理入口组装同一组 ReleasePipeline 步骤，禁止维护两套发布逻辑。
    /// <para>
    /// 热更新入口：<c>-executeMethod Framework.Editor.Release.ReleaseBatchEntry.PublishHotUpdate</c>。
    /// 整包入口：<c>-executeMethod Framework.Editor.Release.ReleaseBatchEntry.BuildFullPackage</c>。
    /// 成功退出码为 0，参数、门禁、构建或发布任一失败退出码为 1；结论哨兵为 <c>RELEASE_RESULT exit=N</c>。
    /// </para>
    /// <para>
    /// GameCI unity-builder 等按进程退出码判定的宿主必须改用 <see cref="PublishHotUpdateForBuilder"/> /
    /// <see cref="BuildFullPackageForBuilder"/>：失败以 BuildFailedException 上抛，规避 batchmode 退出码不可靠问题。
    /// </para>
    /// </summary>
    public static class ReleaseBatchEntry
    {
        public static void PublishHotUpdate()
        {
            RunAndExit(ExecutePublishHotUpdate);
        }

        public static void BuildFullPackage()
        {
            RunAndExit(ExecuteBuildFullPackage);
        }

        /// <summary>
        /// GameCI unity-builder 等外部构建器专用热更发布入口：失败以 <see cref="UnityEditor.Build.BuildFailedException"/>
        /// 上抛让宿主可靠拿到非零结果，不调用 EditorApplication.Exit（batchmode 退出码不可靠）。
        /// </summary>
        public static void PublishHotUpdateForBuilder()
        {
            RunForBuilder(ExecutePublishHotUpdate);
        }

        /// <summary>外部构建器专用整包发布入口；失败语义同 <see cref="PublishHotUpdateForBuilder"/>。</summary>
        public static void BuildFullPackageForBuilder()
        {
            RunForBuilder(ExecuteBuildFullPackage);
        }

        private static void ExecutePublishHotUpdate()
        {
            {
                Dictionary<string, string> args = ParseArgs();
                string releaseId = Get(args, "releaseId", Guid.NewGuid().ToString("N"));
                string projectRoot = Directory.GetParent(Application.dataPath)?.FullName ?? Directory.GetCurrentDirectory();
                EnsureCleanGit(projectRoot, HasFlag(args, "allowDirtyRelease"));

                BuildTarget target = ResolveBuildTarget(args);
                SwitchBuildTarget(target);
                string staging = Get(args, "serverDataDir",
                    Path.Combine(projectRoot, "Artifacts", "ReleaseStaging", releaseId));
                PrepareLocalStaging(projectRoot, staging);

                var context = new ReleaseContext
                {
                    ReleaseId = releaseId,
                    EnvironmentName = Require(args, "releaseEnv"),
                    UploadRootOverride = Get(args, "uploadRoot", string.Empty),
                    BuildTarget = target,
                    AppVersion = Require(args, "appVersion"),
                    ResourceVersion = RequireInt(args, "resourceVersion"),
                    CodeVersion = RequireInt(args, "codeVersion"),
                    PublishResource = RequireBool(args, "publishResource"),
                    PublishCode = RequireBool(args, "publishCode"),
                    ForceUpdate = GetBool(args, "forceUpdate", false),
                    MinCompatibleVersion = Get(args, "minCompatibleVersion", string.Empty),
                    Description = Get(args, "description", string.Empty),
                    GrayPercent = GetInt(args, "grayPercent", 100),
                    UpdateUrl = Get(args, "updateUrl", string.Empty),
                    ServerDataDir = staging,
                    VersionOutputDir = string.Empty,
                    BundleOutputDir = string.Empty,
                    Log = message => UnityEngine.Debug.Log("[ReleaseBatch] " + message),
                };
                if (!context.PublishResource && !context.PublishCode && !context.ForceUpdate)
                    throw new ArgumentException("热更新发布至少包含资源、代码或整包强更清单中的一项。");

                ReleasePipelineResult result = ReleasePipeline.Run(new IReleaseStep[]
                {
                    new HotUpdateReleaseSteps.ValidateReleaseEnvironment(),
                    new HotUpdateReleaseSteps.BuildAddressables(),
                    new HotUpdateReleaseSteps.CompileAndCopyHotUpdateDlls(),
                    new HotUpdateReleaseSteps.GenerateManifest(),
                    new HotUpdateReleaseSteps.WriteAndSignManifest(),
                    new ReleasePublishingSteps.WriteReleaseLedger(),
                    new ReleasePublishingSteps.AtomicPublishArtifacts(),
                }, context);
                ThrowIfFailed(result);
            }
        }

        private static void ExecuteBuildFullPackage()
        {
            {
                Dictionary<string, string> args = ParseArgs();
                string releaseId = Get(args, "releaseId", Guid.NewGuid().ToString("N"));
                string projectRoot = Directory.GetParent(Application.dataPath)?.FullName ?? Directory.GetCurrentDirectory();
                EnsureCleanGit(projectRoot, HasFlag(args, "allowDirtyRelease"));

                BuildTarget target = ResolveBuildTarget(args);
                SwitchBuildTarget(target);
                string staging = Get(args, "serverDataDir",
                    Path.Combine(projectRoot, "Artifacts", "ReleaseStaging", releaseId));
                PrepareLocalStaging(projectRoot, staging);

                var context = new FullPackageReleaseContext
                {
                    ReleaseId = releaseId,
                    EnvironmentName = Require(args, "releaseEnv"),
                    UploadRootOverride = Get(args, "uploadRoot", string.Empty),
                    BuildTarget = target,
                    AppVersion = Require(args, "appVersion"),
                    MinCompatibleVersion = Get(args, "minCompatibleVersion", Require(args, "appVersion")),
                    UpdateUrl = Get(args, "updateUrl", string.Empty),
                    ForceUpdate = true,
                    ResourceVersion = 1,
                    CodeVersion = 1,
                    PublishResource = true,
                    PublishCode = true,
                    ServerDataDir = staging,
                    ExcelFolder = Get(args, "excelFolder", "Assets/RefData_Excel"),
                    StreamingDbPath = Get(args, "streamingDbPath", "Assets/StreamingAssets/RefData/config.db"),
                    OverwriteTables = GetBool(args, "overwriteTables", true),
                    EnableValidation = GetBool(args, "enableValidation", true),
                    RequireHealthyTelemetry = GetBool(args, "requireHealthyTelemetry", true),
                    BuildOutputPath = Require(args, "buildOutput"),
                    Log = message => UnityEngine.Debug.Log("[ReleaseBatch] " + message),
                };

                var steps = new List<IReleaseStep>
                {
                    new HotUpdateReleaseSteps.ValidateReleaseEnvironment(),
                    new FullPackageReleaseSteps.ValidateFullPackageInputs(requireBuildOutputPath: true),
                    new FullPackageReleaseSteps.WriteFullPackageVersion(writeVersionJson: true),
                    new FullPackageReleaseSteps.PrepareFullPackageAddressables(),
                    new FullPackageReleaseSteps.ExportRefData(),
                    new FullPackageReleaseSteps.SyncStreamingAssetsAndVerify(),
                    new FullPackageReleaseSteps.BuildPlayer(),
                    new ReleasePublishingSteps.StageFullPackageArtifact(),
                    new ReleasePublishingSteps.WriteReleaseLedger(),
                    new ReleasePublishingSteps.AtomicPublishArtifacts(),
                    new FullPackageReleaseSteps.SwitchBackHotUpdateRemote(),
                };
                ThrowIfFailed(ReleasePipeline.Run(steps, context));
            }
        }

        private static void RunAndExit(Action action)
        {
            try
            {
                action();
                UnityEngine.Debug.Log("[ReleaseBatch] RELEASE_RESULT exit=0");
                EditorApplication.Exit(0);
            }
            catch (Exception ex)
            {
                UnityEngine.Debug.LogError($"[ReleaseBatch] 发布失败：{ex}\n[ReleaseBatch] RELEASE_RESULT exit=1");
                EditorApplication.Exit(1);
            }
        }

        /// <summary>
        /// 外部构建器收口：成功打印哨兵，失败转 BuildFailedException 上抛，由宿主构建器判定非零。
        /// </summary>
        private static void RunForBuilder(Action action)
        {
            try
            {
                action();
                UnityEngine.Debug.Log("[ReleaseBatch] RELEASE_RESULT exit=0");
            }
            catch (Exception ex)
            {
                UnityEngine.Debug.LogError($"[ReleaseBatch] 发布失败：{ex}\n[ReleaseBatch] RELEASE_RESULT exit=1");
                throw new UnityEditor.Build.BuildFailedException(ex.Message);
            }
        }

        private static void ThrowIfFailed(ReleasePipelineResult result)
        {
            if (result == null || !result.Success)
                throw new InvalidOperationException($"发布步骤 {result?.FailedStep ?? "unknown"} 失败：{result?.Error ?? "unknown"}");
        }

        private static Dictionary<string, string> ParseArgs()
        {
            string[] commandLine = Environment.GetCommandLineArgs();
            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < commandLine.Length; i++)
            {
                string arg = commandLine[i];
                if (!arg.StartsWith("-", StringComparison.Ordinal)) continue;
                string key = arg.TrimStart('-');
                string value = "true";
                if (i + 1 < commandLine.Length && !commandLine[i + 1].StartsWith("-", StringComparison.Ordinal))
                    value = commandLine[++i];
                result[key] = value;
            }
            return result;
        }

        private static string Require(Dictionary<string, string> args, string key)
        {
            if (!args.TryGetValue(key, out string value) || string.IsNullOrWhiteSpace(value))
                throw new ArgumentException($"缺少命令行参数 -{key}。");
            return value;
        }

        private static string Get(Dictionary<string, string> args, string key, string fallback) =>
            args.TryGetValue(key, out string value) ? value : fallback;

        private static int RequireInt(Dictionary<string, string> args, string key)
        {
            string value = Require(args, key);
            if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed))
                throw new ArgumentException($"参数 -{key} 不是有效整数：{value}");
            return parsed;
        }

        private static int GetInt(Dictionary<string, string> args, string key, int fallback) =>
            args.TryGetValue(key, out string value) && int.TryParse(value, out int parsed) ? parsed : fallback;

        private static bool RequireBool(Dictionary<string, string> args, string key)
        {
            string value = Require(args, key);
            if (!bool.TryParse(value, out bool parsed))
                throw new ArgumentException($"参数 -{key} 不是 true/false：{value}");
            return parsed;
        }

        private static bool GetBool(Dictionary<string, string> args, string key, bool fallback) =>
            args.TryGetValue(key, out string value) && bool.TryParse(value, out bool parsed) ? parsed : fallback;

        private static bool HasFlag(Dictionary<string, string> args, string key) =>
            args.TryGetValue(key, out string value) && (!bool.TryParse(value, out bool parsed) || parsed);

        /// <summary>
        /// 解析发布目标平台。-buildTarget 是 Unity 启动器的<b>保留参数</b>，会先于托管代码被
        /// Unity 自身解析：值不在 Unity 官方平台名单（Win64/Android/iOS/Linux64/OSXUniversal/WebGL…）
        /// 内时编辑器直接崩溃。因此本方法只接受 Unity 官方命令行平台名，禁止自造别名；
        /// 参数缺省时使用当前激活目标（Unity 已按同一参数完成平台切换，与 GameCI targetPlatform 天然一致）。
        /// </summary>
        private static BuildTarget ResolveBuildTarget(Dictionary<string, string> args)
        {
            if (!args.TryGetValue("buildTarget", out string value) || string.IsNullOrWhiteSpace(value))
                return EditorUserBuildSettings.activeBuildTarget;

            switch (value.Trim().ToLowerInvariant())
            {
                case "win": return BuildTarget.StandaloneWindows;
                case "win64": return BuildTarget.StandaloneWindows64;
                case "linux64": return BuildTarget.StandaloneLinux64;
                case "osxuniversal": return BuildTarget.StandaloneOSX;
                case "android": return BuildTarget.Android;
                case "ios": return BuildTarget.iOS;
                case "webgl": return BuildTarget.WebGL;
                default:
                    throw new ArgumentException(
                        $"不支持的 buildTarget：{value}。必须使用 Unity 官方命令行平台名" +
                        "（Win64/Win/Linux64/OSXUniversal/Android/iOS/WebGL），自造别名会让 Unity 启动器直接崩溃。");
            }
        }

        private static void SwitchBuildTarget(BuildTarget target)
        {
            BuildTargetGroup group = BuildPipeline.GetBuildTargetGroup(target);
            if (group == BuildTargetGroup.Unknown || !EditorUserBuildSettings.SwitchActiveBuildTarget(group, target))
                throw new InvalidOperationException($"切换 BuildTarget 失败：{target}");
        }

        private static void PrepareLocalStaging(string projectRoot, string staging)
        {
            string root = Path.GetFullPath(projectRoot)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
            string full = Path.GetFullPath(staging)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException($"本地发布 staging 必须位于工程目录内：{full}");
            if (Directory.Exists(full)) Directory.Delete(full, true);
            Directory.CreateDirectory(full);
        }

        private static void EnsureCleanGit(string projectRoot, bool allowDirty)
        {
            if (allowDirty) return;
            var start = new ProcessStartInfo
            {
                FileName = "git",
                Arguments = "status --porcelain",
                WorkingDirectory = projectRoot,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            using (Process process = Process.Start(start))
            {
                string output = process.StandardOutput.ReadToEnd();
                string error = process.StandardError.ReadToEnd();
                process.WaitForExit(10000);
                if (process.ExitCode != 0)
                    throw new InvalidOperationException($"读取 Git 状态失败：{error}");
                if (!string.IsNullOrWhiteSpace(output))
                    throw new InvalidOperationException("工作区存在未提交修改；正式发布必须基于干净 Git Commit。调试时可显式传 -allowDirtyRelease。");
            }
        }
    }
}
