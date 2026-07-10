using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Framework.Core;
using Framework.Serialization;
using Framework.Storage;
using UnityEngine;

namespace Framework.HotUpdate
{
    /// <summary>
    /// 热更新代码事务槽管理器。
    /// <para>
    /// 所有补丁先下载到独立 staging 目录，完整通过 Size + SHA-256 校验后，再通过同卷目录 Move 提交为不可变槽。
    /// 运行时只从状态文件指向的 ActiveSlot 读取程序集，禁止扫描或加载 persistentDataPath 根目录中的散装 DLL。
    /// </para>
    /// <para>
    /// 新槽激活后先处于 PendingConfirmation 状态，只有 HotfixEntry.Start 成功完成才提升为 Last-Known-Good。
    /// 如果进程在确认前崩溃、被杀或下次启动仍发现 Pending，则自动回滚到上一已确认槽，避免坏补丁造成永久启动死循环。
    /// </para>
    /// <para>
    /// 槽状态按 AppVersion 隔离。整包升级后旧槽不会被新原生运行时继续加载，防止跨整包复用不兼容程序集。
    /// </para>
    /// </summary>
    internal static class HotUpdateSlotManager
    {
        /// <summary>
        /// 安装状态的持久化结构。Active、Pending 与 LastKnownGood 的切换必须在同一把锁内完成并原子写盘。
        /// </summary>
        [Serializable]
        private sealed class InstallState
        {
            public int SchemaVersion = 1;
            public string AppVersion = string.Empty;
            public string ActiveSlot = string.Empty;
            public string LastKnownGoodSlot = string.Empty;
            public string PendingConfirmationSlot = string.Empty;
            public long UpdatedAtUnixSeconds;
        }

        /// <summary>
        /// 不可变代码槽自描述清单，记录整包边界、代码版本及完整文件摘要，用于每次启动前重新验证。
        /// </summary>
        [Serializable]
        private sealed class SlotManifest
        {
            public int SchemaVersion = 1;
            public string SlotId = string.Empty;
            public string AppVersion = string.Empty;
            public int CodeVersion;
            public long InstalledAtUnixSeconds;
            public List<PatchFile> Files = new List<PatchFile>();
        }

        private const string StateFileName = "install-state.json";
        private const string SlotManifestFileName = "slot.json";
        private static readonly object Sync = new object();
        private static readonly StringComparison PathComparison =
            Path.DirectorySeparatorChar == '\\' ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        private static InstallState _state;

#if UNITY_INCLUDE_TESTS
        /// <summary>
        /// 仅供本仓库测试程序集注入隔离根目录，避免 EditMode 测试读写开发者真实 persistentDataPath。
        /// 正式 Player 不编译该入口。
        /// </summary>
        internal static string TestRootDirectoryOverride;

        /// <summary>
        /// 清除进程内状态缓存，使测试能够模拟应用被杀后重新启动并重新读取 install-state.json。
        /// </summary>
        internal static void ResetStateForTests()
        {
            lock (Sync) _state = null;
        }
#endif

        private static string RootDirectory
        {
            get
            {
#if UNITY_INCLUDE_TESTS
                if (!string.IsNullOrEmpty(TestRootDirectoryOverride))
                    return TestRootDirectoryOverride;
#endif
                return Path.Combine(Application.persistentDataPath, "FrameworkBase", "HotUpdate");
            }
        }

        private static string SlotsDirectory => Path.Combine(RootDirectory, "slots");
        private static string StagingDirectory => Path.Combine(RootDirectory, "staging");
        private static string StatePath => Path.Combine(RootDirectory, StateFileName);

        /// <summary>
        /// 在任何热更新程序集加载之前准备启动状态：清理遗留 staging、隔离其他 AppVersion 的状态，
        /// 将上次未确认槽视为启动失败并回滚到 LKG，同时重新校验 ActiveSlot 中每个程序集的长度和摘要。
        /// </summary>
        public static void PrepareForLaunch()
        {
            lock (Sync)
            {
                EnsureDirectories();
                CleanupStagingDirectories();
                InstallState state = LoadState();

                if (!string.Equals(state.AppVersion, Application.version, StringComparison.Ordinal))
                {
                    GameLog.Log($"[HotUpdateSlots] 整包版本已变化：{state.AppVersion} -> {Application.version}，旧代码槽不再参与本次启动。");
                    state = NewState();
                    SaveState(state);
                    _state = state;
                    CleanupObsoleteSlots(state);
                    return;
                }

                if (!string.IsNullOrEmpty(state.PendingConfirmationSlot))
                {
                    string failedSlot = state.PendingConfirmationSlot;
                    state.ActiveSlot = ValidateSlot(state.LastKnownGoodSlot, out _)
                        ? state.LastKnownGoodSlot
                        : string.Empty;
                    state.PendingConfirmationSlot = string.Empty;
                    state.UpdatedAtUnixSeconds = Now();
                    SaveState(state);
                    GameLog.Error($"[HotUpdateSlots] 检测到上次未确认槽 {failedSlot}，已回滚到 LKG={state.ActiveSlot}。");
                }

                if (!string.IsNullOrEmpty(state.ActiveSlot) && !ValidateSlot(state.ActiveSlot, out string reason))
                {
                    GameLog.Error($"[HotUpdateSlots] 活动槽校验失败（{state.ActiveSlot}）：{reason}");
                    state.ActiveSlot = ValidateSlot(state.LastKnownGoodSlot, out _)
                        ? state.LastKnownGoodSlot
                        : string.Empty;
                    state.PendingConfirmationSlot = string.Empty;
                    state.UpdatedAtUnixSeconds = Now();
                    SaveState(state);
                }

                _state = state;
                CleanupObsoleteSlots(state);
            }
        }

        /// <summary>
        /// 为已通过安全准入的清单创建全新 staging 目录。目录名由 AppVersion、CodeVersion 和文件集摘要确定，
        /// 同一 SlotId 的旧 staging 会先被清理，正式槽不会在此阶段受到修改。
        /// </summary>
        /// <param name="updateInfo">已验签并通过字段准入的更新清单。</param>
        /// <returns>供下载器写入的 staging 绝对目录。</returns>
        public static string PrepareStagingSlot(UpdateInfo updateInfo)
        {
            if (updateInfo == null) throw new ArgumentNullException(nameof(updateInfo));
            if (!string.Equals(updateInfo.AppVersion, Application.version, StringComparison.Ordinal))
                throw new InvalidDataException($"代码补丁 AppVersion={updateInfo.AppVersion} 与当前整包 {Application.version} 不一致。");
            if (!UpdateSecurity.ValidateCompleteCodePatchSet(
                    updateInfo.PatchFiles,
                    AppConfig.Load()?.AppEnv,
                    out string patchSetError))
            {
                throw new InvalidDataException(patchSetError);
            }

            lock (Sync)
            {
                EnsureDirectories();
                string slotId = BuildSlotId(updateInfo);
                string staging = Path.Combine(StagingDirectory, slotId);
                if (Directory.Exists(staging))
                    Directory.Delete(staging, true);
                Directory.CreateDirectory(staging);
                return staging;
            }
        }

        /// <summary>
        /// 在指定 staging 根目录内解析安全文件路径。除校验叶子文件名外，还会比较规范化绝对路径，
        /// 双重阻止目录穿越、盘符注入以及利用分隔符逃逸受控目录。
        /// </summary>
        /// <param name="stagingDirectory">受控 staging 根目录。</param>
        /// <param name="fileName">清单声明的程序集叶子文件名。</param>
        /// <returns>确认仍位于 staging 根目录下的规范化绝对路径。</returns>
        public static string GetSafeStagingFilePath(string stagingDirectory, string fileName)
        {
            if (!UpdateSecurity.IsSafeLeafFileName(fileName))
                throw new InvalidDataException($"补丁文件名不安全：{fileName}");

            string root = Path.GetFullPath(stagingDirectory)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
            string candidate = Path.GetFullPath(Path.Combine(stagingDirectory, fileName));
            if (!candidate.StartsWith(root, PathComparison))
                throw new InvalidDataException($"补丁路径逃逸 staging 根目录：{fileName}");
            return candidate;
        }

        /// <summary>
        /// 将 staging 目录作为一个事务提交：先复验完整文件集并写入槽清单，再移动为不可变正式槽，
        /// 最后原子更新 ActiveSlot 与 PendingConfirmationSlot。提交只代表“可尝试启动”，不会提前提升为 LKG。
        /// </summary>
        /// <param name="updateInfo">本次代码更新清单。</param>
        /// <param name="stagingDirectory">已完成下载的 staging 目录。</param>
        /// <param name="error">失败时返回安装或校验原因。</param>
        /// <returns>槽目录和状态均提交成功时返回 true。</returns>
        public static bool CommitStagingSlot(UpdateInfo updateInfo, string stagingDirectory, out string error)
        {
            error = null;
            if (updateInfo == null || string.IsNullOrEmpty(stagingDirectory))
            {
                error = "更新清单或 staging 目录为空。";
                return false;
            }

            lock (Sync)
            {
                try
                {
                    if (!string.Equals(updateInfo.AppVersion, Application.version, StringComparison.Ordinal))
                        throw new InvalidDataException($"代码补丁 AppVersion={updateInfo.AppVersion} 与当前整包 {Application.version} 不一致。");
                    if (!UpdateSecurity.ValidateCompleteCodePatchSet(
                            updateInfo.PatchFiles,
                            AppConfig.Load()?.AppEnv,
                            out string patchSetError))
                    {
                        throw new InvalidDataException(patchSetError);
                    }

                    string slotId = BuildSlotId(updateInfo);
                    ValidateStagingDirectory(stagingDirectory, slotId);
                    ValidateStagingFileSet(stagingDirectory, updateInfo.PatchFiles);
                    foreach (PatchFile patch in updateInfo.PatchFiles)
                    {
                        string path = GetSafeStagingFilePath(stagingDirectory, patch.FileName);
                        if (!FileVerifier.VerifyPatchFile(path, patch, out string verifyError))
                            throw new InvalidDataException(verifyError);
                    }

                    var manifest = new SlotManifest
                    {
                        SlotId = slotId,
                        AppVersion = Application.version,
                        CodeVersion = updateInfo.CodeVersion,
                        InstalledAtUnixSeconds = Now(),
                        Files = updateInfo.PatchFiles.Select(ClonePatch).ToList(),
                    };
                    FileStorages.Shared.AtomicWriteText(
                        Path.Combine(stagingDirectory, SlotManifestFileName),
                        JsonSerializers.Shared.ToJson(manifest, true));

                    string finalDirectory = Path.Combine(SlotsDirectory, slotId);
                    if (Directory.Exists(finalDirectory))
                    {
                        if (ValidateSlotDirectory(finalDirectory, out _, out _))
                        {
                            Directory.Delete(stagingDirectory, true);
                        }
                        else
                        {
                            if (IsProtectedSlot(slotId))
                                throw new IOException($"目标槽同时受 Active/LKG 状态保护，禁止覆盖：{slotId}");
                            Directory.Delete(finalDirectory, true);
                            Directory.Move(stagingDirectory, finalDirectory);
                        }
                    }
                    else
                    {
                        Directory.Move(stagingDirectory, finalDirectory);
                    }

                    InstallState state = CurrentState();
                    if (!string.Equals(state.AppVersion, Application.version, StringComparison.Ordinal))
                        state = NewState();

                    if (!string.IsNullOrEmpty(state.ActiveSlot) &&
                        string.IsNullOrEmpty(state.PendingConfirmationSlot) &&
                        ValidateSlot(state.ActiveSlot, out _))
                    {
                        state.LastKnownGoodSlot = state.ActiveSlot;
                    }

                    state.AppVersion = Application.version;
                    state.ActiveSlot = slotId;
                    state.PendingConfirmationSlot = slotId;
                    state.UpdatedAtUnixSeconds = Now();
                    SaveState(state);
                    _state = state;

                    GameLog.Log($"[HotUpdateSlots] 已激活待确认槽={slotId}，当前 LKG={state.LastKnownGoodSlot}。");
                    return true;
                }
                catch (Exception ex)
                {
                    error = ex.Message;
                    GameLog.Error($"[HotUpdateSlots] 提交 staging 槽失败：{ex}");
                    return false;
                }
            }
        }

        /// <summary>
        /// 在 HotfixEntry.Start 成功返回后确认当前 Pending 槽，并将其提升为 Last-Known-Good。
        /// 若 Pending 与 Active 不一致或槽完整性复验失败，则抛出异常，绝不写入虚假的成功状态。
        /// </summary>
        public static void ConfirmPendingSlot()
        {
            lock (Sync)
            {
                InstallState state = CurrentState();
                if (string.IsNullOrEmpty(state.PendingConfirmationSlot))
                    return;

                string reason = null;
                if (!string.Equals(state.PendingConfirmationSlot, state.ActiveSlot, StringComparison.Ordinal) ||
                    !ValidateSlot(state.ActiveSlot, out reason))
                {
                    throw new InvalidDataException($"待确认热更新槽无效：{reason ?? "Pending 与 Active 状态不一致"}");
                }

                state.LastKnownGoodSlot = state.ActiveSlot;
                state.PendingConfirmationSlot = string.Empty;
                state.UpdatedAtUnixSeconds = Now();
                SaveState(state);
                _state = state;
                CleanupObsoleteSlots(state);
                GameLog.Log($"[HotUpdateSlots] 热更新槽已确认并提升为 LKG：{state.ActiveSlot}");
            }
        }

        /// <summary>
        /// 将当前 Pending 槽标记为启动失败，并立即把持久化 Active 指针回滚到上一 LKG。
        /// 当前进程已加载的程序集无法从 AppDomain 卸载，因此调用方仍应结束本次启动或重启进程，不能在同一进程内假装完成代码回滚。
        /// </summary>
        /// <param name="reason">用于诊断和崩溃关联的失败原因。</param>
        public static void MarkPendingSlotFailed(string reason)
        {
            lock (Sync)
            {
                InstallState state = CurrentState();
                if (string.IsNullOrEmpty(state.PendingConfirmationSlot))
                    return;

                string failed = state.PendingConfirmationSlot;
                state.ActiveSlot = ValidateSlot(state.LastKnownGoodSlot, out _)
                    ? state.LastKnownGoodSlot
                    : string.Empty;
                state.PendingConfirmationSlot = string.Empty;
                state.UpdatedAtUnixSeconds = Now();
                SaveState(state);
                _state = state;
                CleanupObsoleteSlots(state);
                GameLog.Error($"[HotUpdateSlots] 待确认槽 {failed} 已标记失败（{reason}），回滚目标={state.ActiveSlot}。");
            }
        }

        /// <summary>
        /// 仅从当前已验证的 ActiveSlot 解析程序集路径。未命中时由调用方显式回退到 StreamingAssets 基线，
        /// 本方法不会扫描 persistentDataPath 其他位置，从根源上阻止散装或残留 DLL 被意外加载。
        /// </summary>
        /// <param name="fileName">配置白名单中的程序集叶子文件名。</param>
        /// <param name="path">成功时返回活动槽内的绝对文件路径。</param>
        public static bool TryResolveActiveFile(string fileName, out string path)
        {
            path = null;
            lock (Sync)
            {
                if (!UpdateSecurity.IsSafeLeafFileName(fileName))
                    return false;

                InstallState state = CurrentState();
                if (string.IsNullOrEmpty(state.ActiveSlot) || !ValidateSlot(state.ActiveSlot, out _))
                    return false;

                string candidate = Path.Combine(SlotsDirectory, state.ActiveSlot, fileName);
                if (!File.Exists(candidate))
                    return false;

                path = candidate;
                return true;
            }
        }

        /// <summary>
        /// 从当前已验证 ActiveSlot 的槽清单读取真实代码版本。代码版本事实不再来自可独立漂移的 persistent/version.json。
        /// </summary>
        /// <param name="codeVersion">成功时返回活动槽代码版本。</param>
        /// <returns>存在可验证活动槽及有效清单时返回 true。</returns>
        public static bool TryGetActiveCodeVersion(out int codeVersion)
        {
            codeVersion = 0;
            lock (Sync)
            {
                InstallState state = CurrentState();
                if (string.IsNullOrEmpty(state.ActiveSlot) ||
                    !ValidateSlot(state.ActiveSlot, out _) ||
                    !TryReadSlotManifest(state.ActiveSlot, out SlotManifest manifest))
                {
                    return false;
                }

                codeVersion = manifest.CodeVersion;
                return true;
            }
        }

        private static InstallState CurrentState()
        {
            if (_state == null)
                PrepareForLaunch();
            return _state ?? NewState();
        }

        private static InstallState LoadState()
        {
            if (!File.Exists(StatePath))
                return NewState();

            try
            {
                InstallState state = JsonSerializers.Shared.FromJson<InstallState>(File.ReadAllText(StatePath));
                return state ?? NewState();
            }
            catch (Exception ex)
            {
                GameLog.Error($"[HotUpdateSlots] 安装状态文件无效，已重置：{ex.Message}");
                return NewState();
            }
        }

        private static InstallState NewState() => new InstallState
        {
            AppVersion = Application.version,
            UpdatedAtUnixSeconds = Now(),
        };

        private static void SaveState(InstallState state)
        {
            state.AppVersion = Application.version;
            FileStorages.Shared.AtomicWriteText(StatePath, JsonSerializers.Shared.ToJson(state, true), StatePath + ".bak");
        }

        private static bool ValidateSlot(string slotId, out string reason)
        {
            reason = null;
            if (string.IsNullOrEmpty(slotId))
            {
                reason = "槽 ID 为空。";
                return false;
            }
            return ValidateSlotDirectory(Path.Combine(SlotsDirectory, slotId), out _, out reason);
        }

        private static bool ValidateSlotDirectory(string directory, out SlotManifest manifest, out string reason)
        {
            manifest = null;
            reason = null;
            try
            {
                string manifestPath = Path.Combine(directory, SlotManifestFileName);
                if (!File.Exists(manifestPath))
                    throw new FileNotFoundException("缺少槽清单。", manifestPath);

                manifest = JsonSerializers.Shared.FromJson<SlotManifest>(File.ReadAllText(manifestPath));
                if (manifest == null)
                    throw new InvalidDataException("槽清单无法反序列化。 ");
                if (!string.Equals(manifest.AppVersion, Application.version, StringComparison.Ordinal))
                    throw new InvalidDataException($"槽整包版本 {manifest.AppVersion} 与当前 {Application.version} 不一致。");
                if (manifest.CodeVersion < 1)
                    throw new InvalidDataException("槽清单代码版本号小于 1。");
                string directoryName = Path.GetFileName(
                    directory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
                if (!string.Equals(manifest.SlotId, directoryName, StringComparison.Ordinal))
                    throw new InvalidDataException($"槽清单 SlotId={manifest.SlotId} 与目录名 {directoryName} 不一致。");
                if (!UpdateSecurity.ValidateCompleteCodePatchSet(manifest.Files, appEnv: null, out string patchSetError))
                    throw new InvalidDataException(patchSetError);

                ValidateCommittedFileSet(directory, manifest.Files);
                foreach (PatchFile patch in manifest.Files)
                {
                    string file = GetSafeStagingFilePath(directory, patch.FileName);
                    if (!FileVerifier.VerifyPatchFile(file, patch, out string verifyError))
                        throw new InvalidDataException(verifyError);
                }
                return true;
            }
            catch (Exception ex)
            {
                reason = ex.Message;
                return false;
            }
        }

        private static bool TryReadSlotManifest(string slotId, out SlotManifest manifest)
        {
            return ValidateSlotDirectory(Path.Combine(SlotsDirectory, slotId), out manifest, out _);
        }

        private static string BuildSlotId(UpdateInfo updateInfo)
        {
            var canonical = new StringBuilder();
            canonical.Append(updateInfo.AppVersion).Append('|').Append(updateInfo.CodeVersion);
            foreach (PatchFile patch in updateInfo.PatchFiles.OrderBy(file => file.FileName, StringComparer.Ordinal))
            {
                canonical.Append('|').Append(patch.FileName)
                    .Append(':').Append(patch.Size)
                    .Append(':').Append(patch.SHA256);
            }

            string digest;
            using (SHA256 sha = SHA256.Create())
                digest = ToHex(sha.ComputeHash(Encoding.UTF8.GetBytes(canonical.ToString()))).Substring(0, 16);

            string app = SanitizeSegment(updateInfo.AppVersion);
            return $"app_{app}_code_{updateInfo.CodeVersion}_{digest}";
        }

        private static string SanitizeSegment(string value)
        {
            if (string.IsNullOrEmpty(value)) return "unknown";
            var builder = new StringBuilder(value.Length);
            foreach (char c in value)
                builder.Append(char.IsLetterOrDigit(c) || c == '.' || c == '-' || c == '_' ? c : '_');
            return builder.ToString();
        }

        private static PatchFile ClonePatch(PatchFile patch) => new PatchFile
        {
            FileName = patch.FileName,
            Url = patch.Url,
            Size = patch.Size,
            SHA256 = patch.SHA256,
            MD5 = patch.MD5,
        };

        private static bool IsProtectedSlot(string slotId)
        {
            InstallState state = CurrentState();
            return string.Equals(slotId, state.ActiveSlot, StringComparison.Ordinal) ||
                   string.Equals(slotId, state.LastKnownGoodSlot, StringComparison.Ordinal);
        }

        /// <summary>
        /// 确认提交入口只能接收由 <see cref="PrepareStagingSlot"/> 创建的预期直接子目录，禁止移动任意外部目录。
        /// </summary>
        private static void ValidateStagingDirectory(string stagingDirectory, string slotId)
        {
            string expected = Path.GetFullPath(Path.Combine(StagingDirectory, slotId))
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            string actual = Path.GetFullPath(stagingDirectory)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (!string.Equals(expected, actual, PathComparison))
                throw new InvalidDataException($"staging 目录不属于预期事务槽：expected={expected}, actual={actual}");
            if (!Directory.Exists(actual))
                throw new DirectoryNotFoundException($"staging 目录不存在：{actual}");
        }

        /// <summary>
        /// 提交前确认 staging 中只有清单声明的程序集文件，不允许夹带未签名文件、旧下载残留或额外载荷。
        /// </summary>
        private static void ValidateStagingFileSet(string directory, IReadOnlyList<PatchFile> patches)
        {
            var expected = new HashSet<string>(patches.Select(patch => patch.FileName), StringComparer.Ordinal);
            string[] actualFiles = Directory.GetFiles(directory, "*", SearchOption.TopDirectoryOnly);
            foreach (string file in actualFiles)
            {
                string fileName = Path.GetFileName(file);
                if (!expected.Remove(fileName))
                    throw new InvalidDataException($"staging 目录包含清单外文件：{fileName}");
            }
            if (expected.Count > 0)
                throw new InvalidDataException($"staging 目录缺少清单文件：{string.Join(",", expected)}");
            if (Directory.GetDirectories(directory, "*", SearchOption.TopDirectoryOnly).Length > 0)
                throw new InvalidDataException("staging 目录不允许包含子目录。");
        }

        /// <summary>
        /// 启动复验时确认正式槽只包含完整程序集集合和 slot.json，防止槽目录在提交后被追加非清单文件。
        /// </summary>
        private static void ValidateCommittedFileSet(string directory, IReadOnlyList<PatchFile> patches)
        {
            var expected = new HashSet<string>(patches.Select(patch => patch.FileName), StringComparer.Ordinal)
            {
                SlotManifestFileName,
            };
            foreach (string file in Directory.GetFiles(directory, "*", SearchOption.TopDirectoryOnly))
            {
                string fileName = Path.GetFileName(file);
                if (!expected.Remove(fileName))
                    throw new InvalidDataException($"正式代码槽包含清单外文件：{fileName}");
            }
            if (expected.Count > 0)
                throw new InvalidDataException($"正式代码槽缺少必需文件：{string.Join(",", expected)}");
            if (Directory.GetDirectories(directory, "*", SearchOption.TopDirectoryOnly).Length > 0)
                throw new InvalidDataException("正式代码槽不允许包含子目录。");
        }

        private static void EnsureDirectories()
        {
            Directory.CreateDirectory(RootDirectory);
            Directory.CreateDirectory(SlotsDirectory);
            Directory.CreateDirectory(StagingDirectory);
        }

        /// <summary>
        /// 删除不再被 Active、Pending 或 Last-Known-Good 引用的旧代码槽，避免长期运营过程中持久化目录无限增长。
        /// 删除目标只来自 SlotsDirectory 的直接子目录，并再次校验规范化父路径，禁止越界递归删除。
        /// </summary>
        private static void CleanupObsoleteSlots(InstallState state)
        {
            if (!Directory.Exists(SlotsDirectory)) return;
            var protectedSlots = new HashSet<string>(StringComparer.Ordinal)
            {
                state?.ActiveSlot ?? string.Empty,
                state?.LastKnownGoodSlot ?? string.Empty,
                state?.PendingConfirmationSlot ?? string.Empty,
            };
            string root = Path.GetFullPath(SlotsDirectory)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
            foreach (string directory in Directory.GetDirectories(SlotsDirectory, "*", SearchOption.TopDirectoryOnly))
            {
                string fullPath = Path.GetFullPath(directory);
                string slotId = Path.GetFileName(fullPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
                if (protectedSlots.Contains(slotId)) continue;
                if (!fullPath.StartsWith(root, PathComparison))
                {
                    GameLog.Error($"[HotUpdateSlots] 拒绝清理越界代码槽路径：{fullPath}");
                    continue;
                }
                try { Directory.Delete(fullPath, true); }
                catch (Exception ex)
                {
                    GameLog.Warning($"[HotUpdateSlots] 清理旧代码槽失败 {fullPath}：{ex.Message}");
                }
            }
        }

        private static void CleanupStagingDirectories()
        {
            if (!Directory.Exists(StagingDirectory)) return;
            foreach (string directory in Directory.GetDirectories(StagingDirectory))
            {
                try { Directory.Delete(directory, true); }
                catch (Exception ex) { GameLog.Warning($"[HotUpdateSlots] 清理 staging 目录失败 {directory}：{ex.Message}"); }
            }
        }

        private static string ToHex(byte[] hash)
        {
            var builder = new StringBuilder(hash.Length * 2);
            foreach (byte value in hash) builder.Append(value.ToString("x2"));
            return builder.ToString();
        }

        private static long Now() => DateTimeOffset.UtcNow.ToUnixTimeSeconds();
    }
}
