using System;
using System.IO;
using NUnit.Framework;

namespace Framework.Tests
{
    /// <summary>
    /// 配表分片目录（ADR-006）单测：表→片路由、片缺失回退主库、主库自定义文件名、
    /// Bootstrap 表判定、登记校验（幂等重登记放行 / 配置漂移拒绝 / 非法文件名拒绝）。
    /// </summary>
    public class ConfigShardCatalogTests
    {
        // 路径分隔符按运行平台构造：被测代码走 Path.GetDirectoryName/Combine（平台相关），
        // 断言若硬编码 Windows 的 "D:\..." 在 Linux CI 上会因反斜杠不是分隔符而全成文件名，故用 Path.Combine 生成。
        private static readonly string MainDir = Path.Combine(Path.GetTempPath(), "persist");
        private static readonly string MainDb = Path.Combine(MainDir, "config.db");

        [Test]
        public void 路由_language表归language片_未登记表归主片()
        {
            Assert.AreEqual(ConfigShardCatalog.LanguageShardFileName,
                ConfigShardCatalog.ResolveFileNameByTable("language"));
            Assert.AreEqual(ConfigShardCatalog.LanguageShardFileName,
                ConfigShardCatalog.ResolveFileNameByTable("LANGUAGE"), "表名大小写不敏感");
            Assert.AreEqual(ConfigShardCatalog.MainShardFileName,
                ConfigShardCatalog.ResolveFileNameByTable("clicker_config"));
            Assert.AreEqual(ConfigShardCatalog.MainShardFileName,
                ConfigShardCatalog.ResolveFileNameByTable(null), "空表名安全归主片");
        }

        [Test]
        public void 片清单_主片恒在首位且去重()
        {
            var names = ConfigShardCatalog.GetAllShardFileNames();
            Assert.AreEqual(ConfigShardCatalog.MainShardFileName, names[0]);
            CollectionAssert.Contains(names, ConfigShardCatalog.LanguageShardFileName);
            CollectionAssert.AllItemsAreUnique(names);
        }

        [Test]
        public void 路径解析_片文件存在_读片库()
        {
            string path = ConfigShardCatalog.ResolveDbPathForTable(
                MainDb, "language", _ => true, out bool fellBack);

            Assert.IsFalse(fellBack);
            StringAssert.EndsWith("language.db", path);
            StringAssert.StartsWith(MainDir, path, "片库与主库同目录");
        }

        [Test]
        public void 路径解析_片文件缺失_回退主库并标记()
        {
            string path = ConfigShardCatalog.ResolveDbPathForTable(
                MainDb, "language", _ => false, out bool fellBack);

            Assert.IsTrue(fellBack, "片缺失必须显式标记回退，供调用方记日志");
            Assert.AreEqual(MainDb, path);
        }

        [Test]
        public void 路径解析_主片表不查文件存在性_直接主库()
        {
            // 主片表不得因 fileExists 返回 false 被误判回退（主库缺失属 EnsureDatabaseReady 职责）。
            string path = ConfigShardCatalog.ResolveDbPathForTable(
                MainDb, "clicker_config", _ => false, out bool fellBack);

            Assert.IsFalse(fellBack);
            Assert.AreEqual(MainDb, path);
        }

        [Test]
        public void 路径推导_主库文件名允许自定义()
        {
            // 测试/多实例场景主库可能不叫 config.db；主片路径必须原样返回，辅片跟随其目录。
            string customDir = Path.Combine(Path.GetTempPath(), "t");
            string customMain = Path.Combine(customDir, "custom_main.db");
            Assert.AreEqual(customMain,
                ConfigShardCatalog.GetShardDbPath(customMain, ConfigShardCatalog.MainShardFileName));
            StringAssert.StartsWith(customDir, ConfigShardCatalog.GetShardDbPath(
                customMain, ConfigShardCatalog.LanguageShardFileName));
        }

        [Test]
        public void Bootstrap表判定_language为真_业务表为假()
        {
            Assert.IsTrue(ConfigShardCatalog.IsFrameworkBootstrapTable("language"));
            Assert.IsFalse(ConfigShardCatalog.IsFrameworkBootstrapTable("clicker_config"));
            Assert.IsFalse(ConfigShardCatalog.IsFrameworkBootstrapTable(null));
        }

        [Test]
        public void 登记_幂等重登记放行_改片拒绝_非法文件名拒绝()
        {
            // 幂等：language 已内置登记到 language.db，重复同映射登记不抛。
            Assert.DoesNotThrow(() => ConfigShardCatalog.RegisterTableShard(
                "language", ConfigShardCatalog.LanguageShardFileName));

            // 配置漂移：同表改登记到别的片直接拒绝。
            Assert.Throws<InvalidOperationException>(() =>
                ConfigShardCatalog.RegisterTableShard("language", "other.db"));

            // 非法输入。
            Assert.Throws<ArgumentException>(() => ConfigShardCatalog.RegisterTableShard("", "x.db"));
            Assert.Throws<ArgumentException>(() => ConfigShardCatalog.RegisterTableShard("t", ""));
            Assert.Throws<ArgumentException>(() => ConfigShardCatalog.RegisterTableShard("t", "sub/x.db"));
        }
    }
}
