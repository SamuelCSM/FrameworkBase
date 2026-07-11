using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEngine;

namespace Framework.Editor.Release
{
    /// <summary>
    /// 从产物仓库派生的 release 展示状态。
    /// <para>
    /// 注意：这是从指针与台账<b>推导</b>的视图状态，不是持久化状态机事件流
    /// （append-only 迁移事件为目标设计 §4 的后续项）。Active/Previous 可精确判定；
    /// 其余完整目录统一显示 Archived（均为合法回滚/晋级目标）。
    /// </para>
    /// </summary>
    public enum ReleaseDisplayState
    {
        /// <summary>current.json 指针当前指向。</summary>
        Active,
        /// <summary>指针历史链上一跳（PreviousReleaseId）。</summary>
        Previous,
        /// <summary>完整的历史版本目录，可作为回滚/晋级目标。</summary>
        Archived,
        /// <summary>目录存在但正本清单或台账缺失，不可作为任何操作目标。</summary>
        Incomplete,
    }

    /// <summary>产物仓库中单个不可变 release 目录的视图信息（台账 + 指针推导）。</summary>
    [Serializable]
    public sealed class ReleaseEntryView
    {
        public string ReleaseId;
        public string AppVersion;
        public int ResourceVersion;
        public int CodeVersion;
        public string GitCommit;
        public string GeneratedAtUtc;
        public string Environment;
        public ReleaseDisplayState State;
        /// <summary>相对渠道根的版本目录路径（releases/{app}/{rid}）。</summary>
        public string DirRelative;
        public string LedgerPath;
        public string ManifestPath;
    }

    /// <summary>单个 {env}/{platform}/{channel} 渠道作用域的完整快照。</summary>
    public sealed class ChannelSnapshot
    {
        /// <summary>作用域相对路径（env/platform/channel）。</summary>
        public string Scope;
        /// <summary>渠道根绝对路径。</summary>
        public string RootAbsolute;
        /// <summary>当前指针；渠道尚未切过指针时为 null。</summary>
        public Framework.HotUpdate.CurrentPointer Pointer;
        /// <summary>按生成时间倒序的 release 列表。</summary>
        public List<ReleaseEntryView> Releases = new List<ReleaseEntryView>();
    }

    /// <summary>
    /// Release Center 的仓库读取层：扫描产物仓库根，产出渠道快照供面板展示。
    /// 纯读取、无 UI 依赖，可被 EditMode 测试直接驱动——面板只做编排与展示（铁律）。
    /// </summary>
    public static class ReleaseRepositoryScanner
    {
        /// <summary>台账展示所需的最小字段子集（与 ReleasePublishingSteps 的台账契约兼容解析）。</summary>
        [Serializable]
        private sealed class LedgerView
        {
            public string ReleaseId;
            public string GeneratedAtUtc;
            public string GitCommit;
            public string Environment;
            public string AppVersion;
            public int ResourceVersion;
            public int CodeVersion;
        }

        /// <summary>
        /// 枚举仓库根下所有渠道作用域（{env}/{platform}/{channel}）：
        /// 以"目录内存在 releases/ 子目录或 current.json"为渠道根判据。
        /// </summary>
        public static List<string> ScanScopes(string uploadRoot)
        {
            var scopes = new List<string>();
            if (string.IsNullOrWhiteSpace(uploadRoot) || !Directory.Exists(uploadRoot))
                return scopes;

            foreach (string envDir in Directory.GetDirectories(uploadRoot))
            foreach (string platformDir in Directory.GetDirectories(envDir))
            foreach (string channelDir in Directory.GetDirectories(platformDir))
            {
                if (Directory.Exists(Path.Combine(channelDir, "releases")) ||
                    File.Exists(Path.Combine(channelDir, "current.json")))
                {
                    scopes.Add(string.Join("/",
                        Path.GetFileName(envDir),
                        Path.GetFileName(platformDir),
                        Path.GetFileName(channelDir)));
                }
            }
            scopes.Sort(StringComparer.Ordinal);
            return scopes;
        }

        /// <summary>读取单个渠道作用域的指针与全部 release 视图（按生成时间倒序）。</summary>
        public static ChannelSnapshot LoadChannel(string uploadRoot, string scope)
        {
            var snapshot = new ChannelSnapshot
            {
                Scope = scope,
                RootAbsolute = Path.GetFullPath(Path.Combine(
                    uploadRoot, scope.Replace('/', Path.DirectorySeparatorChar))),
            };

            string pointerPath = Path.Combine(snapshot.RootAbsolute, "current.json");
            if (File.Exists(pointerPath))
            {
                try
                {
                    snapshot.Pointer = JsonUtility.FromJson<Framework.HotUpdate.CurrentPointer>(
                        File.ReadAllText(pointerPath));
                }
                catch (Exception ex)
                {
                    Debug.LogWarning($"[ReleaseCenter] 指针解析失败（{pointerPath}）：{ex.Message}");
                }
            }

            string releasesRoot = Path.Combine(snapshot.RootAbsolute, "releases");
            if (Directory.Exists(releasesRoot))
            {
                foreach (string appDir in Directory.GetDirectories(releasesRoot))
                foreach (string releaseDir in Directory.GetDirectories(appDir))
                    snapshot.Releases.Add(LoadEntry(snapshot, appDir, releaseDir));
            }

            snapshot.Releases = snapshot.Releases
                .OrderByDescending(entry => entry.GeneratedAtUtc, StringComparer.Ordinal)
                .ToList();
            return snapshot;
        }

        private static ReleaseEntryView LoadEntry(ChannelSnapshot snapshot, string appDir, string releaseDir)
        {
            string releaseId = Path.GetFileName(releaseDir);
            var entry = new ReleaseEntryView
            {
                ReleaseId = releaseId,
                DirRelative = $"releases/{Path.GetFileName(appDir)}/{releaseId}",
                LedgerPath = Path.Combine(releaseDir, "ledger.json"),
                ManifestPath = Path.Combine(releaseDir, "version.json"),
            };

            bool complete = File.Exists(entry.ManifestPath) &&
                            File.Exists(entry.ManifestPath + ".sig") &&
                            File.Exists(entry.LedgerPath);
            if (complete)
            {
                try
                {
                    LedgerView ledger = JsonUtility.FromJson<LedgerView>(File.ReadAllText(entry.LedgerPath));
                    entry.AppVersion = ledger?.AppVersion;
                    entry.ResourceVersion = ledger?.ResourceVersion ?? 0;
                    entry.CodeVersion = ledger?.CodeVersion ?? 0;
                    entry.GitCommit = ledger?.GitCommit;
                    entry.GeneratedAtUtc = ledger?.GeneratedAtUtc;
                    entry.Environment = ledger?.Environment;
                }
                catch (Exception ex)
                {
                    Debug.LogWarning($"[ReleaseCenter] 台账解析失败（{entry.LedgerPath}）：{ex.Message}");
                    complete = false;
                }
            }

            entry.State = DeriveState(snapshot.Pointer, releaseId, complete);
            return entry;
        }

        /// <summary>指针推导视图状态：Active（指针指向）→ Previous（历史链上一跳）→ Archived / Incomplete。</summary>
        internal static ReleaseDisplayState DeriveState(
            Framework.HotUpdate.CurrentPointer pointer,
            string releaseId,
            bool complete)
        {
            if (!complete) return ReleaseDisplayState.Incomplete;
            if (pointer != null && string.Equals(pointer.ReleaseId, releaseId, StringComparison.Ordinal))
                return ReleaseDisplayState.Active;
            if (pointer != null && string.Equals(pointer.PreviousReleaseId, releaseId, StringComparison.Ordinal))
                return ReleaseDisplayState.Previous;
            return ReleaseDisplayState.Archived;
        }
    }
}
