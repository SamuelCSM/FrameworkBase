using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using Cysharp.Threading.Tasks;
using Framework.HotUpdate;
using Framework.Storage;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace Framework.Tests
{
    public class StorageCapacityTests
    {
        private static T Wait<T>(UniTask<T> task) => task.GetAwaiter().GetResult();

        private sealed class FakeProvider : IStorageCapacityProvider
        {
            public StorageVolumeSnapshot Snapshot;
            public string LastPath;

            public StorageVolumeSnapshot Query(string path)
            {
                LastPath = path;
                return Snapshot;
            }
        }

        private static StorageBudgetPolicy ExactPolicy() => new StorageBudgetPolicy
        {
            FixedOverheadBytes = 100,
            MinimumFreeReserveBytes = 200,
            PayloadReserveRatio = 0.10d
        };

        [Test]
        public void 容量充足_返回可继续并保留计算明细()
        {
            var provider = new FakeProvider { Snapshot = StorageVolumeSnapshot.Known(2000, "fake") };

            StoragePreflightResult result = StoragePreflight.Check(provider, "/data", 1000, ExactPolicy());

            Assert.IsTrue(result.CanProceed);
            Assert.AreEqual(StorageCapacityStatus.Sufficient, result.Status);
            Assert.AreEqual(1300, result.RequiredBytes, "payload 1000 + fixed 100 + minimum reserve 200");
            Assert.AreEqual(2000, result.AvailableBytes);
            Assert.AreEqual("/data", provider.LastPath);
        }

        [Test]
        public void 动态余量超过最低余量_按Payload比例计算()
        {
            StorageBudgetPolicy policy = ExactPolicy();
            long required = policy.CalculateRequiredBytes(10000);

            Assert.AreEqual(11100, required, "payload 10000 + fixed 100 + ratio reserve 1000");
        }

        [Test]
        public void 容量不足_失败关闭且返回稳定错误码()
        {
            var provider = new FakeProvider { Snapshot = StorageVolumeSnapshot.Known(1299, "fake") };

            StoragePreflightResult result = StoragePreflight.Check(provider, "/data", 1000, ExactPolicy());

            Assert.IsFalse(result.CanProceed);
            Assert.AreEqual(StorageCapacityStatus.Insufficient, result.Status);
            Assert.AreEqual(StoragePreflight.InsufficientCode, result.Code);
            StringAssert.Contains("required=1300", result.Message);
            StringAssert.Contains("available=1299", result.Message);
        }

        [Test]
        public void 查询未知_不得伪装成容量充足()
        {
            var provider = new FakeProvider { Snapshot = StorageVolumeSnapshot.Unknown("platform unsupported") };

            StoragePreflightResult result = StoragePreflight.Check(provider, "/data", 1, ExactPolicy());

            Assert.IsFalse(result.CanProceed);
            Assert.AreEqual(StorageCapacityStatus.Unknown, result.Status);
            Assert.AreEqual(StoragePreflight.UnknownCode, result.Code);
            StringAssert.Contains("platform unsupported", result.Message);
        }

        [Test]
        public void 极大Payload_计算饱和而不发生整数溢出()
        {
            long required = ExactPolicy().CalculateRequiredBytes(long.MaxValue);
            Assert.AreEqual(long.MaxValue, required);
        }

        [Test]
        public void 非法策略_明确拒绝()
        {
            var policy = ExactPolicy();
            policy.PayloadReserveRatio = -0.1d;
            Assert.Throws<System.InvalidOperationException>(() => policy.CalculateRequiredBytes(1));
            Assert.Throws<System.ArgumentOutOfRangeException>(() => ExactPolicy().CalculateRequiredBytes(-1));
        }

        [Test]
        public void 热更磁盘不足_下载前失败且不创建Staging()
        {
            string root = Path.Combine(Application.temporaryCachePath, "storage-preflight-test");
            if (Directory.Exists(root)) Directory.Delete(root, true);
            HotUpdateSlotManager.TestRootDirectoryOverride = root;
            HotUpdateSlotManager.ResetStateForTests();

            try
            {
                var provider = new FakeProvider { Snapshot = StorageVolumeSnapshot.Known(1299, "fake") };
                var manager = new HotUpdateManager();
                manager.SetStoragePreflightForTests(provider, ExactPolicy());
                string reported = null;
                manager.OnUpdateError += error => reported = error;

                var update = new UpdateInfo { AppVersion = Application.version, CodeVersion = 2 };
                var patches = new List<PatchFile>
                {
                    new PatchFile { FileName = "HotUpdate.dll.bytes", Url = "https://cdn.test/x", Size = 1000, SHA256 = new string('a', 64) }
                };

                LogAssert.Expect(LogType.Error, new Regex(StoragePreflight.InsufficientCode));
                bool success = Wait(manager.DownloadPatchAsync(update, patches));

                Assert.IsFalse(success);
                StringAssert.Contains(StoragePreflight.InsufficientCode, reported);
                Assert.IsFalse(Directory.Exists(root), "容量门禁必须早于 staging 目录和任何网络写入");
            }
            finally
            {
                HotUpdateSlotManager.TestRootDirectoryOverride = null;
                HotUpdateSlotManager.ResetStateForTests();
                if (Directory.Exists(root)) Directory.Delete(root, true);
            }
        }
    }
}
