using System;
using System.IO;
using System.Security.Cryptography;
using Framework.HotUpdate;
using HybridCLR.Editor.Commands;
using UnityEditor;
using UnityEditor.AddressableAssets;
using UnityEditor.AddressableAssets.Settings;
using UnityEditor.AddressableAssets.Build;

namespace Framework.Editor.Release
{
    /// <summary>
    /// 热更发布流水线的标准步骤集。入口（HotUpdatePublisher 窗口 / ReleaseBatchEntry CI 命令行）组装
    /// 这些步骤调用 <see cref="ReleasePipeline.Run"/>，不再把流程内联在窗口方法里。
    /// </summary>
    public static class HotUpdateReleaseSteps
    {
        /// <summary>发布前环境校验：加载当前活动 ReleaseProfile 并跑准入门禁，结果填入上下文。</summary>
        public class ValidateReleaseEnvironment : IReleaseStep
        {
            public string Name => "ValidateReleaseEnvironment";
            public string Description => "校验发布环境（prod 强制 HTTPS、要求签名须有私钥），不达标直接中止";

            public void Execute(ReleaseContext ctx)
            {
                if (!Guid.TryParse(ctx.ReleaseId, out _))
                    throw new Exception($"ReleaseId 必须是有效 GUID：{ctx.ReleaseId}");

                string environment = string.IsNullOrWhiteSpace(ctx.EnvironmentName)
                    ? ReleaseProfileStore.ActiveEnv
                    : ctx.EnvironmentName.Trim();
                ReleaseProfile profile = ReleaseProfileStore.TryLoad(environment, out string loadError);
                if (profile == null)
                    throw new Exception($"发布环境 {environment} 加载失败：{loadError}");

                if (!string.IsNullOrWhiteSpace(ctx.UploadRootOverride))
                    profile.UploadRoot = Path.GetFullPath(ctx.UploadRootOverride);

                if (!ReleaseProfileGate.Validate(
                        profile,
                        UpdateManifestSigner.HasUsablePrivateKey,
                        out string report))
                {
                    ctx.Log(report);
                    throw new Exception("发布环境校验未通过：\n" + report);
                }

                ctx.EnvironmentName = environment;
                ctx.Profile = profile;
                if (ctx.BuildTarget == BuildTarget.NoTarget)
                    ctx.BuildTarget = EditorUserBuildSettings.activeBuildTarget;

                // 产物仓库布局事实源：{env}/{platform}/{channel} 作用域 + releases/{app}/{releaseId} 不可变目录。
                // Channel 在此一次取定，清单字段与产物路径共用，禁止后续步骤各自读 AppConfig 造成漂移。
                string channel = Framework.Core.AppConfig.Load()?.AppChannel;
                ctx.Channel = string.IsNullOrWhiteSpace(channel) ? "default" : channel.Trim();
                if (!UpdateSecurity.IsSafeManifestIdentifier(ctx.Channel))
                    throw new Exception($"AppChannel 含非法字符，无法作为发布路径段：{ctx.Channel}");
                ctx.PublishScopeRelative = string.Join("/",
                    SanitizePathSegment(environment),
                    GetPlatformId(ctx.BuildTarget),
                    ctx.Channel);
                ctx.ReleaseDirRelative = $"releases/{SanitizePathSegment(ctx.AppVersion)}/{ctx.ReleaseId}";

                ctx.Log("[环境校验] " + report);
                ctx.Log($"[环境校验] BuildTarget={ctx.BuildTarget} UploadRoot={profile.UploadRoot}");
                ctx.Log($"[环境校验] 发布作用域={ctx.PublishScopeRelative} 版本目录={ctx.ReleaseDirRelative}");
            }
        }

        /// <summary>构建 Addressables 资源包（仅计划含资源更新时执行），并按需同步 bundle 到联调目录。</summary>
        public class BuildAddressables : IReleaseStep
        {
            public string Name => "BuildAddressables";
            public string Description => "构建 Addressables 资源包；未勾选资源更新时跳过";

            public void Execute(ReleaseContext ctx)
            {
                if (!ctx.PublishResource)
                {
                    ctx.Log("      跳过（本次不含资源更新）");
                    return;
                }

                AddressableAssetSettings settings = AddressableAssetSettingsDefaultObject.Settings;
                if (settings == null)
                    throw new Exception("未找到 Addressables Settings，请先执行 Framework/Setup Addressables");
                if (ctx.Profile == null)
                    throw new Exception("发布环境尚未通过校验，无法配置 Addressables 远程路径。");

                string profileId = settings.activeProfileId;
                var profileSettings = settings.profileSettings;
                string oldBuildPath = profileSettings.GetValueByName(profileId, AddressableAssetSettings.kRemoteBuildPath);
                string oldLoadPath = profileSettings.GetValueByName(profileId, AddressableAssetSettings.kRemoteLoadPath);
                string stagedBuildPath = Path.Combine(ctx.ServerDataDir, "addressables", "[BuildTarget]")
                    .Replace('\\', '/');
                string stagedLoadPath = ctx.Profile.BaseUrl.TrimEnd('/') + "/addressables/[BuildTarget]";

                try
                {
                    // 构建期临时覆盖远程路径，使资源产物进入本次统一 staging；finally 恢复 Profile，避免环境 URL 污染工程资产。
                    profileSettings.SetValue(profileId, AddressableAssetSettings.kRemoteBuildPath, stagedBuildPath);
                    profileSettings.SetValue(profileId, AddressableAssetSettings.kRemoteLoadPath, stagedLoadPath);
                    AddressableAssetSettings.BuildPlayerContent(out AddressablesPlayerBuildResult result);
                    if (!string.IsNullOrEmpty(result.Error))
                        throw new Exception($"Addressables Build 失败：{result.Error}");
                }
                finally
                {
                    profileSettings.SetValue(profileId, AddressableAssetSettings.kRemoteBuildPath, oldBuildPath);
                    profileSettings.SetValue(profileId, AddressableAssetSettings.kRemoteLoadPath, oldLoadPath);
                    EditorUtility.SetDirty(settings);
                    AssetDatabase.SaveAssets();
                }

                string stagedOutput = Path.Combine(
                    ctx.ServerDataDir,
                    "addressables",
                    GetBuildTargetName(ctx.BuildTarget));
                if (!Directory.Exists(stagedOutput) || Directory.GetFiles(stagedOutput, "*", SearchOption.AllDirectories).Length == 0)
                    throw new Exception($"Addressables 构建产物目录不存在或为空：{stagedOutput}");
                ctx.Log($"      Addressables Build 完成 → {stagedOutput}");

                // 旧窗口额外输出路径仅作本地联调兼容；正式发布由 AtomicPublishArtifacts 使用 Profile.UploadRoot 提交。
                if (!string.IsNullOrEmpty(ctx.BundleOutputDir))
                {
                    CopyDirectory(stagedOutput, ctx.BundleOutputDir);
                    ctx.Log($"      bundle 已同步到兼容目录 → {ctx.BundleOutputDir}");
                }
            }

            private static void CopyDirectory(string source, string destination)
            {
                foreach (string directory in Directory.GetDirectories(source, "*", SearchOption.AllDirectories))
                {
                    string relative = directory.Substring(source.Length)
                        .TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                    Directory.CreateDirectory(Path.Combine(destination, relative));
                }
                Directory.CreateDirectory(destination);
                foreach (string file in Directory.GetFiles(source, "*", SearchOption.AllDirectories))
                {
                    string relative = file.Substring(source.Length)
                        .TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                    string target = Path.Combine(destination, relative);
                    Directory.CreateDirectory(Path.GetDirectoryName(target));
                    File.Copy(file, target, true);
                }
            }
        }

        /// <summary>编译并复制热更程序集（仅计划含代码更新时执行），补丁清单写入上下文。</summary>
        public class CompileAndCopyHotUpdateDlls : IReleaseStep
        {
            public string Name => "CompileAndCopyHotUpdateDlls";
            public string Description => "HybridCLR CompileDll 并复制热更程序集到发布目录；未勾选代码更新时跳过";

            public void Execute(ReleaseContext ctx)
            {
                if (!ctx.PublishCode)
                {
                    ctx.Log("      跳过（本次不含代码更新）");
                    return;
                }

                BuildTarget target = ctx.BuildTarget == BuildTarget.NoTarget
                    ? EditorUserBuildSettings.activeBuildTarget
                    : ctx.BuildTarget;
                CompileDllCommand.CompileDll(target);
                ctx.Log("      编译完成，复制热更程序集...");

                Directory.CreateDirectory(ctx.ServerDataDir);
                if (!string.IsNullOrEmpty(ctx.VersionOutputDir))
                    Directory.CreateDirectory(ctx.VersionOutputDir);

                ctx.PatchFiles.Clear();
                foreach (string destFileName in VersionManager.HotUpdateAssemblyFileNames)
                {
                    string src = GetHotUpdateDllSrc(target, destFileName);
                    if (!File.Exists(src))
                        throw new Exception($"未找到热更程序集：{src}\n请先执行 HybridCLR/Generate/All");

                    string sha256 = ComputeSHA256(src);
                    // 补丁进入本 release 的不可变版本目录（releases/{app}/{releaseId}/payloads/…）：
                    // releaseId 隔离每次发布，sha16 子目录保留内容寻址；旧清单继续引用旧 release 目录，
                    // 发布窗口内新旧内容互不可见。
                    string relativePath = $"{ctx.ReleaseDirRelative}/payloads/{sha256.Substring(0, 16)}/{destFileName}";
                    string destLocal = Path.Combine(ctx.ServerDataDir, relativePath.Replace('/', Path.DirectorySeparatorChar));
                    Directory.CreateDirectory(Path.GetDirectoryName(destLocal));
                    if (File.Exists(destLocal) && !string.Equals(ComputeSHA256(destLocal), sha256, StringComparison.OrdinalIgnoreCase))
                        throw new IOException($"不可变补丁路径已存在但内容不同：{destLocal}");
                    File.Copy(src, destLocal, overwrite: true);

                    // URL = BaseUrl/{env}/{platform}/{channel}/releases/…，位于客户端 UpdateServerUrl
                    // （渠道根）的路径前缀之下，满足 ResolveTrustedPatchUrl 的同源同路径根契约。
                    var patch = new PatchFile
                    {
                        FileName = destFileName,
                        Url      = ctx.Profile.BaseUrl.TrimEnd('/') + "/" + ctx.PublishScopeRelative + "/" + relativePath,
                        Size     = new FileInfo(destLocal).Length,
                        SHA256   = sha256,
                        MD5      = ComputeMD5(destLocal)
                    };
                    ctx.PatchFiles.Add(patch);
                    ctx.Log($"      {patch.FileName} 大小={patch.Size}B SHA256={patch.SHA256} URL={patch.Url}");
                }
            }
        }

        /// <summary>按统一契约（ReleaseManifestWriter）生成 version.json 文本。</summary>
        public class GenerateManifest : IReleaseStep
        {
            public string Name => "GenerateManifest";
            public string Description => "按统一发布契约序列化 version.json（UpdateInfo 本体，字段不漂移）";

            public void Execute(ReleaseContext ctx)
            {
                long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                ctx.ManifestJson = ReleaseManifestWriter.ToJson(new UpdateInfo
                {
                    ManifestVersion = FrameworkRuntimeInfo.UpdateManifestVersion,
                    ManifestId = string.IsNullOrWhiteSpace(ctx.ReleaseId) ? Guid.NewGuid().ToString("D") : ctx.ReleaseId,
                    IssuedAtUnixSeconds = now,
                    ExpiresAtUnixSeconds = now + 30L * 24 * 60 * 60,
                    KeyId = string.IsNullOrWhiteSpace(ctx.Profile?.SigningKeyRef)
                        ? "development"
                        : ctx.Profile.SigningKeyRef,
                    Platform = GetPlatformId(ctx.BuildTarget == BuildTarget.NoTarget
                        ? EditorUserBuildSettings.activeBuildTarget
                        : ctx.BuildTarget),
                    Channel = ctx.Channel, // 与产物路径作用域同一事实源（环境校验步骤取定）

                    MinFrameworkVersion = FrameworkRuntimeInfo.Version,
                    AppVersion = ctx.AppVersion,
                    ResourceVersion = ctx.ResourceVersion,
                    CodeVersion = ctx.CodeVersion,
                    ForceUpdate = ctx.ForceUpdate,
                    MinCompatibleVersion = ctx.MinCompatibleVersion,
                    Description = ctx.Description ?? string.Empty,
                    PatchFiles = ctx.PatchFiles,
                    UpdateUrl = ctx.ForceUpdate ? (ctx.UpdateUrl ?? string.Empty) : string.Empty,
                    GrayPercent = ctx.GrayPercent
                });
            }
        }

        /// <summary>写出 version.json（ServerData + 可选联调目录）并按环境要求签名；强制环境签名失败即中止。</summary>
        public class WriteAndSignManifest : IReleaseStep
        {
            public string Name => "WriteAndSignManifest";
            public string Description => "写出 version.json 并生成伴生签名；环境要求签名而失败时中止发布";

            public void Execute(ReleaseContext ctx)
            {
                if (string.IsNullOrEmpty(ctx.ManifestJson))
                    throw new Exception("清单 JSON 为空（GenerateManifest 步骤未执行？）");

                Directory.CreateDirectory(ctx.ServerDataDir);
                // 渠道根别名：旧客户端与迁移期直取入口（可变，manifest-last 提交）。
                WriteOne(Path.Combine(ctx.ServerDataDir, "version.json"), ctx, required: true);

                // 不可变版本目录内的正本：回滚/晋级按 releaseId 引用，永不随后续发布改变。
                if (!string.IsNullOrEmpty(ctx.ReleaseDirRelative))
                {
                    string releaseDir = Path.Combine(
                        ctx.ServerDataDir,
                        ctx.ReleaseDirRelative.Replace('/', Path.DirectorySeparatorChar));
                    Directory.CreateDirectory(releaseDir);
                    WriteOne(Path.Combine(releaseDir, "version.json"), ctx, required: true);
                }
                ctx.Log($"      清单与签名已写入本地 staging → {ctx.ServerDataDir}");
            }

            private static void WriteOne(string manifestPath, ReleaseContext ctx, bool required)
            {
                // 清单契约：无 BOM UTF-8。签名对象是原始字节，BOM 会随签名一起"合法化"，
                // 但客户端把字节转字符串解析 KeyId 信封时 BOM 进入 JSON 解析器即拒收——
                // 属于"签名有效但解析失败"的线上事故类别（release-rehearsal 已把该契约测试化）。
                File.WriteAllText(manifestPath, ctx.ManifestJson, new System.Text.UTF8Encoding(false));
                if (!UpdateManifestSigner.SignManifestForPublish(manifestPath, ctx.Log, required))
                    throw new Exception($"清单签名失败（环境 {ctx.Profile?.Name} 要求签名），已中止发布");
            }
        }

        // ── 步骤共用工具 ──────────────────────────────────────────────────────

        /// <summary>指定构建目标的 HybridCLR 热更 DLL 生成路径。</summary>
        internal static string GetHotUpdateDllSrc(BuildTarget target, string bytesFileName)
        {
            string assemblyName = VersionManager.ToAssemblyName(bytesFileName);
            return Path.Combine(Directory.GetParent(UnityEngine.Application.dataPath).FullName,
                "HybridCLRData", "HotUpdateDlls", GetBuildTargetName(target), assemblyName + ".dll");
        }

        /// <summary>Unity 构建目标 → HybridCLRData 平台目录名。</summary>
        internal static string GetBuildTargetName(BuildTarget target)
        {
            return target switch
            {
                BuildTarget.StandaloneWindows   => "StandaloneWindows",
                BuildTarget.StandaloneWindows64 => "StandaloneWindows64",
                BuildTarget.Android             => "Android",
                BuildTarget.iOS                 => "iOS",
                _                               => target.ToString()
            };
        }

        /// <summary>计算文件 MD5（小写十六进制）。</summary>
        internal static string GetPlatformId(BuildTarget target)
        {
            switch (target)
            {
                case BuildTarget.Android: return "android";
                case BuildTarget.iOS: return "ios";
                case BuildTarget.StandaloneWindows:
                case BuildTarget.StandaloneWindows64: return "windows";
                case BuildTarget.StandaloneOSX: return "macos";
                case BuildTarget.StandaloneLinux64: return "linux";
                case BuildTarget.WebGL: return "webgl";
                default: return target.ToString().ToLowerInvariant();
            }
        }

        /// <summary>
        /// 将版本号等路径片段规整为只包含字母、数字、点、下划线和连字符的安全目录名。
        /// </summary>
        internal static string SanitizePathSegment(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return "unknown";
            var chars = value.ToCharArray();
            for (int i = 0; i < chars.Length; i++)
            {
                char c = chars[i];
                if (!(char.IsLetterOrDigit(c) || c == '.' || c == '_' || c == '-'))
                    chars[i] = '_';
            }
            return new string(chars);
        }

        internal static string ComputeSHA256(string filePath)
        {
            using var sha256 = SHA256.Create();
            using var stream = File.OpenRead(filePath);
            byte[] hash = sha256.ComputeHash(stream);
            return BitConverter.ToString(hash).Replace("-", "").ToLowerInvariant();
        }

        internal static string ComputeMD5(string filePath)
        {
            using var md5 = MD5.Create();
            using var stream = File.OpenRead(filePath);
            byte[] hash = md5.ComputeHash(stream);
            return BitConverter.ToString(hash).Replace("-", "").ToLowerInvariant();
        }
    }
}
