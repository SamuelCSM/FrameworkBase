using System;
using System.IO;
using Framework.HotUpdate;
using Framework.Serialization;
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

        private string StatePath => Path.Combine(_txRoot, "release-state.json");
        private string SnapshotDir => Path.Combine(_txRoot, "catalog-lkg");

        /// <summary>把状态文件写坏（模拟落盘掉电写坏），触发启动准备阶段的安全兜底。</summary>
        private void CorruptStateFile()
        {
            Directory.CreateDirectory(_txRoot);
            File.WriteAllText(StatePath, "not a valid json !!!");
        }

        /// <summary>用指定结构版本写出一份状态（格式无关，避免依赖 JSON 缩进/大小写）。</summary>
        private void WriteRawState(ContentReleaseState state)
        {
            Directory.CreateDirectory(_txRoot);
            File.WriteAllText(StatePath, JsonSerializers.Shared.ToJson(state, true));
        }

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
        public void 状态文件损坏_失败安全不抛异常且走安全兜底_不静默信任当前内容()
        {
            Directory.CreateDirectory(_txRoot);
            File.WriteAllText(Path.Combine(_txRoot, "release-state.json"), "{ 这不是合法 JSON ///");
            WriteCatalogCache("catalog");

            var tx = NewTransaction();
            ContentReleaseTransaction.LaunchRecoveryReport report = null;
            Assert.DoesNotThrow(() => report = tx.PrepareForLaunch(), "损坏状态不得抛异常中断启动");
            // 策略变更（#2）：损坏状态不再"当作无状态继续信任当前 Catalog"——那样可能放行事务中途
            // 写坏的、与旧代码槽错配的新 Catalog。改为安全兜底：无法证明已确认时优先恢复快照，无快照则回退出厂。
            Assert.IsTrue(report.StateCorruptionHandled,
                "损坏状态必须走安全兜底（恢复快照/回退出厂），不得静默继续信任当前内容");
            Assert.IsFalse(report.PendingRolledBack, "损坏时不基于垃圾数据做猜测性 Pending 回滚");
            // 本用例无快照 → 回退出厂：分层兜底的两个分支分别由
            // 状态文件损坏_有快照_恢复快照不回退出厂 / 状态文件损坏_无快照_回退出厂 详测。
            Assert.IsTrue(report.FactoryResetPerformed, "无快照可恢复时回退出厂");
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

            // 模拟恢复时的 I/O 故障，且要求跨平台确定性成立：把缓存目录替换成同名文件。
            // 恢复逻辑（CatalogCacheSnapshotManager.RestoreSnapshot）此时 Directory.Exists(缓存路径)
            // 为 false 而跳过删除，随后 Directory.CreateDirectory(缓存路径) 因路径被文件占用，
            // 在 Windows 与 Linux（CI）上都抛 IOException，走进"恢复失败保留快照"分支。
            // 不用独占文件句柄：POSIX 文件锁是劝告性的，句柄无法阻断删除/覆盖，独占锁方案仅在
            // Windows 成立，会让本用例在 Linux 容器里"恢复照样成功"从而误判失败。
            var snapshotDir = Path.Combine(_txRoot, "catalog-lkg");
            Directory.Delete(_cacheDir, true);
            File.WriteAllText(_cacheDir, "缓存路径被同名文件占用，恢复必然失败");

            var lockedTx = NewTransaction();
            var report = lockedTx.PrepareForLaunch();
            Assert.IsTrue(report.PendingRolledBack, "Pending 依旧被判定回滚");
            Assert.IsFalse(report.CatalogRestored, "恢复失败必须如实上报");
            Assert.IsTrue(Directory.Exists(snapshotDir), "恢复失败时快照必须保留，等待下次启动重试");
            Assert.IsTrue(lockedTx.HasPending, "恢复失败时 Pending 必须保留作为回滚凭据");

            // 障碍清除后（模拟下次启动）：PrepareForLaunch 自动重试恢复成功
            File.Delete(_cacheDir);
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

        // ── #1 确认阶段崩溃原子性：Committing 日志 + 前滚重放 ──────────────────────

        [Test]
        public void 确认阶段中断_内容确认前崩溃_下次启动前滚不回滚()
        {
            WriteCatalogCache("old catalog");
            var tx = NewTransaction();
            tx.PrepareForLaunch();
            tx.BeginPending(NewRecord("rel-commit"));
            WriteCatalogCache("new catalog"); // UpdateCatalogs 覆盖缓存
            tx.BeginCommit();                 // 进入提交（代码槽已在别处确认，本用例不建模）
            // 崩溃：ConfirmPending 之前进程被杀 → 用全新事务模拟下次启动重新加载

            var replayer = NewTransaction();
            var report = replayer.PrepareForLaunch();
            Assert.IsTrue(report.CommitReplayed, "检测到中断提交必须前滚，绝不回滚");
            Assert.AreEqual("rel-commit", report.CommittingRecord.ReleaseId);
            Assert.AreEqual("new catalog", ReadCatalogCache(), "前滚：Catalog 停在新内容");

            var after = NewTransaction();
            Assert.IsFalse(after.HasPending, "前滚后 Pending 已清空");
            Assert.AreEqual("rel-commit", after.Active.ReleaseId, "前滚后发行提升为 Active");
            Assert.IsTrue(after.IsCommitInProgress, "内容侧前滚后提交日志仍在，待调用方 EndCommit");

            replayer.EndCommit(); // 模拟 LaunchFlow 补完配置/version 后清日志
            Assert.IsFalse(NewTransaction().IsCommitInProgress, "EndCommit 后提交日志清除");
        }

        [Test]
        public void 确认阶段中断_内容已确认配置前崩溃_前滚补完清日志()
        {
            WriteCatalogCache("old catalog");
            var tx = NewTransaction();
            tx.PrepareForLaunch();
            tx.BeginPending(NewRecord("rel-commit"));
            WriteCatalogCache("new catalog");
            tx.BeginCommit();
            tx.ConfirmPending(); // 内容已确认（Pending→Active），配置/version 未提交即崩溃

            var replayer = NewTransaction();
            var report = replayer.PrepareForLaunch();
            Assert.IsTrue(report.CommitReplayed);
            Assert.AreEqual("rel-commit", report.CommittingRecord.ReleaseId, "Pending 已空时记录取自提交日志");
            Assert.AreEqual("new catalog", ReadCatalogCache());
            replayer.EndCommit();
            Assert.IsFalse(NewTransaction().IsCommitInProgress);
        }

        [Test]
        public void 确认提交完整完成_下次启动正常无前滚()
        {
            WriteCatalogCache("old catalog");
            var tx = NewTransaction();
            tx.PrepareForLaunch();
            tx.BeginPending(NewRecord("rel-commit"));
            WriteCatalogCache("new catalog");
            tx.BeginCommit();
            tx.ConfirmPending();
            tx.EndCommit();

            var report = NewTransaction().PrepareForLaunch();
            Assert.IsFalse(report.CommitReplayed, "提交已完成，下次启动不前滚");
            Assert.IsFalse(report.PendingRolledBack, "已确认发行不回滚");
            Assert.AreEqual("new catalog", ReadCatalogCache());
        }

        [Test]
        public void 无Pending时BeginCommit空操作_不产生提交日志()
        {
            WriteCatalogCache("old catalog");
            var tx = NewTransaction();
            tx.PrepareForLaunch();
            tx.BeginCommit(); // 本次无新发行
            Assert.IsFalse(tx.IsCommitInProgress, "无 Pending 不产生提交日志");
            Assert.IsFalse(NewTransaction().PrepareForLaunch().CommitReplayed);
        }

        [Test]
        public void 前滚重放幂等_重放中再崩溃下次启动结果一致()
        {
            WriteCatalogCache("old catalog");
            var tx = NewTransaction();
            tx.PrepareForLaunch();
            tx.BeginPending(NewRecord("rel-commit"));
            WriteCatalogCache("new catalog");
            tx.BeginCommit();

            // 第一次前滚（模拟 EndCommit 之前又崩溃：不清日志）
            Assert.IsTrue(NewTransaction().PrepareForLaunch().CommitReplayed);
            // 第二次前滚（下次启动重放）
            var replayer = NewTransaction();
            var r2 = replayer.PrepareForLaunch();
            Assert.IsTrue(r2.CommitReplayed, "重放仍前滚，幂等");
            Assert.AreEqual("rel-commit", r2.CommittingRecord.ReleaseId);
            Assert.AreEqual("new catalog", ReadCatalogCache());
            replayer.EndCommit();
            Assert.IsFalse(NewTransaction().PrepareForLaunch().CommitReplayed, "清日志后不再前滚");
        }

        [Test]
        public void 确认阶段中断后整包升级_旧版本提交被隔离不前滚()
        {
            WriteCatalogCache("old catalog");
            var tx = NewTransaction(AppV1);
            tx.PrepareForLaunch();
            tx.BeginPending(NewRecord("rel-commit", AppV1));
            WriteCatalogCache("new catalog");
            tx.BeginCommit(); // 提交中断（CommitInProgress=true，AppVersion=1.0.0）

            // 整包升级到 2.0.0 后再启动：旧版本的中断提交必须被整包隔离，绝不前滚
            var afterUpgrade = NewTransaction("2.0.0");
            var report = afterUpgrade.PrepareForLaunch();
            Assert.IsFalse(report.CommitReplayed, "旧整包版本的中断提交不得前滚（版本隔离优先于提交前滚）");
            Assert.IsFalse(afterUpgrade.IsCommitInProgress, "跨整包版本状态已隔离重置");
            Assert.IsTrue(afterUpgrade.Active.IsEmpty, "隔离后无 Active");
        }

        // ── #2 状态损坏分层兜底：有快照优先恢复，无快照回退出厂 ──────────────────

        [Test]
        public void 状态文件损坏_有快照_恢复快照不回退出厂()
        {
            WriteCatalogCache("old catalog");
            var tx = NewTransaction();
            tx.PrepareForLaunch();
            tx.BeginPending(NewRecord("rel-x")); // 建快照（内容 = old catalog）
            WriteCatalogCache("new catalog");    // 缓存被新 Catalog 覆盖
            CorruptStateFile();                  // 状态损坏，无法证明当前内容已确认

            var report = NewTransaction().PrepareForLaunch();
            Assert.IsTrue(report.StateCorruptionHandled, "损坏必须走安全兜底");
            Assert.IsTrue(report.CatalogRestored, "有快照优先恢复");
            Assert.IsFalse(report.FactoryResetPerformed, "有快照绝不回退出厂");
            Assert.AreEqual("old catalog", ReadCatalogCache(), "恢复到快照的旧 Catalog");
        }

        [Test]
        public void 状态文件损坏_无快照_回退出厂()
        {
            WriteCatalogCache("current catalog");
            var tx = NewTransaction();
            tx.PrepareForLaunch();
            tx.BeginPending(NewRecord("rel-x"));
            tx.BeginCommit();
            tx.ConfirmPending(); // 确认后快照被丢弃
            tx.EndCommit();
            Assert.IsFalse(Directory.Exists(SnapshotDir), "前置条件：确认后快照已丢弃");
            CorruptStateFile();

            var report = NewTransaction().PrepareForLaunch();
            Assert.IsTrue(report.StateCorruptionHandled);
            Assert.IsFalse(report.CatalogRestored, "无快照可恢复");
            Assert.IsTrue(report.FactoryResetPerformed, "无快照才回退出厂");
            Assert.IsFalse(Directory.Exists(_cacheDir), "出厂回退清空 Catalog 缓存");
        }

        [Test]
        public void 状态结构版本过高_按损坏安全兜底_有快照则恢复()
        {
            WriteCatalogCache("old catalog");
            var tx = NewTransaction();
            tx.PrepareForLaunch();
            tx.BeginPending(NewRecord("rel-x")); // 建快照
            WriteCatalogCache("new catalog");
            // 降级安装：状态结构版本高于当前实现，无法解释语义
            WriteRawState(new ContentReleaseState { SchemaVersion = 999, AppVersion = AppV1 });

            var report = NewTransaction().PrepareForLaunch();
            Assert.IsTrue(report.StateCorruptionHandled, "版本过高按损坏兜底");
            Assert.IsTrue(report.CatalogRestored, "有快照优先恢复");
            Assert.AreEqual("old catalog", ReadCatalogCache());
        }

        [Test]
        public void 旧版本v1状态_向前兼容不按损坏处理()
        {
            WriteCatalogCache("old catalog");
            // 模拟旧版本 v1 状态文件（无提交日志字段）：SchemaVersion=1 必须被接受
            var v1 = new ContentReleaseState { SchemaVersion = 1, AppVersion = AppV1 };
            v1.Pending = NewRecord("rel-legacy");
            WriteRawState(v1);

            var report = NewTransaction().PrepareForLaunch();
            Assert.IsFalse(report.StateCorruptionHandled, "v1 向前兼容，不按损坏处理");
            Assert.IsTrue(report.PendingRolledBack, "v1 的未确认 Pending 正常回滚");
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
