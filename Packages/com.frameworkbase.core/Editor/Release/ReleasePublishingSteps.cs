using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using UnityEngine;

namespace Framework.Editor.Release
{
    /// <summary>
    /// 发布产物审计与部署步骤：先生成不可变发布台账，再把本地 staging 复制到目标根，最后提交签名和 version.json。
    /// </summary>
    public static class ReleasePublishingSteps
    {
        [Serializable]
        private sealed class ArtifactRecord
        {
            public string Path;
            public long Size;
            public string SHA256;
        }

        [Serializable]
        private sealed class ReleaseLedger
        {
            public int SchemaVersion = 1;
            public string ReleaseId;
            public string GeneratedAtUtc;
            public string GitCommit;
            public bool GitWorkingTreeDirty;
            public string UnityVersion;
            public string PackagesLockSHA256;
            public string Environment;
            public string Channel;
            public string BuildTarget;
            public string AppVersion;
            public int ResourceVersion;
            public int CodeVersion;
            public bool PublishResource;
            public bool PublishCode;
            public bool FullPackage;
            public List<ArtifactRecord> Artifacts = new List<ArtifactRecord>();
        }

        /// <summary>
        /// 将 BuildPlayer 产物复制到本次发布 staging 的不可变 full-packages 路径，使整包与热更共享台账和部署链路。
        /// </summary>
        public sealed class StageFullPackageArtifact : IReleaseStep
        {
            public string Name => "StageFullPackageArtifact";
            public string Description => "把整包 Player 产物纳入统一发布 staging";

            public void Execute(ReleaseContext context)
            {
                if (!(context is FullPackageReleaseContext ctx))
                    throw new ArgumentException("StageFullPackageArtifact 只接受 FullPackageReleaseContext。");
                if (string.IsNullOrWhiteSpace(ctx.BuildOutputPath) ||
                    (!File.Exists(ctx.BuildOutputPath) && !Directory.Exists(ctx.BuildOutputPath)))
                {
                    throw new FileNotFoundException($"整包构建产物不存在：{ctx.BuildOutputPath}");
                }

                string destinationRoot = Path.Combine(
                    ctx.ServerDataDir,
                    "full-packages",
                    HotUpdateReleaseSteps.SanitizePathSegment(ctx.AppVersion),
                    ctx.BuildTarget.ToString());
                Directory.CreateDirectory(destinationRoot);

                if (File.Exists(ctx.BuildOutputPath))
                {
                    File.Copy(
                        ctx.BuildOutputPath,
                        Path.Combine(destinationRoot, Path.GetFileName(ctx.BuildOutputPath)),
                        true);
                }
                else
                {
                    string folderName = Path.GetFileName(
                        ctx.BuildOutputPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
                    CopyDirectory(ctx.BuildOutputPath, Path.Combine(destinationRoot, folderName));
                }

                if (Directory.GetFiles(destinationRoot, "*", SearchOption.AllDirectories).Length == 0)
                    throw new IOException($"整包 staging 目录为空：{destinationRoot}");
                ctx.Log($"      整包产物已纳入 staging → {destinationRoot}");
            }
        }

        /// <summary>
        /// 生成机器可读发布台账，记录 Git Commit、Unity 版本、packages-lock 摘要、目标环境/平台和全部产物 SHA-256。
        /// </summary>
        public sealed class WriteReleaseLedger : IReleaseStep
        {
            public string Name => "WriteReleaseLedger";
            public string Description => "生成发布台账与产物 SHA-256 审计记录";

            public void Execute(ReleaseContext ctx)
            {
                if (string.IsNullOrWhiteSpace(ctx.ServerDataDir) || !Directory.Exists(ctx.ServerDataDir))
                    throw new DirectoryNotFoundException($"发布 staging 目录不存在：{ctx.ServerDataDir}");

                string projectRoot = Directory.GetParent(Application.dataPath)?.FullName ?? Directory.GetCurrentDirectory();
                string gitCommit = RunGit(projectRoot, "rev-parse HEAD", required: true);
                string gitStatus = RunGit(projectRoot, "status --porcelain", required: false);
                string packagesLock = Path.Combine(projectRoot, "Packages", "packages-lock.json");
                if (!File.Exists(packagesLock))
                    throw new FileNotFoundException("缺少 Packages/packages-lock.json，无法生成可复现发布台账。", packagesLock);

                var ledger = new ReleaseLedger
                {
                    ReleaseId = ctx.ReleaseId,
                    GeneratedAtUtc = DateTime.UtcNow.ToString("O"),
                    GitCommit = gitCommit,
                    GitWorkingTreeDirty = !string.IsNullOrWhiteSpace(gitStatus),
                    UnityVersion = Application.unityVersion,
                    PackagesLockSHA256 = ComputeSHA256(packagesLock),
                    Environment = ctx.Profile?.Name ?? ctx.EnvironmentName,
                    Channel = Framework.Core.AppConfig.Load()?.AppChannel ?? "default",
                    BuildTarget = ctx.BuildTarget.ToString(),
                    AppVersion = ctx.AppVersion,
                    ResourceVersion = ctx.ResourceVersion,
                    CodeVersion = ctx.CodeVersion,
                    PublishResource = ctx.PublishResource,
                    PublishCode = ctx.PublishCode,
                    FullPackage = ctx.ForceUpdate,
                };

                foreach (string file in Directory.GetFiles(ctx.ServerDataDir, "*", SearchOption.AllDirectories)
                             .OrderBy(path => path, StringComparer.Ordinal))
                {
                    string relative = GetSafeRelativePath(ctx.ServerDataDir, file);
                    ledger.Artifacts.Add(new ArtifactRecord
                    {
                        Path = relative,
                        Size = new FileInfo(file).Length,
                        SHA256 = ComputeSHA256(file),
                    });
                }

                if (ctx is FullPackageReleaseContext full &&
                    !string.IsNullOrWhiteSpace(full.BuildOutputPath) &&
                    (File.Exists(full.BuildOutputPath) || Directory.Exists(full.BuildOutputPath)))
                {
                    foreach (string file in EnumerateArtifactFiles(full.BuildOutputPath))
                    {
                        string relative = File.Exists(full.BuildOutputPath)
                            ? Path.GetFileName(file)
                            : GetSafeRelativePath(full.BuildOutputPath, file);
                        ledger.Artifacts.Add(new ArtifactRecord
                        {
                            Path = "player/" + relative.Replace('\\', '/'),
                            Size = new FileInfo(file).Length,
                            SHA256 = ComputeSHA256(file),
                        });
                    }
                }

                string ledgerDir = Path.Combine(ctx.ServerDataDir, "ledger");
                Directory.CreateDirectory(ledgerDir);
                string ledgerPath = Path.Combine(ledgerDir, $"release-{ctx.ReleaseId}.json");
                File.WriteAllText(ledgerPath, JsonUtility.ToJson(ledger, true), new UTF8Encoding(false));
                ctx.ReleaseLedgerPath = ledgerPath;
                ctx.GitCommit = gitCommit;
                ctx.Log($"      发布台账 → {ledgerPath}");
            }
        }

        /// <summary>
        /// 把本地 staging 发布到 Profile.UploadRoot。所有载荷先进入目标根下的隔离 staging，逐文件校验后提交；
        /// version.json.sig 倒数第二提交，version.json 最后原子替换，使旧清单在整个部署窗口内始终可用。
        /// </summary>
        public sealed class AtomicPublishArtifacts : IReleaseStep
        {
            public string Name => "AtomicPublishArtifacts";
            public string Description => "发布到 UploadRoot，并以 manifest-last 方式原子切换版本";

            public void Execute(ReleaseContext ctx)
            {
                string targetRoot = !string.IsNullOrWhiteSpace(ctx.Profile?.UploadRoot)
                    ? ctx.Profile.UploadRoot
                    : ctx.VersionOutputDir;
                if (string.IsNullOrWhiteSpace(targetRoot))
                {
                    ctx.Log("      未配置 UploadRoot，保留本地 staging，不执行部署。");
                    return;
                }

                string sourceRoot = Path.GetFullPath(ctx.ServerDataDir);
                targetRoot = Path.GetFullPath(targetRoot);
                string sourcePrefix = sourceRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
                string targetPrefix = targetRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
                if (string.Equals(sourceRoot, targetRoot, StringComparison.OrdinalIgnoreCase) ||
                    sourceRoot.StartsWith(targetPrefix, StringComparison.OrdinalIgnoreCase) ||
                    targetRoot.StartsWith(sourcePrefix, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException("ServerDataDir 与 UploadRoot 不得相同或互为父子目录。");
                }

                string manifestRelative = "version.json";
                string signatureRelative = "version.json.sig";
                string sourceManifest = Path.Combine(sourceRoot, manifestRelative);
                string sourceSignature = Path.Combine(sourceRoot, signatureRelative);
                if (!File.Exists(sourceManifest) || !File.Exists(sourceSignature))
                    throw new FileNotFoundException("原子发布要求 version.json 与 version.json.sig 同时存在。");

                string stagingRoot = Path.Combine(targetRoot, ".frameworkbase-staging", ctx.ReleaseId);
                if (Directory.Exists(stagingRoot)) Directory.Delete(stagingRoot, true);
                Directory.CreateDirectory(stagingRoot);

                try
                {
                    var relativeFiles = new List<string>();
                    foreach (string source in Directory.GetFiles(sourceRoot, "*", SearchOption.AllDirectories))
                    {
                        string relative = GetSafeRelativePath(sourceRoot, source);
                        string staged = Path.Combine(stagingRoot, relative.Replace('/', Path.DirectorySeparatorChar));
                        Directory.CreateDirectory(Path.GetDirectoryName(staged));
                        File.Copy(source, staged, true);
                        if (!string.Equals(ComputeSHA256(source), ComputeSHA256(staged), StringComparison.OrdinalIgnoreCase))
                            throw new IOException($"发布 staging 复制校验失败：{relative}");
                        relativeFiles.Add(relative);
                    }

                    foreach (string relative in relativeFiles
                                 .Where(path => path != manifestRelative && path != signatureRelative)
                                 .OrderBy(path => path, StringComparer.Ordinal))
                    {
                        CommitOne(stagingRoot, targetRoot, relative);
                    }

                    CommitOne(stagingRoot, targetRoot, signatureRelative);
                    CommitOne(stagingRoot, targetRoot, manifestRelative);
                    ctx.Log($"      原子发布完成 → {targetRoot}（version.json 最后提交）");
                }
                finally
                {
                    if (Directory.Exists(stagingRoot)) Directory.Delete(stagingRoot, true);
                }
            }
        }

        private static void CommitOne(string stagingRoot, string targetRoot, string relative)
        {
            string source = Path.Combine(stagingRoot, relative.Replace('/', Path.DirectorySeparatorChar));
            string destination = Path.Combine(targetRoot, relative.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(destination));

            bool immutable = relative.StartsWith("payloads/", StringComparison.Ordinal) ||
                             relative.StartsWith("full-packages/", StringComparison.Ordinal);
            if (File.Exists(destination))
            {
                if (string.Equals(ComputeSHA256(source), ComputeSHA256(destination), StringComparison.OrdinalIgnoreCase))
                {
                    File.Delete(source);
                    return;
                }
                if (immutable)
                    throw new IOException($"不可变发布对象已存在但摘要不同，拒绝覆盖：{relative}");

                string backup = destination + ".release-backup";
                if (File.Exists(backup)) File.Delete(backup);
                File.Replace(source, destination, backup, true);
                if (File.Exists(backup)) File.Delete(backup);
                return;
            }

            File.Move(source, destination);
        }

        private static void CopyDirectory(string source, string destination)
        {
            Directory.CreateDirectory(destination);
            foreach (string directory in Directory.GetDirectories(source, "*", SearchOption.AllDirectories))
            {
                string relative = GetSafeRelativePath(source, directory);
                Directory.CreateDirectory(Path.Combine(destination, relative.Replace('/', Path.DirectorySeparatorChar)));
            }
            foreach (string file in Directory.GetFiles(source, "*", SearchOption.AllDirectories))
            {
                string relative = GetSafeRelativePath(source, file);
                string target = Path.Combine(destination, relative.Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(target));
                File.Copy(file, target, true);
            }
        }

        private static IEnumerable<string> EnumerateArtifactFiles(string path)
        {
            if (File.Exists(path)) return new[] { path };
            return Directory.GetFiles(path, "*", SearchOption.AllDirectories)
                .OrderBy(item => item, StringComparer.Ordinal);
        }

        private static string GetSafeRelativePath(string root, string file)
        {
            string normalizedRoot = Path.GetFullPath(root)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
            string normalizedFile = Path.GetFullPath(file);
            if (!normalizedFile.StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"产物路径逃逸发布根目录：{normalizedFile}");
            return normalizedFile.Substring(normalizedRoot.Length).Replace('\\', '/');
        }

        private static string ComputeSHA256(string path)
        {
            using (SHA256 sha = SHA256.Create())
            using (FileStream stream = File.OpenRead(path))
            {
                return BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", string.Empty).ToLowerInvariant();
            }
        }

        private static string RunGit(string workDir, string arguments, bool required)
        {
            try
            {
                var startInfo = new ProcessStartInfo
                {
                    FileName = "git",
                    Arguments = arguments,
                    WorkingDirectory = workDir,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                };
                using (Process process = Process.Start(startInfo))
                {
                    string output = process.StandardOutput.ReadToEnd();
                    string error = process.StandardError.ReadToEnd();
                    process.WaitForExit(10000);
                    if (process.ExitCode != 0)
                    {
                        if (required) throw new InvalidOperationException(error);
                        return string.Empty;
                    }
                    return output.Trim();
                }
            }
            catch when (!required)
            {
                return string.Empty;
            }
        }
    }
}
