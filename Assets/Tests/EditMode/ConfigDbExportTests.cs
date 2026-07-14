using System.Collections.Generic;
using System.IO;
using NUnit.Framework;
using SQLite;

namespace Game.Template.Tests
{
    /// <summary>
    /// Clicker 模板配表产物门禁（垂直切片 B）：校验壳工程导出的首包 config.db
    /// 结构完整、与 Excel 源（Assets/RefData_Excel）保持同步提交。
    ///
    /// 本测试属于游戏侧参考样例，不进入 com.frameworkbase.core；删除 Clicker 样例时可一并删除。
    /// 只断言结构与语义不变量，不断言策划平衡数值。
    /// </summary>
    public class ConfigDbExportTests
    {
        private const string DbPath = "Assets/StreamingAssets/RefData/config.db";
        private const string HotUpdateBytesPath = "Assets/ResourcesOut/RefData/config.db.bytes";

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

            byte[] firstPackage = File.ReadAllBytes(DbPath);
            byte[] hotUpdate = File.ReadAllBytes(HotUpdateBytesPath);
            CollectionAssert.AreEqual(firstPackage, hotUpdate,
                "首包 db 与热更 .bytes 内容不一致——请用 Export All（Both）重导并同步提交。");
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
