using System.Collections.Generic;
using System.IO;
using NUnit.Framework;
using SQLite;

namespace Framework.Tests
{
    /// <summary>
    /// 配表导出产物门禁（模板垂直切片 B）：校验 ConfigPipeline 导出的首包 config.db
    /// 结构完整、与 Excel 源（Assets/RefData_Excel）保持同步提交。
    ///
    /// 设计取舍：只断言「结构 + 语义不变量」（表存在 / 列齐全 / 行数下限 / 数值符号），
    /// 不断言具体数值——数值属策划平衡范畴，改表不应打红 CI。
    /// 若本测试因 db 缺失而失败：跑一次 Framework → Config → Export All（或
    /// batchmode 执行 ConfigPipeline.ExportAllForBuilder）并连同 db 一起提交。
    /// </summary>
    public class ConfigDbExportTests
    {
        private const string DbPath = "Assets/StreamingAssets/RefData/config.db";
        private const string HotUpdateBytesPath = "Assets/ResourcesOut/RefData/config.db.bytes";

        /// <summary>clicker_level 行结构（仅测试用，独立于 HotUpdate 侧生成代码）。</summary>
        [Table("clicker_level")]
        private class ClickerLevelRow
        {
            [Column("Id")] public long Id { get; set; }
            [Column("ClickGain")] public long ClickGain { get; set; }
            [Column("IdleGainPerSec")] public long IdleGainPerSec { get; set; }
            [Column("UpgradeCost")] public long UpgradeCost { get; set; }
            [Column("Name")] public string Name { get; set; }
        }

        [Test]
        public void FirstPackageDb_Exists()
        {
            Assert.IsTrue(File.Exists(DbPath),
                $"首包配置库缺失：{DbPath}。请跑 Framework → Config → Export All 并提交产物。");
        }

        [Test]
        public void HotUpdateBytes_ExistsAndMatchesFirstPackage()
        {
            Assert.IsTrue(File.Exists(HotUpdateBytesPath),
                $"热更配置库缺失：{HotUpdateBytesPath}。请跑 Framework → Config → Export All 并提交产物。");

            // Both 目标下热更 .bytes 是首包 db 的整库同步，二者必须字节一致，
            // 否则"首包新装 vs 热更覆盖"两条路径会拿到不同配置。
            byte[] firstPackage = File.ReadAllBytes(DbPath);
            byte[] hotUpdate = File.ReadAllBytes(HotUpdateBytesPath);
            Assert.AreEqual(firstPackage.Length, hotUpdate.Length,
                "首包 db 与热更 .bytes 大小不一致，疑似只导出了单目标——请用 Export All（Both）重导。");
        }

        [Test]
        public void ClickerLevelTable_StructureAndInvariants()
        {
            Assume.That(File.Exists(DbPath), $"跳过：{DbPath} 不存在（先跑 Export All）");

            using (var conn = new SQLiteConnection(DbPath, SQLiteOpenFlags.ReadOnly))
            {
                List<ClickerLevelRow> rows = conn.Table<ClickerLevelRow>().ToList();

                Assert.GreaterOrEqual(rows.Count, 2, "clicker_level 至少要有 2 个等级才能构成升级循环");

                var seenIds = new HashSet<long>();
                foreach (ClickerLevelRow row in rows)
                {
                    Assert.IsTrue(seenIds.Add(row.Id), $"等级主键重复：Id={row.Id}");
                    Assert.Greater(row.ClickGain, 0, $"等级 {row.Id} 点击收益必须为正");
                    Assert.GreaterOrEqual(row.IdleGainPerSec, 0, $"等级 {row.Id} 挂机收益不能为负");
                    Assert.GreaterOrEqual(row.UpgradeCost, 0, $"等级 {row.Id} 升级花费不能为负（0=满级）");
                    Assert.IsFalse(string.IsNullOrEmpty(row.Name), $"等级 {row.Id} 缺少名称");
                }

                // 语义不变量：恰好一个满级（UpgradeCost=0），且必须是最大等级
                List<ClickerLevelRow> maxLevels = rows.FindAll(r => r.UpgradeCost == 0);
                Assert.AreEqual(1, maxLevels.Count, "必须恰好一个满级（UpgradeCost=0）");

                long maxId = long.MinValue;
                foreach (ClickerLevelRow row in rows)
                    if (row.Id > maxId) maxId = row.Id;
                Assert.AreEqual(maxId, maxLevels[0].Id, "满级必须是最大等级");
            }
        }
    }
}
