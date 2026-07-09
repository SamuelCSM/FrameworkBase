using System.Text;
using Cysharp.Threading.Tasks;
using Framework.Save.Cloud;
using NUnit.Framework;

namespace Framework.Tests
{
    /// <summary>
    /// 云存档同步单测：决策矩阵（纯函数）、默认冲突裁决、整链同步（配 InMemory 后端）、离线兜底。
    /// </summary>
    public class CloudSaveSyncTests
    {
        private static T Wait<T>(UniTask<T> task) => task.GetAwaiter().GetResult();

        private static CloudSaveMetadata Meta(long version, long ts, string hash)
            => new CloudSaveMetadata(version, ts, hash, "dev");

        private static CloudSaveRecord Rec(long version, string content, long ts = 0)
        {
            byte[] payload = Encoding.UTF8.GetBytes(content);
            var meta = new CloudSaveMetadata(version, ts, CloudSaveMetadata.HashPayload(payload), "dev");
            return new CloudSaveRecord(meta, payload);
        }

        // ── 决策矩阵（纯函数）──────────────────────────────────────────────

        [Test]
        public void 决策_两端皆无_None()
        {
            Assert.AreEqual(CloudSyncDirection.None,
                CloudSaveSync.Decide(false, default, false, default));
        }

        [Test]
        public void 决策_仅本地_Upload()
        {
            Assert.AreEqual(CloudSyncDirection.Upload,
                CloudSaveSync.Decide(true, Meta(1, 0, "a"), false, default));
        }

        [Test]
        public void 决策_仅云端_Download()
        {
            Assert.AreEqual(CloudSyncDirection.Download,
                CloudSaveSync.Decide(false, default, true, Meta(1, 0, "a")));
        }

        [Test]
        public void 决策_云端版本更高_Download()
        {
            Assert.AreEqual(CloudSyncDirection.Download,
                CloudSaveSync.Decide(true, Meta(1, 0, "a"), true, Meta(2, 0, "b")));
        }

        [Test]
        public void 决策_本地版本更高_Upload()
        {
            Assert.AreEqual(CloudSyncDirection.Upload,
                CloudSaveSync.Decide(true, Meta(3, 0, "a"), true, Meta(2, 0, "b")));
        }

        [Test]
        public void 决策_同版本同内容_None()
        {
            Assert.AreEqual(CloudSyncDirection.None,
                CloudSaveSync.Decide(true, Meta(2, 10, "same"), true, Meta(2, 20, "same")));
        }

        [Test]
        public void 决策_同版本异内容_Conflict()
        {
            Assert.AreEqual(CloudSyncDirection.Conflict,
                CloudSaveSync.Decide(true, Meta(2, 10, "local"), true, Meta(2, 20, "cloud")));
        }

        // ── 默认冲突裁决 ──────────────────────────────────────────────────

        [Test]
        public void 冲突裁决_云端时间更新_Download()
        {
            Assert.AreEqual(CloudSyncDirection.Download,
                CloudSaveSync.ResolveConflictByTimestamp(Meta(2, 10, "l"), Meta(2, 20, "c")));
        }

        [Test]
        public void 冲突裁决_本地时间更新_Upload()
        {
            Assert.AreEqual(CloudSyncDirection.Upload,
                CloudSaveSync.ResolveConflictByTimestamp(Meta(2, 30, "l"), Meta(2, 20, "c")));
        }

        [Test]
        public void 冲突裁决_时间并列_保本地Upload()
        {
            Assert.AreEqual(CloudSyncDirection.Upload,
                CloudSaveSync.ResolveConflictByTimestamp(Meta(2, 20, "l"), Meta(2, 20, "c")));
        }

        // ── 整链同步（InMemory 后端）─────────────────────────────────────

        [Test]
        public void 同步_后端不可用_Offline()
        {
            var backend = new InMemoryCloudSaveBackend { Available = false };
            CloudSaveSync.SetBackend(backend);

            var result = Wait(CloudSaveSync.SyncAsync("k", Rec(1, "local")));
            Assert.AreEqual(CloudSyncStatus.Offline, result.Status);
        }

        [Test]
        public void 同步_本地新云端空_上传()
        {
            var backend = new InMemoryCloudSaveBackend();
            CloudSaveSync.SetBackend(backend);

            var result = Wait(CloudSaveSync.SyncAsync("k", Rec(1, "local")));
            Assert.AreEqual(CloudSyncStatus.Uploaded, result.Status);
            Assert.AreEqual(1, backend.Count);
        }

        [Test]
        public void 同步_云端新本地空_下载并携带正文()
        {
            var backend = new InMemoryCloudSaveBackend();
            backend.Seed("k", Rec(5, "cloud"));
            CloudSaveSync.SetBackend(backend);

            var result = Wait(CloudSaveSync.SyncAsync("k", null));
            Assert.AreEqual(CloudSyncStatus.Downloaded, result.Status);
            Assert.IsNotNull(result.DownloadedRecord);
            Assert.AreEqual("cloud", Encoding.UTF8.GetString(result.DownloadedRecord.Payload));
        }

        [Test]
        public void 同步_同版本同内容_UpToDate()
        {
            var backend = new InMemoryCloudSaveBackend();
            backend.Seed("k", Rec(2, "same"));
            CloudSaveSync.SetBackend(backend);

            var result = Wait(CloudSaveSync.SyncAsync("k", Rec(2, "same")));
            Assert.AreEqual(CloudSyncStatus.UpToDate, result.Status);
        }

        [Test]
        public void 同步_冲突走自定义解决器()
        {
            var backend = new InMemoryCloudSaveBackend();
            backend.Seed("k", Rec(2, "cloud"));
            CloudSaveSync.SetBackend(backend);

            // 自定义解决器：永远保本地（上传）
            var result = Wait(CloudSaveSync.SyncAsync(
                "k", Rec(2, "local"),
                (l, c) => CloudSyncDirection.Upload));

            Assert.AreEqual(CloudSyncStatus.Uploaded, result.Status);
            Assert.AreEqual("local", Encoding.UTF8.GetString(Wait(backend.DownloadAsync("k")).Payload));
        }

        [Test]
        public void 默认后端_未注入时云同步关闭()
        {
            CloudSaveSync.SetBackend(null); // 回退 NoOp
            Assert.IsFalse(CloudSaveSync.IsEnabled);
            var result = Wait(CloudSaveSync.SyncAsync("k", Rec(1, "local")));
            Assert.AreEqual(CloudSyncStatus.Offline, result.Status);
        }
    }
}
