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
    /// 热更发布流水线的标准步骤集。入口（HotUpdatePublisher 窗口 / 未来的 CI 命令行）组装
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
                if (!ReleaseProfileStore.TryResolveActive(out ReleaseProfile profile, out string report))
                {
                    ctx.Log(report);
                    throw new Exception("发布环境校验未通过：\n" + report);
                }

                ctx.Profile = profile;
                ctx.Log("[环境校验] " + report);
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

                var settings = AddressableAssetSettingsDefaultObject.Settings;
                if (settings == null)
                    throw new Exception("未找到 Addressables Settings，请先执行 Framework/Setup Addressables");

                AddressableAssetSettings.BuildPlayerContent(out AddressablesPlayerBuildResult result);
                if (!string.IsNullOrEmpty(result.Error))
                    throw new Exception($"Addressables Build 失败：{result.Error}");

                ctx.Log("      Addressables Build 完成");

                if (string.IsNullOrEmpty(ctx.BundleOutputDir))
                    return;

                string srcDir = Path.Combine(
                    Directory.GetParent(UnityEngine.Application.dataPath).FullName,
                    "ServerData", GetBuildTargetName(EditorUserBuildSettings.activeBuildTarget));
                if (!Directory.Exists(srcDir))
                    throw new Exception($"ServerData 源目录不存在：{srcDir}");

                Directory.CreateDirectory(ctx.BundleOutputDir);
                foreach (string file in Directory.GetFiles(srcDir))
                    File.Copy(file, Path.Combine(ctx.BundleOutputDir, Path.GetFileName(file)), overwrite: true);

                ctx.Log($"      bundle 已同步 → {ctx.BundleOutputDir}");
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

                BuildTarget target = EditorUserBuildSettings.activeBuildTarget;
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

                    string destLocal = Path.Combine(ctx.ServerDataDir, destFileName);
                    File.Copy(src, destLocal, overwrite: true);

                    if (!string.IsNullOrEmpty(ctx.VersionOutputDir))
                        File.Copy(src, Path.Combine(ctx.VersionOutputDir, destFileName), overwrite: true);

                    // Url 只写相对文件名，保持清单环境无关：实际下载地址运行时由客户端
                    // UpdateServerUrl 派生（VersionManager.TryResolveCodePatchFiles）。
                    var patch = new PatchFile
                    {
                        FileName = destFileName,
                        Url      = destFileName,
                        Size     = new FileInfo(destLocal).Length,
                        MD5      = ComputeMD5(destLocal)
                    };
                    ctx.PatchFiles.Add(patch);
                    ctx.Log($"      {patch.FileName} 大小={patch.Size}B  MD5={patch.MD5}");
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
                ctx.ManifestJson = ReleaseManifestWriter.ToJson(new UpdateInfo
                {
                    AppVersion           = ctx.AppVersion,
                    ResourceVersion      = ctx.ResourceVersion,
                    CodeVersion          = ctx.CodeVersion,
                    ForceUpdate          = ctx.ForceUpdate,
                    MinCompatibleVersion = ctx.MinCompatibleVersion,
                    Description          = ctx.Description ?? string.Empty,
                    PatchFiles           = ctx.PatchFiles,
                    UpdateUrl            = ctx.ForceUpdate ? (ctx.UpdateUrl ?? string.Empty) : string.Empty,
                    GrayPercent          = ctx.GrayPercent
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

                bool required = ctx.Profile != null && ctx.Profile.RequireManifestSignature;

                Directory.CreateDirectory(ctx.ServerDataDir);
                WriteOne(Path.Combine(ctx.ServerDataDir, "version.json"), ctx, required);
                ctx.Log($"      写入 → {ctx.ServerDataDir}");

                if (!string.IsNullOrEmpty(ctx.VersionOutputDir))
                {
                    Directory.CreateDirectory(ctx.VersionOutputDir);
                    WriteOne(Path.Combine(ctx.VersionOutputDir, "version.json"), ctx, required);
                    ctx.Log($"      写入 → {ctx.VersionOutputDir}");
                }
            }

            private static void WriteOne(string manifestPath, ReleaseContext ctx, bool required)
            {
                File.WriteAllText(manifestPath, ctx.ManifestJson, System.Text.Encoding.UTF8);
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
        internal static string ComputeMD5(string filePath)
        {
            using var md5 = MD5.Create();
            using var stream = File.OpenRead(filePath);
            byte[] hash = md5.ComputeHash(stream);
            return BitConverter.ToString(hash).Replace("-", "").ToLowerInvariant();
        }
    }
}
