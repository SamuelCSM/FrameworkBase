using System;
using System.IO;
using System.Text;
using NUnit.Framework;

namespace Framework.Tests
{
    /// <summary>
    /// 配置数据库事务化安装器单测：
    /// 安装成功备份保留 / 确认后备份清理 / 确认前失败恢复旧库 / 校验失败不触碰正式库 /
    /// 空载荷失败 / 替换失败即时回滚 / 恢复语义幂等。
    /// 校验函数注入内容标记假实现，全程不依赖 SQLite 与 Unity API。
    /// </summary>
    public class ConfigDatabaseInstallerTests
    {
        private string _root;
        private string _dbPath;

        // 假校验：以 "VALID" 前缀判定数据库合法，替代 SQLite 打开校验。
        private static bool FakeValidator(string path) =>
            File.ReadAllText(path, Encoding.UTF8).StartsWith("VALID", StringComparison.Ordinal);

        private ConfigDatabaseInstaller NewInstaller() =>
            new ConfigDatabaseInstaller(_dbPath, FakeValidator);

        private static byte[] Bytes(string content) => Encoding.UTF8.GetBytes(content);

        [SetUp]
        public void SetUp()
        {
            _root = Path.Combine(Path.GetTempPath(), "fw_cfg_installer_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_root);
            _dbPath = Path.Combine(_root, "config.db");
        }

        [TearDown]
        public void TearDown()
        {
            if (Directory.Exists(_root))
                Directory.Delete(_root, true);
        }

        [Test]
        public void 安装成功_旧库备份保留而不是立即删除()
        {
            File.WriteAllText(_dbPath, "VALID old");
            var installer = NewInstaller();

            ConfigInstallResult result = installer.Install(Bytes("VALID new"), "test");

            Assert.AreEqual(ConfigInstallStatus.Installed, result.Status);
            Assert.IsTrue(result.Succeeded);
            Assert.AreEqual("VALID new", File.ReadAllText(_dbPath), "正式库应为新内容");
            Assert.IsTrue(installer.HasUnconfirmedBackup, "备份必须保留到启动确认点（旧实现立即删除是缺陷）");
            Assert.AreEqual("VALID old", File.ReadAllText(installer.BackupPath), "备份应为旧内容");
        }

        [Test]
        public void 启动确认后_备份被清理()
        {
            File.WriteAllText(_dbPath, "VALID old");
            var installer = NewInstaller();
            installer.Install(Bytes("VALID new"), "test");

            installer.ConfirmInstalled();

            Assert.IsFalse(installer.HasUnconfirmedBackup, "确认后备份应清理");
            Assert.AreEqual("VALID new", File.ReadAllText(_dbPath), "确认不改变正式库内容");
        }

        [Test]
        public void 确认前失败_恢复上一份已确认数据库()
        {
            File.WriteAllText(_dbPath, "VALID old");
            var installer = NewInstaller();
            installer.Install(Bytes("VALID new"), "test");

            // 模拟：配置安装成功后 Hotfix 启动失败（代码槽回滚），配置必须一起回滚。
            bool restored = installer.RestoreLastConfirmed();

            Assert.IsTrue(restored);
            Assert.AreEqual("VALID old", File.ReadAllText(_dbPath), "必须恢复旧库，不能停留在新版本");
            Assert.IsFalse(installer.HasUnconfirmedBackup, "恢复后备份已消费");
        }

        [Test]
        public void 无备份时恢复_返回false且不动正式库()
        {
            File.WriteAllText(_dbPath, "VALID current");
            var installer = NewInstaller();

            bool restored = installer.RestoreLastConfirmed();

            Assert.IsFalse(restored, "没有未确认安装时恢复应是空操作");
            Assert.AreEqual("VALID current", File.ReadAllText(_dbPath));
        }

        [Test]
        public void 校验失败_正式库与备份均不被触碰()
        {
            File.WriteAllText(_dbPath, "VALID old");
            var installer = NewInstaller();

            ConfigInstallResult result = installer.Install(Bytes("BROKEN payload"), "test");

            Assert.AreEqual(ConfigInstallStatus.ValidationFailed, result.Status);
            Assert.IsFalse(result.Succeeded, "校验失败必须是失败终态，不能与\"没有配置更新\"混同");
            Assert.AreEqual("VALID old", File.ReadAllText(_dbPath), "校验失败不得污染正式库");
            Assert.IsFalse(installer.HasUnconfirmedBackup);
            Assert.IsFalse(File.Exists(installer.TempPath), "临时文件应清理");
        }

        [Test]
        public void 空载荷_返回校验失败()
        {
            var installer = NewInstaller();

            ConfigInstallResult empty = installer.Install(Array.Empty<byte>(), "test");
            ConfigInstallResult nul = installer.Install(null, "test");

            Assert.AreEqual(ConfigInstallStatus.ValidationFailed, empty.Status);
            Assert.AreEqual(ConfigInstallStatus.ValidationFailed, nul.Status);
        }

        [Test]
        public void 首次安装无旧库_成功且无备份()
        {
            var installer = NewInstaller();

            ConfigInstallResult result = installer.Install(Bytes("VALID first"), "test");

            Assert.AreEqual(ConfigInstallStatus.Installed, result.Status);
            Assert.AreEqual("VALID first", File.ReadAllText(_dbPath));
            Assert.IsFalse(installer.HasUnconfirmedBackup, "无旧库可备份");
        }

        [Test]
        public void 校验函数抛异常_归类校验失败而非静默成功()
        {
            File.WriteAllText(_dbPath, "VALID old");
            var installer = new ConfigDatabaseInstaller(
                _dbPath,
                _ => throw new InvalidOperationException("模拟校验器崩溃"));

            ConfigInstallResult result = installer.Install(Bytes("whatever"), "test");

            Assert.AreEqual(ConfigInstallStatus.ValidationFailed, result.Status);
            Assert.AreEqual("VALID old", File.ReadAllText(_dbPath), "校验异常不得污染正式库");
        }

        [Test]
        public void 二次安装未确认_备份仍指向最近一次安装前的库()
        {
            File.WriteAllText(_dbPath, "VALID v1");
            var installer = NewInstaller();

            installer.Install(Bytes("VALID v2"), "test");
            installer.Install(Bytes("VALID v3"), "test");

            // 语义说明：连续安装未确认时备份滚动为 v2（最近一次安装前内容）。
            // 统一事务（ContentReleaseTransaction）保证同一启动周期内只有一次配置安装，
            // 这里验证文件层语义与该约束自洽。
            Assert.AreEqual("VALID v3", File.ReadAllText(_dbPath));
            Assert.AreEqual("VALID v2", File.ReadAllText(installer.BackupPath));
        }
    }
}
