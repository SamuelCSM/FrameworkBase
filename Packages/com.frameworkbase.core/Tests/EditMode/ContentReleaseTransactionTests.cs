using System;
using System.IO;
using Framework.HotUpdate;
using NUnit.Framework;

namespace Framework.Tests
{
    /// <summary>
    /// 内容发行事务故障注入测试（任务书第四章第 14 点可纯逻辑覆盖子集）：
    /// 统一确认提升 LKG / Hotfix 失败后 Catalog 快照恢复 / Pending 重启回滚 /
    /// 连续失败出厂回退 / 正常重启不误回滚 / 旧 AppVersion 隔离 / 状态文件损坏失败安全 /
    /// 快照残留覆盖 / 恢复失败保留快照重试。
    /// 全程注入临时目录，不碰真实 persistentDataPath 与 Addressables。
    /// </summary>
    public class ContentReleaseTransactionTests
    {
        private string _root;
        private string _txRoot;
        private string _cacheDir;

        private const string AppV1 = "1.0.0";

        [SetUp]
        public void SetUp()
        {
            _root = Path.Combine(Path.GetTempPath(), "fw_content_tx_" + Guid.NewGuid().ToString("N"));
            _txRoot = Path.Combine(_root, "ContentRelease");
            _cacheDir = Path.Combine(_root, "com.unity.addressables");
            Directory.CreateDirectory(_root);
        }

        [TearDown]
        public void TearDown()
        {
            if (Directory.Exists(_root))
                Directory.Delete(_root, true);
        }

        private ContentReleaseTransaction NewTransaction(string appVersion = AppV1) =>
            new ContentReleaseTransaction(_txRoot, appVersion, _cacheDir);

        private static ContentReleaseRecord NewRecord(string releaseId = "rel-001", string appVersion = AppV1) =>
            new ContentReleaseRecord
            {
                ReleaseId = releaseId,
                AppVersion = appVersion,
                ResourceVersion = 2,
                CodeVersion = 3,
                ResourceChanged = true,
                CodeChanged = true,
            };

        private void WriteCatalogCache(string content)
        {
            Directory.CreateDirectory(_cacheDir);
            File.WriteAllText(Path.Combine(_cacheDir, "catalog_v.json"), content);
        }

        private string ReadCatalogCache() =>
            File.ReadAllText(Path.Combine(_cacheDir, "catalog_v.json"));

        // ── 成功路径：统一确认 ────────────────────────────────────────────────

        [Test]
        public void 全部成功后统一确认_Pending提升为Active与LKG_快照清除()
        {
            WriteCatalogCache("old catalog");
            var tx = NewTransaction();
            tx.PrepareForLaunch();

            Assert.IsTrue(tx.BeginPending(NewRecord()));
            WriteCatalogCache("new catalog"); // 模拟 UpdateCatalogs 覆盖缓存
            ContentReleaseRecord confirmed = tx.ConfirmPending();

            Assert.IsNotNull(confirmed);
            Assert.AreEqual("rel-001", confirmed.ReleaseId);
            Assert.AreEqual("rel-001", tx.Active.ReleaseId, "确认后 Active 指向本次发行");
            Assert.AreEqual("rel-001", tx.LastKnownGood.ReleaseId, "确认后 LKG 更新");
            Assert.IsFalse(tx.HasPending);
            Assert.AreEqual("new catalog", ReadCatalogCache(), "确认后新 Catalog 缓存保留（成为新 LKG）");
        }

        // ── 失败路径：进程内主动失败 ──────────────────────────────────────────

        [Test]
        public void 新Catalog激活后Hotfix失败_MarkPendingFailed恢复旧Catalog缓存()
        {
            WriteCatalogCache("old catalog");
            var tx = NewTransaction();
            tx.PrepareForLaunch();

            tx.BeginPending(NewRecord());
            WriteCatalogCache("new catalog"); // UpdateCatalogs 已覆盖缓存

            tx.MarkPendingFailed("hotfix_start_returned_false");

            Assert.IsFalse(tx.HasPending);
            Assert.AreEqual("old catalog", ReadCatalogCache(),
                "Catalog 缓存必须恢复到快照内容，下次进程启动回到旧 Catalog");
            Assert.IsTrue(tx.Active.IsEmpty, "失败发行绝不进入 Active");
        }

        // ── 失败路径：进程被杀，重启恢复 ─────────────────────────────────────

        [Test]
        public void Pending重启恢复_下次启动检测未确认发行并回滚Catalog()
        {
            WriteCatalogCache("old catalog");
            var tx1 = NewTransaction();
            tx1.PrepareForLaunch();
            tx1.BeginPending(NewRecord());
            WriteCatalogCache("new catalog");
            // 进程在确认前被杀：不调用 Confirm/MarkFailed，直接丢弃实例

            var tx2 = NewTransaction(); // 模拟重启：从持久化状态恢复，而不是内存布尔值
            var report = tx2.PrepareForLaunch();

            Assert.IsTrue(report.PendingRolledBack, "重启后必须检测到未确认 Pending");
            Assert.AreEqual("rel-001", report.RolledBackReleaseId);
            Assert.IsTrue(report.CatalogRestored);
            Assert.AreEqual("old catalog", ReadCatalogCache(), "Catalog 缓存已回滚");
            Assert.IsFalse(tx2.HasPending, "回滚后 Pending 清除，二次启动不再重复回滚");

            var report3 = NewTransaction().PrepareForLaunch();
            Assert.IsFalse(report3.PendingRolledBack, "已回滚的 Pending 不会被重复回滚");
        }

        // ── Crash-loop：出厂回退 ─────────────────────────────────────────────

        [Test]
        public void 已确认发行连续多次启动未确认_触发内容级出厂回退()
        {
            WriteCatalogCache("catalog v2");
            var tx = NewTransaction();
            tx.PrepareForLaunch();
            tx.BeginPending(NewRecord());
            tx.ConfirmPending();

            // 连续 4 次启动都未到达确认点（每次 PrepareForLaunch 计数 +1，阈值 3）
            ContentReleaseTransaction.LaunchRecoveryReport last = null;
            for (int i = 0; i < ContentReleaseTransaction.MaxUnconfirmedLaunchAttempts + 1; i++)
                last = NewTransaction().PrepareForLaunch();

            Assert.IsNotNull(last);
            Assert.IsTrue(last.FactoryResetPerformed, "超过阈值必须出厂回退");
            Assert.IsFalse(Directory.Exists(_cacheDir), "Catalog 缓存清空，回到包内出厂 Catalog");

            var tx2 = NewTransaction();
            tx2.PrepareForLaunch();
            Assert.IsTrue(tx2.Active.IsEmpty, "出厂回退后无 Active 发行");
        }

        [Test]
        public void 已确认发行正常重启并确认_不会被误回滚且计数清零()
        {
            WriteCatalogCache("catalog");
            var tx = NewTransaction();
            tx.PrepareForLaunch();
            tx.BeginPending(NewRecord());
            tx.ConfirmPending();

            for (int i = 0; i < 10; i++)
            {
                var restart = NewTransaction();
                var report = restart.PrepareForLaunch();
                Assert.IsFalse(report.PendingRolledBack, "正常重启不得误判回滚");
                Assert.IsFalse(report.FactoryResetPerformed, $"第 {i + 1} 次正常重启不得触发出厂回退");
                Assert.IsNull(restart.ConfirmPending(), "无 Pending 时确认返回 null 且只清计数");
                Assert.AreEqual("rel-001", restart.Active.ReleaseId, "Active 发行保持");
            }
        }

        // ── 整包隔离 ─────────────────────────────────────────────────────────

        [Test]
        public void 旧AppVersion的发行状态_不被新整包加载()
        {
            WriteCatalogCache("catalog");
            var tx1 = NewTransaction("1.0.0");
            tx1.PrepareForLaunch();
            tx1.BeginPending(NewRecord("rel-old", "1.0.0"));
            tx1.ConfirmPending();

            var tx2 = NewTransaction("2.0.0"); // 整包升级
            var report = tx2.PrepareForLaunch();

            Assert.IsFalse(report.PendingRolledBack);
            Assert.IsTrue(tx2.Active.IsEmpty, "旧整包的发行记录必须被隔离重置");
            Assert.IsTrue(tx2.LastKnownGood.IsEmpty);
        }

        [Test]
        public void 发行AppVersion与当前整包不一致_拒绝开启Pending()
        {
            var tx = NewTransaction("2.0.0");
            tx.PrepareForLaunch();

            bool begun = tx.BeginPending(NewRecord("rel-x", "1.0.0"));

            Assert.IsFalse(begun, "跨整包的发行必须拒绝，防止旧清单内容装进新整包");
            Assert.IsFalse(tx.HasPending);
        }

        // ── 失败安全 ─────────────────────────────────────────────────────────

        [Test]
        public void 状态文件损坏_失败安全视为无状态_不执行半信半疑的回滚()
        {
            Directory.CreateDirectory(_txRoot);
            File.WriteAllText(Path.Combine(_txRoot, "release-state.json"), "{ 这不是合法 JSON ///");
            WriteCatalogCache("catalog");

            var tx = NewTransaction();
            ContentReleaseTransaction.LaunchRecoveryReport report = null;
            Assert.DoesNotThrow(() => report = tx.PrepareForLaunch(), "损坏状态不得抛异常中断启动");
            Assert.IsFalse(report.PendingRolledBack, "损坏状态视为无 Pending，禁止猜测性回滚");
            Assert.AreEqual("catalog", ReadCatalogCache(), "Catalog 缓存不被触碰");
        }

        [Test]
        public void 空发行记录_拒绝开启Pending()
        {
            var tx = NewTransaction();
            tx.PrepareForLaunch();

            Assert.IsFalse(tx.BeginPending(new ContentReleaseRecord()), "空 ReleaseId 必须拒绝");
            Assert.IsFalse(tx.BeginPending(null), "null 记录必须拒绝");
        }

        // ── Catalog 快照细节 ─────────────────────────────────────────────────

        [Test]
        public void 首次热更无Catalog缓存_快照记录空态_恢复动作等于清空缓存()
        {
            // 缓存目录不存在（首次热更）
            var tx = NewTransaction();
            tx.PrepareForLaunch();
            Assert.IsTrue(tx.BeginPending(NewRecord()));

            WriteCatalogCache("first remote catalog"); // UpdateCatalogs 首次建缓存
            tx.MarkPendingFailed("test");

            Assert.IsFalse(Directory.Exists(_cacheDir),
                "快照时无缓存 → 恢复动作 = 删除缓存目录，回到包内 Catalog");
        }

        [Test]
        public void 快照残留_BeginPending覆盖旧快照()
        {
            WriteCatalogCache("v1");
            var tx = NewTransaction();
            tx.PrepareForLaunch();
            tx.BeginPending(NewRecord("rel-a"));
            tx.MarkPendingFailed("first_attempt_failed"); // 消费快照

            WriteCatalogCache("v1"); // 恢复后缓存为 v1
            Assert.IsTrue(tx.BeginPending(NewRecord("rel-b")), "残留/重试场景必须能重新开启");
            WriteCatalogCache("v2");
            tx.MarkPendingFailed("second_attempt_failed");

            Assert.AreEqual("v1", ReadCatalogCache(), "第二次快照内容正确（覆盖了第一次残留）");
        }

        [Test]
        public void 快照恢复被文件占用阻断_快照保留供下次启动重试()
        {
            WriteCatalogCache("old catalog");
            var tx1 = NewTransaction();
            tx1.PrepareForLaunch();
            tx1.BeginPending(NewRecord());
            WriteCatalogCache("new catalog");

            // 模拟恢复失败：独占句柄锁住缓存文件，Directory.Delete 抛异常
            var snapshotDir = Path.Combine(_txRoot, "catalog-lkg");
            ContentReleaseTransaction lockedTx;
            using (new FileStream(
                       Path.Combine(_cacheDir, "catalog_v.json"),
                       FileMode.Open, FileAccess.Read, FileShare.None))
            {
                lockedTx = NewTransaction();
                var report = lockedTx.PrepareForLaunch();
                Assert.IsTrue(report.PendingRolledBack, "Pending 依旧被判定回滚");
                Assert.IsFalse(report.CatalogRestored, "恢复失败必须如实上报");
                Assert.IsTrue(Directory.Exists(snapshotDir), "恢复失败时快照必须保留，等待下次启动重试");
                Assert.IsTrue(lockedTx.HasPending, "恢复失败时 Pending 必须保留作为回滚凭据");
            }

            // 句柄释放后（模拟下次启动）：PrepareForLaunch 自动重试恢复成功
            var retryReport = NewTransaction().PrepareForLaunch();
            Assert.IsTrue(retryReport.PendingRolledBack);
            Assert.IsTrue(retryReport.CatalogRestored, "下次启动自动重试恢复成功");
            Assert.AreEqual("old catalog", ReadCatalogCache());
        }

        [Test]
        public void 快照描述文件损坏_失败安全跳过恢复并丢弃()
        {
            WriteCatalogCache("current");
            var snapshotDir = Path.Combine(_txRoot, "catalog-lkg");
            Directory.CreateDirectory(snapshotDir);
            File.WriteAllText(Path.Combine(snapshotDir, "snapshot-info.json"), "not json !!!");

            var snapshot = new CatalogCacheSnapshotManager(_cacheDir, snapshotDir);
            bool restored = snapshot.RestoreSnapshot();

            Assert.IsFalse(restored, "损坏快照不得执行半信半疑的恢复");
            Assert.AreEqual("current", ReadCatalogCache(), "缓存不被触碰");
            Assert.IsFalse(snapshot.HasSnapshot, "损坏快照被丢弃，不再反复尝试");
        }

        [Test]
        public void 缓存目录与快照目录互为父子_构造直接拒绝()
        {
            Assert.Throws<ArgumentException>(() =>
                new CatalogCacheSnapshotManager(_root, Path.Combine(_root, "sub")));
            Assert.Throws<ArgumentException>(() =>
                new CatalogCacheSnapshotManager(Path.Combine(_root, "sub"), _root));
        }
    }
}
