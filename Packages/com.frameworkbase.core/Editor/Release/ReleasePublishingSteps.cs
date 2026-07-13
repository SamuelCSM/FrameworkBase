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

                // 整包产物同样进入本 release 的不可变版本目录，与热更补丁共享回滚/晋级引用单元。
                string destinationRoot = string.IsNullOrEmpty(ctx.ReleaseDirRelative)
                    ? Path.Combine(
                        ctx.ServerDataDir,
                        "full-packages",
                        HotUpdateReleaseSteps.SanitizePathSegment(ctx.AppVersion),
                        ctx.BuildTarget.ToString())
                    : Path.Combine(
                        ctx.ServerDataDir,
                        ctx.ReleaseDirRelative.Replace('/', Path.DirectorySeparatorChar),
                        "full-packages",
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

                // 台账随不可变版本目录冻结（releases/{app}/{releaseId}/ledger.json），
                // 与其审计的产物同生命周期；无版本目录时回退旧位置（单元测试/迁移期）。
                string ledgerDir = string.IsNullOrEmpty(ctx.ReleaseDirRelative)
                    ? Path.Combine(ctx.ServerDataDir, "ledger")
                    : Path.Combine(ctx.ServerDataDir, ctx.ReleaseDirRelative.Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(ledgerDir);
                string ledgerPath = string.IsNullOrEmpty(ctx.ReleaseDirRelative)
                    ? Path.Combine(ledgerDir, $"release-{ctx.ReleaseId}.json")
                    : Path.Combine(ledgerDir, "ledger.json");
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
                string deployTarget = !string.IsNullOrWhiteSpace(ctx.Profile?.UploadRoot)
                    ? ctx.Profile.UploadRoot
                    : ctx.VersionOutputDir;

                // 部署目标失败关闭闸门（P0）：旧实现在目标为空时静默保留本地 staging 并返回，
                // 导致 Publish 模式的工作流"看起来成功"却从未部署。现在按模式判定：
                //   - BuildOnly：允许无部署目标，只保留 staging（此时才是合法的"不部署"）；
                //   - Publish/Promote/Rollback：目标为空由工厂抛 RELEASE_E_STORE_NOT_CONFIGURED。
                if (ctx.Mode == ReleaseMode.BuildOnly)
                {
                    ctx.Log("      BuildOnly 模式：只生成候选产物，保留本地 staging，不执行部署。");
                    return;
                }

                IReleaseArtifactStore store = ReleaseArtifactStoreFactory.Resolve(ctx.Mode, deployTarget);
                string targetRoot = ((LocalFileSystemReleaseStore)store).Root;
                ctx.Log($"      发布存储：{store.Describe()}");

                // 产物仓库作用域：{UploadRoot}/{env}/{platform}/{channel}。环境、平台、渠道
                // 在存储层物理隔离，客户端 UpdateServerUrl 指向各自渠道根。
                if (!string.IsNullOrEmpty(ctx.PublishScopeRelative))
                    targetRoot = Path.Combine(targetRoot, ctx.PublishScopeRelative.Replace('/', Path.DirectorySeparatorChar));

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
                    ctx.PublishedRootAbsolute = targetRoot;
                    ctx.Log($"      原子发布完成 → {targetRoot}（version.json 最后提交）");
                }
                finally
                {
                    if (Directory.Exists(stagingRoot)) Directory.Delete(stagingRoot, true);
                }
            }
        }

        /// <summary>
        /// 上传后校验（目标设计 §1.5，状态机 Published→Verified）：从已发布的渠道根回读本 release
        /// 台账列出的每个产物，逐文件比对 SHA-256。任何缺失或不一致都抛异常中止，后续指针切换不会执行，
        /// 线上指针保持指向上一个已验证 release。
        /// </summary>
        public sealed class VerifyPublishedArtifacts : IReleaseStep
        {
            public string Name => "VerifyPublishedArtifacts";
            public string Description => "回读已发布产物并逐文件比对 SHA-256（Published→Verified）";

            public void Execute(ReleaseContext ctx)
            {
                if (string.IsNullOrEmpty(ctx.PublishedRootAbsolute))
                {
                    ctx.Log("      未执行部署，跳过上传后校验。");
                    return;
                }

                int verified = VerifyLedgerArtifacts(
                    ctx.PublishedRootAbsolute,
                    Path.Combine(
                        ctx.PublishedRootAbsolute,
                        ctx.ReleaseDirRelative.Replace('/', Path.DirectorySeparatorChar),
                        "ledger.json"),
                    immutableOnly: false);
                ctx.Log($"      上传后校验通过：{verified} 个产物 SHA-256 与台账一致（Verified）。");
            }
        }

        /// <summary>
        /// 切换渠道根 current.json 签名指针（状态机 Verified→Active）：唯一可变对象，
        /// 先提交伴生签名再提交指针本体；PreviousReleaseId 指向上一个激活 release 形成可回溯历史链。
        /// </summary>
        public sealed class SwitchCurrentPointer : IReleaseStep
        {
            public string Name => "SwitchCurrentPointer";
            public string Description => "签名并原子切换 current.json 指针（Verified→Active）";

            public void Execute(ReleaseContext ctx)
            {
                if (string.IsNullOrEmpty(ctx.PublishedRootAbsolute) || string.IsNullOrEmpty(ctx.ReleaseDirRelative))
                {
                    ctx.Log("      未执行部署或缺少版本目录，跳过指针切换。");
                    return;
                }

                string previousReleaseId = string.Empty;
                string currentPath = Path.Combine(ctx.PublishedRootAbsolute, "current.json");
                if (File.Exists(currentPath))
                {
                    var existing = JsonUtility.FromJson<Framework.HotUpdate.CurrentPointer>(File.ReadAllText(currentPath));
                    previousReleaseId = existing?.ReleaseId ?? string.Empty;
                }

                var pointer = new Framework.HotUpdate.CurrentPointer
                {
                    SchemaVersion = 1,
                    KeyId = ctx.Profile?.SigningKeyRef ?? "development",
                    Env = ctx.EnvironmentName,
                    Platform = HotUpdateReleaseSteps.GetPlatformId(ctx.BuildTarget),
                    Channel = ctx.Channel,
                    AppVersion = ctx.AppVersion,
                    ReleaseId = ctx.ReleaseId,
                    ManifestPath = ctx.ReleaseDirRelative + "/version.json",
                    PreviousReleaseId = previousReleaseId,
                    SwitchedAtUnixSeconds = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                    SwitchedBy = string.IsNullOrWhiteSpace(ctx.SwitchedBy)
                        ? $"{Environment.UserName}@{Environment.MachineName}"
                        : ctx.SwitchedBy,
                };
                CommitPointer(ctx.PublishedRootAbsolute, pointer, ctx.Log);
                ctx.Log($"      指针已切换：{previousReleaseId} → {pointer.ReleaseId}（Active）。");
            }
        }

        /// <summary>
        /// 序列化、签名并以"签名先行、指针最后"顺序原子提交 current.json。指针契约要求无 BOM UTF-8。
        /// </summary>
        internal static void CommitPointer(string publishedRoot, Framework.HotUpdate.CurrentPointer pointer, Action<string> log)
        {
            string json = JsonUtility.ToJson(pointer, true);
            string tempPointer = Path.Combine(publishedRoot, "current.json.publishing");
            File.WriteAllText(tempPointer, json, new UTF8Encoding(false));
            if (!UpdateManifestSigner.SignManifestForPublish(tempPointer, log, required: true))
            {
                File.Delete(tempPointer);
                throw new InvalidOperationException("current.json 指针签名失败，指针未切换。");
            }

            ReplaceFile(tempPointer + ".sig", Path.Combine(publishedRoot, "current.json.sig"));
            ReplaceFile(tempPointer, Path.Combine(publishedRoot, "current.json"));
        }

        /// <summary>
        /// 按台账回读校验发布根下的产物。immutableOnly=true 时仅校验 releases/ 前缀
        /// （回滚场景：根别名已随后续发布合法变化，不参与目标 release 的完整性判定）。
        /// </summary>
        /// <returns>实际校验的产物数量。</returns>
        internal static int VerifyLedgerArtifacts(string publishedRoot, string ledgerPath, bool immutableOnly)
        {
            if (!File.Exists(ledgerPath))
                throw new FileNotFoundException("发布台账不存在，无法执行回读校验。", ledgerPath);

            var ledger = JsonUtility.FromJson<ReleaseLedger>(File.ReadAllText(ledgerPath));
            if (ledger == null || ledger.Artifacts == null || ledger.Artifacts.Count == 0)
                throw new InvalidDataException($"发布台账无产物记录：{ledgerPath}");

            int verified = 0;
            foreach (ArtifactRecord record in ledger.Artifacts)
            {
                // player/ 前缀是构建原始产物的审计记录，不部署到渠道根，不参与回读。
                if (record.Path.StartsWith("player/", StringComparison.Ordinal)) continue;
                if (immutableOnly && !record.Path.StartsWith("releases/", StringComparison.Ordinal)) continue;

                string published = Path.Combine(publishedRoot, record.Path.Replace('/', Path.DirectorySeparatorChar));
                if (!File.Exists(published))
                    throw new FileNotFoundException($"回读校验失败：已发布产物缺失 {record.Path}", published);
                string actual = ComputeSHA256(published);
                if (!string.Equals(actual, record.SHA256, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidDataException(
                        $"回读校验失败：{record.Path} SHA-256 不一致，期望={record.SHA256}，实际={actual}");
                }
                verified++;
            }

            if (verified == 0)
                throw new InvalidDataException($"回读校验未覆盖任何产物：{ledgerPath}");
            return verified;
        }

        /// <summary>
        /// 一键回滚（目标设计 §1.3，状态机 Active→RolledBack）：把渠道根指针回切到指定或上一个
        /// releaseId。不重建、不重传产物；回切前对目标 release 的不可变产物集执行台账回读校验
        /// （回滚目标必须仍处于 Verified 完整性），同时同步渠道根 version.json 别名保护旧客户端。
        /// </summary>
        /// <param name="ctx">已通过环境校验的上下文（Profile/作用域已填充）。</param>
        /// <param name="targetReleaseId">回滚目标 releaseId；为空时取当前指针的 PreviousReleaseId。</param>
        public static void ExecuteRollback(ReleaseContext ctx, string targetReleaseId)
        {
            // 失败关闭：回滚要求非空部署目标，经统一存储工厂发出稳定错误码 RELEASE_E_STORE_NOT_CONFIGURED。
            string uploadRoot = ((LocalFileSystemReleaseStore)
                ReleaseArtifactStoreFactory.Resolve(ReleaseMode.Rollback, ctx.Profile?.UploadRoot)).Root;
            string publishedRoot = string.IsNullOrEmpty(ctx.PublishScopeRelative)
                ? Path.GetFullPath(uploadRoot)
                : Path.GetFullPath(Path.Combine(uploadRoot, ctx.PublishScopeRelative.Replace('/', Path.DirectorySeparatorChar)));

            string currentPath = Path.Combine(publishedRoot, "current.json");
            if (!File.Exists(currentPath))
                throw new FileNotFoundException("渠道根不存在 current.json 指针，无法回滚。", currentPath);
            var current = JsonUtility.FromJson<Framework.HotUpdate.CurrentPointer>(File.ReadAllText(currentPath));
            if (current == null || string.IsNullOrWhiteSpace(current.ReleaseId))
                throw new InvalidDataException("current.json 指针无法解析。");

            string target = string.IsNullOrWhiteSpace(targetReleaseId) ? current.PreviousReleaseId : targetReleaseId.Trim();
            if (string.IsNullOrWhiteSpace(target))
                throw new InvalidOperationException("指针没有 PreviousReleaseId 且未指定回滚目标，历史链为空。");
            if (string.Equals(target, current.ReleaseId, StringComparison.Ordinal))
                throw new InvalidOperationException($"回滚目标与当前激活 release 相同：{target}");

            // 定位目标不可变版本目录：releases/{appVersion}/{releaseId}
            string releasesRoot = Path.Combine(publishedRoot, "releases");
            string targetDir = Directory.Exists(releasesRoot)
                ? Directory.GetDirectories(releasesRoot, target, SearchOption.AllDirectories)
                    .FirstOrDefault(dir => string.Equals(Path.GetFileName(dir), target, StringComparison.Ordinal))
                : null;
            if (targetDir == null)
                throw new DirectoryNotFoundException($"渠道根 releases/ 下找不到回滚目标版本目录：{target}");

            string targetManifest = Path.Combine(targetDir, "version.json");
            string targetSignature = targetManifest + ".sig";
            string targetLedger = Path.Combine(targetDir, "ledger.json");
            if (!File.Exists(targetManifest) || !File.Exists(targetSignature))
                throw new FileNotFoundException($"回滚目标缺少正本清单或签名：{targetDir}");

            // 目标 release 必须仍满足 Verified：不可变产物集逐文件回读校验（别名不参与，见方法注释）。
            int verified = VerifyLedgerArtifacts(publishedRoot, targetLedger, immutableOnly: true);
            ctx.Log($"      回滚目标完整性复验通过：{verified} 个不可变产物一致。");

            var targetInfo = JsonUtility.FromJson<Framework.HotUpdate.UpdateInfo>(File.ReadAllText(targetManifest));
            if (targetInfo == null)
                throw new InvalidDataException($"回滚目标正本清单无法解析：{targetManifest}");

            // 渠道根别名同步（旧客户端路径），签名先行、清单最后。
            string aliasSignature = Path.Combine(publishedRoot, "version.json.sig");
            string aliasManifest = Path.Combine(publishedRoot, "version.json");
            string tempSig = aliasSignature + ".rollback";
            string tempManifest = aliasManifest + ".rollback";
            File.Copy(targetSignature, tempSig, true);
            File.Copy(targetManifest, tempManifest, true);
            ReplaceFile(tempSig, aliasSignature);
            ReplaceFile(tempManifest, aliasManifest);

            string relativeManifest = GetSafeRelativePath(publishedRoot, targetManifest);
            var pointer = new Framework.HotUpdate.CurrentPointer
            {
                SchemaVersion = 1,
                KeyId = ctx.Profile?.SigningKeyRef ?? "development",
                Env = ctx.EnvironmentName,
                Platform = HotUpdateReleaseSteps.GetPlatformId(ctx.BuildTarget),
                Channel = ctx.Channel,
                AppVersion = targetInfo.AppVersion,
                ReleaseId = target,
                ManifestPath = relativeManifest,
                PreviousReleaseId = current.ReleaseId, // 历史链指向被回滚者，链条不断裂
                SwitchedAtUnixSeconds = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                SwitchedBy = string.IsNullOrWhiteSpace(ctx.SwitchedBy)
                    ? $"{Environment.UserName}@{Environment.MachineName}"
                    : ctx.SwitchedBy,
            };
            CommitPointer(publishedRoot, pointer, ctx.Log);
            ctx.Log($"      回滚完成：{current.ReleaseId} → {target}（RolledBack，仅指针与别名变化）。");
        }

        /// <summary>
        /// 晋级（目标设计 §1.4）：把源环境已验证的 releaseId 以"同产物重签"方式推进目标环境。
        /// <para>
        /// payload 逐字节复用（SHA-256 不变，"测过的就是发出去的"）；仅清单重新派生：KeyId 换目标
        /// 环境签名引用、补丁 URL 从源渠道根重根到目标渠道根、签发/失效时间刷新。目标侧完成
        /// 回读校验后才切指针，禁止 prod 单独重建产物。
        /// </para>
        /// </summary>
        /// <param name="ctx">已通过目标环境校验的上下文（Profile/作用域已填充）。</param>
        /// <param name="sourceEnv">源环境名（如 qa）。</param>
        /// <param name="sourceReleaseId">要晋级的 releaseId；为空时取源渠道根当前指针的激活 release。</param>
        public static void ExecutePromote(ReleaseContext ctx, string sourceEnv, string sourceReleaseId)
        {
            if (string.IsNullOrWhiteSpace(sourceEnv))
                throw new ArgumentException("晋级必须指定 -sourceEnv 源环境。");
            if (string.Equals(sourceEnv.Trim(), ctx.EnvironmentName, StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("晋级源环境与目标环境相同。");

            ReleaseProfile sourceProfile = ReleaseProfileStore.TryLoad(sourceEnv.Trim(), out string loadError);
            if (sourceProfile == null)
                throw new InvalidOperationException($"源环境 {sourceEnv} 加载失败：{loadError}");

            // 失败关闭：晋级要求非空部署目标（产物仓库单一物理根），经统一存储工厂发出稳定错误码。
            string uploadRoot = ((LocalFileSystemReleaseStore)
                ReleaseArtifactStoreFactory.Resolve(ReleaseMode.Promote, ctx.Profile?.UploadRoot)).Root;

            // 单一产物仓库根：源与目标只是根下不同 {env}/{platform}/{channel} 作用域。
            string platformSegment = HotUpdateReleaseSteps.GetPlatformId(ctx.BuildTarget);
            string sourceScope = $"{HotUpdateReleaseSteps.SanitizePathSegment(sourceEnv.Trim())}/{platformSegment}/{ctx.Channel}";
            string sourceRoot = Path.GetFullPath(Path.Combine(uploadRoot, sourceScope.Replace('/', Path.DirectorySeparatorChar)));
            string targetRoot = Path.GetFullPath(Path.Combine(uploadRoot, ctx.PublishScopeRelative.Replace('/', Path.DirectorySeparatorChar)));

            // 1. 确定晋级对象：显式指定或源指针当前激活 release。
            string releaseId = sourceReleaseId?.Trim();
            if (string.IsNullOrWhiteSpace(releaseId))
            {
                string sourcePointerPath = Path.Combine(sourceRoot, "current.json");
                if (!File.Exists(sourcePointerPath))
                    throw new FileNotFoundException("源渠道根无 current.json 且未显式指定 -sourceReleaseId。", sourcePointerPath);
                var sourcePointer = JsonUtility.FromJson<Framework.HotUpdate.CurrentPointer>(File.ReadAllText(sourcePointerPath));
                releaseId = sourcePointer?.ReleaseId;
                if (string.IsNullOrWhiteSpace(releaseId))
                    throw new InvalidDataException("源渠道根指针无法解析出激活 releaseId。");
            }

            // 2. 定位源版本目录并复验完整性（晋级对象必须仍 Verified）。
            string sourceReleasesRoot = Path.Combine(sourceRoot, "releases");
            string sourceDir = Directory.Exists(sourceReleasesRoot)
                ? Directory.GetDirectories(sourceReleasesRoot, releaseId, SearchOption.AllDirectories)
                    .FirstOrDefault(dir => string.Equals(Path.GetFileName(dir), releaseId, StringComparison.Ordinal))
                : null;
            if (sourceDir == null)
                throw new DirectoryNotFoundException($"源环境 releases/ 下找不到晋级目标：{releaseId}");
            int verifiedSource = VerifyLedgerArtifacts(sourceRoot, Path.Combine(sourceDir, "ledger.json"), immutableOnly: true);
            ctx.Log($"      源 release 完整性复验通过：{verifiedSource} 个不可变产物一致。");

            string releaseRelative = GetSafeRelativePath(sourceRoot, sourceDir);
            string targetDir = Path.Combine(targetRoot, releaseRelative.Replace('/', Path.DirectorySeparatorChar));
            if (Directory.Exists(targetDir))
                throw new IOException($"目标环境已存在同 releaseId 版本目录，禁止覆盖：{releaseRelative}");

            // 3. payload 逐字节复制并校验（同产物：SHA-256 必须与源一致）。
            Directory.CreateDirectory(targetDir);
            foreach (string sourceFile in Directory.GetFiles(sourceDir, "*", SearchOption.AllDirectories))
            {
                string name = Path.GetFileName(sourceFile);
                if (name == "version.json" || name == "version.json.sig" || name == "ledger.json")
                    continue; // 清单重派生、台账重生成，其余全部逐字节复用
                string relative = GetSafeRelativePath(sourceDir, sourceFile);
                string destination = Path.Combine(targetDir, relative.Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(destination));
                File.Copy(sourceFile, destination, false);
                if (!string.Equals(ComputeSHA256(sourceFile), ComputeSHA256(destination), StringComparison.OrdinalIgnoreCase))
                    throw new IOException($"晋级复制校验失败：{relative}");
            }

            // 4. 重派生清单：payload 哈希不变，仅换 KeyId、重根 URL、刷新时效。
            var manifest = JsonUtility.FromJson<Framework.HotUpdate.UpdateInfo>(
                File.ReadAllText(Path.Combine(sourceDir, "version.json")));
            if (manifest == null)
                throw new InvalidDataException("源正本清单无法解析。");
            string sourceChannelUrl = sourceProfile.BaseUrl.TrimEnd('/') + "/" + sourceScope + "/";
            string targetChannelUrl = ctx.Profile.BaseUrl.TrimEnd('/') + "/" + ctx.PublishScopeRelative + "/";
            foreach (Framework.HotUpdate.PatchFile patch in manifest.PatchFiles)
            {
                if (!patch.Url.StartsWith(sourceChannelUrl, StringComparison.Ordinal))
                    throw new InvalidDataException($"源清单补丁 URL 不在源渠道根下，晋级无法重根：{patch.Url}");
                patch.Url = targetChannelUrl + patch.Url.Substring(sourceChannelUrl.Length);
            }
            long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            manifest.KeyId = ctx.Profile.SigningKeyRef;
            manifest.IssuedAtUnixSeconds = now;
            manifest.ExpiresAtUnixSeconds = now + 30L * 24 * 60 * 60;

            string manifestJson = ReleaseManifestWriter.ToJson(manifest);
            string targetManifest = Path.Combine(targetDir, "version.json");
            File.WriteAllText(targetManifest, manifestJson, new UTF8Encoding(false));
            if (!UpdateManifestSigner.SignManifestForPublish(targetManifest, ctx.Log, required: true))
                throw new InvalidOperationException("晋级清单签名失败，已中止。");

            // 5. 重生成目标台账：构建溯源字段照抄源台账，产物记录按目标目录重新枚举。
            var sourceLedger = JsonUtility.FromJson<ReleaseLedger>(
                File.ReadAllText(Path.Combine(sourceDir, "ledger.json")));
            var targetLedger = new ReleaseLedger
            {
                ReleaseId = releaseId,
                GeneratedAtUtc = DateTime.UtcNow.ToString("O"),
                GitCommit = sourceLedger?.GitCommit ?? string.Empty,
                GitWorkingTreeDirty = sourceLedger?.GitWorkingTreeDirty ?? false,
                UnityVersion = sourceLedger?.UnityVersion ?? string.Empty,
                PackagesLockSHA256 = sourceLedger?.PackagesLockSHA256 ?? string.Empty,
                Environment = ctx.EnvironmentName,
                Channel = ctx.Channel,
                BuildTarget = sourceLedger?.BuildTarget ?? ctx.BuildTarget.ToString(),
                AppVersion = manifest.AppVersion,
                ResourceVersion = manifest.ResourceVersion,
                CodeVersion = manifest.CodeVersion,
                PublishResource = sourceLedger?.PublishResource ?? false,
                PublishCode = sourceLedger?.PublishCode ?? false,
                FullPackage = sourceLedger?.FullPackage ?? false,
            };
            foreach (string file in Directory.GetFiles(targetDir, "*", SearchOption.AllDirectories)
                         .OrderBy(path => path, StringComparer.Ordinal))
            {
                targetLedger.Artifacts.Add(new ArtifactRecord
                {
                    Path = GetSafeRelativePath(targetRoot, file),
                    Size = new FileInfo(file).Length,
                    SHA256 = ComputeSHA256(file),
                });
            }
            string targetLedgerPath = Path.Combine(targetDir, "ledger.json");
            File.WriteAllText(targetLedgerPath, JsonUtility.ToJson(targetLedger, true), new UTF8Encoding(false));

            // 6. 目标侧回读校验（Published→Verified）后，别名同步 + 指针切换（Verified→Active）。
            int verifiedTarget = VerifyLedgerArtifacts(targetRoot, targetLedgerPath, immutableOnly: true);
            ctx.Log($"      目标侧回读校验通过：{verifiedTarget} 个产物一致。");

            string aliasSig = Path.Combine(targetRoot, "version.json.sig");
            string aliasManifest = Path.Combine(targetRoot, "version.json");
            string tempSig = aliasSig + ".promote";
            string tempManifest = aliasManifest + ".promote";
            File.Copy(targetManifest + ".sig", tempSig, true);
            File.Copy(targetManifest, tempManifest, true);
            ReplaceFile(tempSig, aliasSig);
            ReplaceFile(tempManifest, aliasManifest);

            string previousReleaseId = string.Empty;
            string targetPointerPath = Path.Combine(targetRoot, "current.json");
            if (File.Exists(targetPointerPath))
            {
                var existing = JsonUtility.FromJson<Framework.HotUpdate.CurrentPointer>(File.ReadAllText(targetPointerPath));
                previousReleaseId = existing?.ReleaseId ?? string.Empty;
            }
            var pointer = new Framework.HotUpdate.CurrentPointer
            {
                SchemaVersion = 1,
                KeyId = ctx.Profile.SigningKeyRef,
                Env = ctx.EnvironmentName,
                Platform = platformSegment,
                Channel = ctx.Channel,
                AppVersion = manifest.AppVersion,
                ReleaseId = releaseId,
                ManifestPath = releaseRelative + "/version.json",
                PreviousReleaseId = previousReleaseId,
                SwitchedAtUnixSeconds = now,
                SwitchedBy = string.IsNullOrWhiteSpace(ctx.SwitchedBy)
                    ? $"{Environment.UserName}@{Environment.MachineName}"
                    : ctx.SwitchedBy,
            };
            CommitPointer(targetRoot, pointer, ctx.Log);
            ctx.Log($"      晋级完成：{sourceEnv}:{releaseId} → {ctx.EnvironmentName}（同产物重签，payload 零重建）。");
        }

        /// <summary>
        /// 只校验（VerifyOnly，状态机不迁移）：对已发布 release 的不可变产物集回读并逐文件校验 SHA-256。
        /// 不产出、不切指针；部署目标为空由存储工厂失败关闭。用于发布后独立复核或告警自愈校验。
        /// </summary>
        /// <param name="ctx">已通过环境校验的上下文（Profile/作用域已填充，Mode=VerifyOnly）。</param>
        /// <param name="targetReleaseId">要校验的 releaseId。</param>
        public static void ExecuteVerifyOnly(ReleaseContext ctx, string targetReleaseId)
        {
            if (string.IsNullOrWhiteSpace(targetReleaseId))
                throw new ArgumentException("VerifyOnly 必须指定 -targetReleaseId。");

            // 复用存储工厂的失败关闭闸门：VerifyOnly 目标为空同样直接失败（无目标何谈校验）。
            IReleaseArtifactStore store = ReleaseArtifactStoreFactory.Resolve(ctx.Mode, ctx.Profile?.UploadRoot);
            string publishedRoot = string.IsNullOrEmpty(ctx.PublishScopeRelative)
                ? ((LocalFileSystemReleaseStore)store).Root
                : Path.Combine(((LocalFileSystemReleaseStore)store).Root,
                    ctx.PublishScopeRelative.Replace('/', Path.DirectorySeparatorChar));

            string releasesRoot = Path.Combine(publishedRoot, "releases");
            string targetDir = Directory.Exists(releasesRoot)
                ? Directory.GetDirectories(releasesRoot, targetReleaseId, SearchOption.AllDirectories)
                    .FirstOrDefault(dir => string.Equals(Path.GetFileName(dir), targetReleaseId, StringComparison.Ordinal))
                : null;
            if (targetDir == null)
                throw new DirectoryNotFoundException($"渠道根 releases/ 下找不到校验目标版本目录：{targetReleaseId}");

            int verified = VerifyLedgerArtifacts(publishedRoot, Path.Combine(targetDir, "ledger.json"), immutableOnly: true);
            ctx.Log($"      VerifyOnly 通过：release {targetReleaseId} 的 {verified} 个不可变产物 SHA-256 与台账一致。");
        }

        private static void ReplaceFile(string source, string destination)
        {
            if (File.Exists(destination))
            {
                string backup = destination + ".release-backup";
                if (File.Exists(backup)) File.Delete(backup);
                File.Replace(source, destination, backup, true);
                if (File.Exists(backup)) File.Delete(backup);
                return;
            }
            File.Move(source, destination);
        }

        private static void CommitOne(string stagingRoot, string targetRoot, string relative)
        {
            string source = Path.Combine(stagingRoot, relative.Replace('/', Path.DirectorySeparatorChar));
            string destination = Path.Combine(targetRoot, relative.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(destination));

            // releases/ 是目标布局的不可变版本目录；payloads/、full-packages/ 为迁移期旧布局前缀。
            bool immutable = relative.StartsWith("releases/", StringComparison.Ordinal) ||
                             relative.StartsWith("payloads/", StringComparison.Ordinal) ||
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
