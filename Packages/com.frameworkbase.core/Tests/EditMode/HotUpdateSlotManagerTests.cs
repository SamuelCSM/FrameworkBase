using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Framework.HotUpdate;
using Framework.Storage;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace Framework.Tests
{
    /// <summary>
    /// 热更新代码槽事务测试，验证“提交待确认 → 启动确认成为 LKG → 未确认重启自动回滚”的核心状态机。
    /// 测试使用独立临时根目录，不接触开发者真实 persistentDataPath。
    /// </summary>
    public class HotUpdateSlotManagerTests
    {
        private string _root;

        [SetUp]
        public void SetUp()
        {
            _root = Path.Combine(Path.GetTempPath(), "FrameworkBase-SlotTests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_root);
            HotUpdateSlotManager.TestRootDirectoryOverride = _root;
            HotUpdateSlotManager.ResetStateForTests();
            HotUpdateSlotManager.PrepareForLaunch();
        }

        [TearDown]
        public void TearDown()
        {
            HotUpdateSlotManager.ResetStateForTests();
            HotUpdateSlotManager.TestRootDirectoryOverride = null;
            if (Directory.Exists(_root)) Directory.Delete(_root, true);
        }

        [Test]
        public void 已确认槽成为LKG_下一待确认槽重启时自动回滚()
        {
            UpdateInfo version2 = CreateUpdate(2, 0x22);
            Commit(version2);
            HotUpdateSlotManager.ConfirmPendingSlot();
            Assert.IsTrue(HotUpdateSlotManager.TryGetActiveCodeVersion(out int activeV2));
            Assert.AreEqual(2, activeV2);

            UpdateInfo version3 = CreateUpdate(3, 0x33);
            Commit(version3);
            Assert.IsTrue(HotUpdateSlotManager.TryGetActiveCodeVersion(out int pendingV3));
            Assert.AreEqual(3, pendingV3);

            // 模拟进程在 HotfixEntry.Start 确认前被杀：清空内存状态，再从磁盘执行启动准备。
            HotUpdateSlotManager.ResetStateForTests();
            LogAssert.Expect(LogType.Error, new Regex(".*检测到上次未确认槽.*已回滚到.*"));
            HotUpdateSlotManager.PrepareForLaunch();

            Assert.IsTrue(HotUpdateSlotManager.TryGetActiveCodeVersion(out int rolledBack));
            Assert.AreEqual(2, rolledBack);
        }

        [Test]
        public void 显式标记待确认槽失败_立即回滚到LKG()
        {
            Commit(CreateUpdate(2, 0x22));
            HotUpdateSlotManager.ConfirmPendingSlot();
            Commit(CreateUpdate(3, 0x33));

            LogAssert.Expect(LogType.Error, new Regex(".*待确认槽.*已标记失败.*"));
            HotUpdateSlotManager.MarkPendingSlotFailed("测试启动失败");

            Assert.IsTrue(HotUpdateSlotManager.TryGetActiveCodeVersion(out int codeVersion));
            Assert.AreEqual(2, codeVersion);
        }

        [Test]
        public void 确认提交重放_新进程直接前滚Pending槽_不得先执行普通启动回滚()
        {
            Commit(CreateUpdate(2, 0x22));
            HotUpdateSlotManager.ConfirmPendingSlot();
            UpdateInfo version3 = CreateUpdate(3, 0x33);
            Commit(version3);
            var committing = new ContentReleaseRecord
            {
                ReleaseId = version3.ManifestId,
                AppVersion = version3.AppVersion,
                ResourceVersion = version3.ResourceVersion,
                CodeVersion = version3.CodeVersion,
                ResourceChanged = false,
                CodeChanged = true,
            };

            // 模拟确认提交日志已经落盘后进程退出：新进程静态内存状态为空。
            // 专用重放入口必须直接读盘确认 v3；若内部误走 CurrentState→PrepareForLaunch，v3 会先被回滚到 v2。
            HotUpdateSlotManager.ResetStateForTests();
            HotUpdateSlotManager.ReplayPendingConfirmationForCommit(committing);

            Assert.IsTrue(HotUpdateSlotManager.TryGetActiveCodeVersion(out int replayed));
            Assert.AreEqual(3, replayed, "确认提交重放必须前滚新代码槽，不能回滚到旧 LKG");

            // 再次重放必须幂等，不得误伤已经确认的活动槽。
            HotUpdateSlotManager.ResetStateForTests();
            HotUpdateSlotManager.ReplayPendingConfirmationForCommit(committing);
            Assert.IsTrue(HotUpdateSlotManager.TryGetActiveCodeVersion(out int replayedAgain));
            Assert.AreEqual(3, replayedAgain);
        }

        [Test]
        public void Staging夹带清单外文件_拒绝提交()
        {
            UpdateInfo update = CreateUpdate(2, 0x44);
            string staging = PrepareFiles(update);
            File.WriteAllText(Path.Combine(staging, "unexpected.bin"), "unexpected");

            LogAssert.Expect(LogType.Error, new Regex(".*提交 staging 槽失败.*清单外文件.*"));
            Assert.IsFalse(HotUpdateSlotManager.CommitStagingSlot(update, staging, out string error));
            StringAssert.Contains("清单外文件", error);
        }

        [Test]
        public void 不完整程序集快照_创建Staging前即拒绝()
        {
            UpdateInfo update = CreateUpdate(2, 0x55);
            update.PatchFiles.RemoveAt(0);
            Assert.Throws<InvalidDataException>(() => HotUpdateSlotManager.PrepareStagingSlot(update));
        }

        [Test]
        public void 已确认槽连续启动未确认_超过阈值回退出厂基线()
        {
            Commit(CreateUpdate(2, 0x22));
            HotUpdateSlotManager.ConfirmPendingSlot();

            // 已确认槽（LKG）连续 3 次带槽启动都没有到达确认点：模拟启动早期崩溃循环。
            for (int attempt = 0; attempt < 3; attempt++)
            {
                HotUpdateSlotManager.ResetStateForTests();
                HotUpdateSlotManager.PrepareForLaunch();
                Assert.IsTrue(HotUpdateSlotManager.TryGetActiveCodeVersion(out int stillActive),
                    $"第 {attempt + 1} 次未确认启动仍应保留活动槽");
                Assert.AreEqual(2, stillActive);
            }

            // 第 4 次启动触发崩溃循环兜底：清空全部槽，回退整包出厂基线。
            HotUpdateSlotManager.ResetStateForTests();
            LogAssert.Expect(LogType.Error, new Regex(".*判定为崩溃循环.*回退整包出厂基线.*"));
            HotUpdateSlotManager.PrepareForLaunch();
            Assert.IsFalse(HotUpdateSlotManager.TryGetActiveCodeVersion(out _));
        }

        [Test]
        public void 启动确认成功_崩溃循环计数清零不误伤()
        {
            Commit(CreateUpdate(2, 0x22));
            HotUpdateSlotManager.ConfirmPendingSlot();

            // 交替"重启 + 启动确认"多轮：每次确认都会清零计数，永远不应触发出厂回退。
            for (int round = 0; round < 5; round++)
            {
                HotUpdateSlotManager.ResetStateForTests();
                HotUpdateSlotManager.PrepareForLaunch();
                HotUpdateSlotManager.ConfirmPendingSlot();
            }

            Assert.IsTrue(HotUpdateSlotManager.TryGetActiveCodeVersion(out int codeVersion));
            Assert.AreEqual(2, codeVersion);
        }

        [Test]
        public void 缓存清理_保护活动与LKG_删除孤儿Staging和历史槽()
        {
            Commit(CreateUpdate(2, 0x22));
            HotUpdateSlotManager.ConfirmPendingSlot();
            string active = Directory.GetDirectories(Path.Combine(_root, "slots"))[0];

            string orphanSlot = Path.Combine(_root, "slots", "orphan-slot");
            string orphanStaging = Path.Combine(_root, "staging", "orphan-staging");
            Directory.CreateDirectory(orphanSlot);
            Directory.CreateDirectory(orphanStaging);
            File.WriteAllBytes(Path.Combine(orphanSlot, "old.bin"), new byte[128]);
            File.WriteAllBytes(Path.Combine(orphanStaging, "partial.download"), new byte[64]);

            CacheCleanupReport report = HotUpdateSlotManager.CleanupCache(
                new CacheCleanupRequest(0, requiredFreeBytes: 1024, availableFreeBytes: 0),
                new CacheRetentionPolicy { MaxCacheBytes = 1024, HighWatermarkRatio = 0.9, LowWatermarkRatio = 0.7 });

            Assert.IsTrue(Directory.Exists(active), "Active/LKG 代码槽属于硬保护集");
            Assert.IsFalse(Directory.Exists(orphanSlot));
            Assert.IsFalse(Directory.Exists(orphanStaging));
            Assert.AreEqual(2, report.DeletedEntries);
            Assert.GreaterOrEqual(report.FreedBytes, 192);
        }

        [Test]
        public void 缓存清理_内容提交进行中不删除任何条目()
        {
            string orphan = Path.Combine(_root, "staging", "commit-owned");
            Directory.CreateDirectory(orphan);
            File.WriteAllBytes(Path.Combine(orphan, "partial.download"), new byte[64]);

            CacheCleanupReport report = HotUpdateSlotManager.CleanupCache(
                new CacheCleanupRequest(0, requiredFreeBytes: 1024, availableFreeBytes: 0),
                new CacheRetentionPolicy(),
                contentCommitInProgress: true);

            Assert.IsTrue(Directory.Exists(orphan));
            Assert.AreEqual(0, report.DeletedEntries);
        }

        /// <summary>
        /// 创建 staging、写入所有清单文件并提交为待确认槽。
        /// </summary>
        private static void Commit(UpdateInfo update)
        {
            string staging = PrepareFiles(update);
            Assert.IsTrue(HotUpdateSlotManager.CommitStagingSlot(update, staging, out string error), error);
        }

        /// <summary>
        /// 按清单写入确定性字节内容，供槽提交阶段执行真实 Size + SHA-256 复验。
        /// </summary>
        private static string PrepareFiles(UpdateInfo update)
        {
            string staging = HotUpdateSlotManager.PrepareStagingSlot(update);
            for (int i = 0; i < update.PatchFiles.Count; i++)
            {
                PatchFile patch = update.PatchFiles[i];
                byte[] bytes = CreatePayload(update.CodeVersion, i);
                File.WriteAllBytes(HotUpdateSlotManager.GetSafeStagingFilePath(staging, patch.FileName), bytes);
            }
            return staging;
        }

        /// <summary>
        /// 创建与当前整包版本一致、文件集合完整的测试更新清单。
        /// </summary>
        private static UpdateInfo CreateUpdate(int codeVersion, byte seed)
        {
            var patches = new List<PatchFile>();
            string[] files = VersionManager.HotUpdateAssemblyFileNames;
            for (int i = 0; i < files.Length; i++)
            {
                byte[] payload = CreatePayload(codeVersion, i);
                patches.Add(new PatchFile
                {
                    FileName = files[i],
                    Url = $"https://cdn.example.com/Updates/{files[i]}",
                    Size = payload.Length,
                    SHA256 = ComputeSha256(payload),
                });
            }

            return new UpdateInfo
            {
                ManifestVersion = FrameworkRuntimeInfo.UpdateManifestVersion,
                ManifestId = Guid.NewGuid().ToString("D"),
                KeyId = "test-key",
                AppVersion = Application.version,
                ResourceVersion = 1,
                CodeVersion = codeVersion,
                PatchFiles = patches,
                Description = seed.ToString(),
            };
        }

        private static byte[] CreatePayload(int codeVersion, int index) =>
            new[] { (byte)codeVersion, (byte)index, (byte)0xA5, (byte)0x5A };

        private static string ComputeSha256(byte[] bytes)
        {
            using (SHA256 sha = SHA256.Create())
            {
                return BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", string.Empty).ToLowerInvariant();
            }
        }
    }
}
