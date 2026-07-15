using System;
using System.Collections.Generic;
using Framework.Storage;
using NUnit.Framework;

namespace Framework.Tests
{
    public class CacheCleanupTests
    {
        private static CacheRetentionPolicy Policy() => new CacheRetentionPolicy
        {
            MaxCacheBytes = 1000,
            HighWatermarkRatio = 0.9,
            LowWatermarkRatio = 0.7,
        };

        [Test]
        public void 空间缺口_按临时到历史版本的优先级清理()
        {
            var entries = new List<CacheEntry>
            {
                Entry("release", 500, CacheEntryKind.ObsoleteRelease, 1),
                Entry("staging", 300, CacheEntryKind.OrphanStaging, 2),
                Entry("temp", 100, CacheEntryKind.Temporary, 3),
            };

            CacheCleanupPlan plan = CacheCleanupPlanner.CreatePlan(
                new CacheCleanupRequest(500, requiredFreeBytes: 350, availableFreeBytes: 0), entries, Policy());

            Assert.AreEqual(400, plan.PlannedBytes);
            Assert.AreEqual("temp", plan.Entries[0].Path);
            Assert.AreEqual("staging", plan.Entries[1].Path);
        }

        [Test]
        public void 保护集_即使空间不足也永不进入删除计划()
        {
            CacheEntry active = Entry("active", 900, CacheEntryKind.ObsoleteRelease, 1);
            active.IsProtected = true;
            CacheEntry orphan = Entry("orphan", 100, CacheEntryKind.ObsoleteRelease, 2);

            CacheCleanupPlan plan = CacheCleanupPlanner.CreatePlan(
                new CacheCleanupRequest(1000, 1000, 0), new[] { active, orphan }, Policy());

            Assert.AreEqual(1, plan.Entries.Count);
            Assert.AreEqual("orphan", plan.Entries[0].Path);
        }

        [Test]
        public void 超过高水位_规划清到低水位()
        {
            CacheCleanupPlan plan = CacheCleanupPlanner.CreatePlan(
                new CacheCleanupRequest(950, 0, 1000),
                new[] { Entry("a", 100, CacheEntryKind.Temporary, 1), Entry("b", 200, CacheEntryKind.ObsoleteRelease, 2) },
                Policy());

            Assert.AreEqual(250, plan.TargetBytes);
            Assert.AreEqual(300, plan.PlannedBytes);
        }

        [Test]
        public void 未触发低空间或高水位_不做投机清理()
        {
            CacheCleanupPlan plan = CacheCleanupPlanner.CreatePlan(
                new CacheCleanupRequest(800, 100, 200),
                new[] { Entry("old", 500, CacheEntryKind.ObsoleteRelease, 1) },
                Policy());

            Assert.AreEqual(0, plan.TargetBytes);
            Assert.AreEqual(0, plan.Entries.Count);
        }

        private static CacheEntry Entry(string path, long size, CacheEntryKind kind, int age) => new CacheEntry
        {
            Path = path,
            SizeBytes = size,
            Kind = kind,
            LastWriteUtc = DateTime.UtcNow.AddDays(-age),
        };
    }
}
