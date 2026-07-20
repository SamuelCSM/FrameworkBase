using System;
using System.IO;
using Framework.Data;
using NUnit.Framework;

namespace Framework.Tests
{
    public sealed class ConfigListBaseTests
    {
        public sealed class RelationRow
        {
            public int ParentId { get; set; }

            public int ChildId { get; set; }
        }

        private sealed class RelationList : ConfigListBase<RelationRow> { }

        private string _dbPath;

        [SetUp]
        public void SetUp()
        {
            _dbPath = Path.Combine(Path.GetTempPath(), "fw_config_list_" + Guid.NewGuid().ToString("N") + ".db");
            using (var db = new SQLiteHelper(_dbPath))
            {
                db.Execute("CREATE TABLE relation_list (ParentId INTEGER, ChildId INTEGER)");
                db.Execute("INSERT INTO relation_list (ParentId, ChildId) VALUES (?, ?)", 100, 201);
                db.Execute("INSERT INTO relation_list (ParentId, ChildId) VALUES (?, ?)", 100, 202);
            }
        }

        [TearDown]
        public void TearDown()
        {
            if (File.Exists(_dbPath)) File.Delete(_dbPath);
        }

        [Test]
        public void Load_PreservesRowsWithDuplicateFirstColumn()
        {
            var table = new RelationList();
            table.Load(_dbPath, "relation_list");

            Assert.AreEqual(2, table.Count);
            Assert.AreEqual(2, table.GetList(row => row.ParentId == 100).Count);
            Assert.AreEqual(201, table.Items[0].ChildId);
            Assert.AreEqual(202, table.Items[1].ChildId);
        }
    }
}
