using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Framework.HotUpdate;
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
