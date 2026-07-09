using System.IO;
using Framework.Editor.BuildSize;
using NUnit.Framework;

namespace Framework.Tests
{
    /// <summary>
    /// 包体门禁单测：裁决矩阵（纯逻辑）、目录扫描（临时目录）、基线读写往返。
    /// </summary>
    public class BuildSizeGateTests
    {
        private static BuildSizeSnapshot Snap(long total, params (string name, long bytes)[] entries)
        {
            var s = new BuildSizeSnapshot { totalBytes = total };
            foreach (var (name, bytes) in entries)
                s.entries.Add(new BuildSizeEntry(name, bytes));
            return s;
        }

        // ── 裁决矩阵 ──────────────────────────────────────────────────────

        [Test]
        public void 无基线_首次Pass()
        {
            var v = BuildSizeGate.Evaluate(null, Snap(1000), new BuildSizePolicy());
            Assert.AreEqual(BuildSizeStatus.Pass, v.Status);
        }

        [Test]
        public void 总量增长在阈内_Pass()
        {
            var baseline = Snap(1000);
            var current = Snap(1050); // +5% < 10%
            var v = BuildSizeGate.Evaluate(baseline, current, new BuildSizePolicy());
            Assert.AreEqual(BuildSizeStatus.Pass, v.Status);
        }

        [Test]
        public void 总量增长超百分比_Fail()
        {
            var baseline = Snap(1000);
            var current = Snap(1200); // +20% > 10%
            var v = BuildSizeGate.Evaluate(baseline, current, new BuildSizePolicy());
            Assert.AreEqual(BuildSizeStatus.Fail, v.Status);
            Assert.AreEqual("TOTAL", v.Violations[0].category);
        }

        [Test]
        public void 总量缩小_Pass()
        {
            var v = BuildSizeGate.Evaluate(Snap(1000), Snap(800), new BuildSizePolicy());
            Assert.AreEqual(BuildSizeStatus.Pass, v.Status);
        }

        [Test]
        public void 总量超绝对字节阈_Fail()
        {
            var policy = new BuildSizePolicy { maxTotalGrowthPercent = 0, maxTotalGrowthBytes = 100 };
            var v = BuildSizeGate.Evaluate(Snap(1000), Snap(1150), policy); // +150B > 100B
            Assert.AreEqual(BuildSizeStatus.Fail, v.Status);
        }

        [Test]
        public void 单类增长超阈_Fail()
        {
            long big = 200 * 1024;
            var baseline = Snap(big, ("a.bundle", big));
            var current = Snap(big + 100 * 1024, ("a.bundle", big + 100 * 1024)); // +50% > 25%
            // 总量也会超，但这里验证单类违规存在
            var v = BuildSizeGate.Evaluate(baseline, current, new BuildSizePolicy { maxTotalGrowthPercent = 0 });
            Assert.AreEqual(BuildSizeStatus.Fail, v.Status);
            Assert.AreEqual("a.bundle", v.Violations[0].category);
        }

        [Test]
        public void 单类体积低于门槛_不查百分比()
        {
            // 小文件从 100B → 1000B（+900%）但 < 64KB 门槛，跳过
            var baseline = Snap(100, ("tiny", 100));
            var current = Snap(1000, ("tiny", 1000));
            var v = BuildSizeGate.Evaluate(baseline, current, new BuildSizePolicy { maxTotalGrowthPercent = 0 });
            Assert.AreEqual(BuildSizeStatus.Pass, v.Status);
        }

        [Test]
        public void 新增条目_默认不违规()
        {
            var baseline = Snap(1000, ("a", 1000));
            var current = Snap(1000, ("a", 1000), ("b", 0));
            var v = BuildSizeGate.Evaluate(baseline, current, new BuildSizePolicy());
            Assert.AreEqual(BuildSizeStatus.Pass, v.Status);
        }

        [Test]
        public void 新增条目_开启后违规()
        {
            var baseline = Snap(1000, ("a", 1000));
            var current = Snap(1500, ("a", 1000), ("b", 500));
            var policy = new BuildSizePolicy { maxTotalGrowthPercent = 0, failOnNewEntry = true };
            var v = BuildSizeGate.Evaluate(baseline, current, policy);
            Assert.AreEqual(BuildSizeStatus.Fail, v.Status);
            Assert.AreEqual("NEW:b", v.Violations[0].category);
        }

        [Test]
        public void 违规但warnOnly_降级为Warn()
        {
            var policy = new BuildSizePolicy { warnOnly = true };
            var v = BuildSizeGate.Evaluate(Snap(1000), Snap(2000), policy);
            Assert.AreEqual(BuildSizeStatus.Warn, v.Status);
            Assert.IsFalse(v.IsBlocking);
        }

        [Test]
        public void 人类可读字节()
        {
            Assert.AreEqual("512 B", BuildSizeGate.Human(512));
            StringAssert.Contains("KB", BuildSizeGate.Human(2048));
            StringAssert.Contains("MB", BuildSizeGate.Human(5 * 1024 * 1024));
        }

        // ── 目录扫描 + 基线往返（临时目录）─────────────────────────────────

        [Test]
        public void 目录扫描_汇总总量与条目()
        {
            string dir = Path.Combine(Path.GetTempPath(), "fb_buildsize_" + System.Guid.NewGuid().ToString("N"));
            try
            {
                Directory.CreateDirectory(Path.Combine(dir, "sub"));
                File.WriteAllBytes(Path.Combine(dir, "a.bin"), new byte[100]);
                File.WriteAllBytes(Path.Combine(dir, "sub", "b.bin"), new byte[250]);

                var snap = BuildSizeSnapshotIO.FromDirectory(dir, "test");
                Assert.AreEqual(350, snap.totalBytes);
                Assert.AreEqual(2, snap.entries.Count);
                // 子目录用正斜杠相对路径
                CollectionAssert.Contains(
                    snap.entries.ConvertAll(e => e.name), "sub/b.bin");
            }
            finally
            {
                Directory.Delete(dir, true);
            }
        }

        [Test]
        public void 目录不存在_空快照总量0()
        {
            var snap = BuildSizeSnapshotIO.FromDirectory(Path.Combine(Path.GetTempPath(), "fb_nonexist_xyz"));
            Assert.AreEqual(0, snap.totalBytes);
            Assert.AreEqual(0, snap.entries.Count);
        }

        [Test]
        public void 基线读写往返()
        {
            string path = Path.Combine(Path.GetTempPath(), "fb_baseline_" + System.Guid.NewGuid().ToString("N") + ".json");
            try
            {
                var snap = Snap(1234, ("x.bundle", 1000), ("y.bundle", 234));
                snap.label = "v1";
                BuildSizeSnapshotIO.SaveBaseline(path, snap);

                var loaded = BuildSizeSnapshotIO.LoadBaseline(path);
                Assert.IsNotNull(loaded);
                Assert.AreEqual(1234, loaded.totalBytes);
                Assert.AreEqual("v1", loaded.label);
                Assert.AreEqual(2, loaded.entries.Count);
            }
            finally
            {
                if (File.Exists(path)) File.Delete(path);
            }
        }

        [Test]
        public void 基线不存在_返回null视为首次()
        {
            var loaded = BuildSizeSnapshotIO.LoadBaseline(Path.Combine(Path.GetTempPath(), "fb_no_baseline.json"));
            Assert.IsNull(loaded);
        }
    }
}
