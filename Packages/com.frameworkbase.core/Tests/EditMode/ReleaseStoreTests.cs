using System;
using System.IO;
using System.Text;
using Framework.Editor.Release;
using NUnit.Framework;

namespace Framework.Tests
{
    /// <summary>
    /// 发布存储抽象与部署目标失败关闭闸门测试：
    /// LocalFileSystemReleaseStore 的不可变写入/冲突/幂等/可变原子替换/枚举/路径穿越防护，
    /// 以及 ReleaseArtifactStoreFactory 对 Publish/Promote/Rollback/VerifyOnly 空目标的失败关闭、
    /// BuildOnly 允许空目标。全程临时目录，不依赖 Unity 运行时。
    /// </summary>
    public class ReleaseStoreTests
    {
        private string _root;

        [SetUp]
        public void SetUp()
        {
            _root = Path.Combine(Path.GetTempPath(), "fw_release_store_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_root);
        }

        [TearDown]
        public void TearDown()
        {
            if (Directory.Exists(_root))
                Directory.Delete(_root, true);
        }

        private string NewSourceFile(string name, string content)
        {
            string dir = Path.Combine(_root, "src");
            Directory.CreateDirectory(dir);
            string path = Path.Combine(dir, name);
            File.WriteAllText(path, content, new UTF8Encoding(false));
            return path;
        }

        private LocalFileSystemReleaseStore NewStore()
        {
            string storeRoot = Path.Combine(_root, "store");
            return new LocalFileSystemReleaseStore(storeRoot);
        }

        // ── 不可变写入 ────────────────────────────────────────────────────────

        [Test]
        public void 不可变写入_新对象成功且可回读校验()
        {
            var store = NewStore();
            string src = NewSourceFile("payload.bin", "hello");

            store.PutImmutable("releases/1.0.0/rid/payload.bin", src);

            Assert.IsTrue(store.Exists("releases/1.0.0/rid/payload.bin"));
            Assert.AreEqual("hello", Encoding.UTF8.GetString(store.Read("releases/1.0.0/rid/payload.bin")));
            Assert.AreEqual(
                store.ComputeSha256("releases/1.0.0/rid/payload.bin"),
                new LocalFileSystemReleaseStore(Path.GetDirectoryName(src)).ComputeSha256("payload.bin"));
        }

        [Test]
        public void 不可变写入_同内容重复_幂等跳过()
        {
            var store = NewStore();
            string src = NewSourceFile("payload.bin", "same");
            store.PutImmutable("releases/a/payload.bin", src);

            Assert.DoesNotThrow(() => store.PutImmutable("releases/a/payload.bin", src),
                "同内容重复写入是幂等重试，必须允许");
        }

        [Test]
        public void 不可变写入_内容冲突_抛冲突异常且不覆盖()
        {
            var store = NewStore();
            store.PutImmutable("releases/a/payload.bin", NewSourceFile("v1.bin", "original"));
            string tampered = NewSourceFile("v2.bin", "tampered");

            var ex = Assert.Throws<ImmutableArtifactConflictException>(
                () => store.PutImmutable("releases/a/payload.bin", tampered));
            StringAssert.Contains(ReleaseStoreErrorCodes.ImmutableConflict, ex.Message);
            Assert.AreEqual("original", Encoding.UTF8.GetString(store.Read("releases/a/payload.bin")),
                "冲突时不可变产物必须保持原内容");
        }

        // ── 可变对象原子替换 ──────────────────────────────────────────────────

        [Test]
        public void 可变写入_覆盖旧对象_原子替换成功()
        {
            var store = NewStore();
            store.PutMutable("current.json", NewSourceFile("p1.json", "{\"v\":1}"));
            store.PutMutable("current.json", NewSourceFile("p2.json", "{\"v\":2}"));

            Assert.AreEqual("{\"v\":2}", Encoding.UTF8.GetString(store.Read("current.json")));
        }

        // ── 枚举 ─────────────────────────────────────────────────────────────

        [Test]
        public void 枚举ReleaseId_只返回含台账的版本目录()
        {
            var store = NewStore();
            store.PutImmutable("releases/1.0.0/rel-a/ledger.json", NewSourceFile("la.json", "{}"));
            store.PutImmutable("releases/1.0.0/rel-b/ledger.json", NewSourceFile("lb.json", "{}"));
            // 无 ledger 的目录不算 release
            store.PutImmutable("releases/1.0.0/orphan/payload.bin", NewSourceFile("o.bin", "x"));

            var ids = store.EnumerateReleaseIds();

            CollectionAssert.AreEquivalent(new[] { "rel-a", "rel-b" }, ids);
        }

        // ── 路径穿越防护 ──────────────────────────────────────────────────────

        [Test]
        public void 路径穿越_逃逸根目录_拒绝()
        {
            var store = NewStore();
            string src = NewSourceFile("x.bin", "x");

            Assert.Throws<InvalidDataException>(() => store.PutImmutable("../escape.bin", src));
            Assert.Throws<InvalidDataException>(() => store.Exists("../../etc/passwd"));
        }

        // ── 部署目标失败关闭闸门 ──────────────────────────────────────────────

        [Test]
        public void BuildOnly_允许空部署目标_返回null()
        {
            Assert.IsNull(ReleaseArtifactStoreFactory.Resolve(ReleaseMode.BuildOnly, string.Empty));
            Assert.IsNull(ReleaseArtifactStoreFactory.Resolve(ReleaseMode.BuildOnly, null));
        }

        [Test]
        public void Publish空目标_失败关闭并携带稳定错误码()
        {
            var ex = Assert.Throws<ReleaseStoreNotConfiguredException>(
                () => ReleaseArtifactStoreFactory.Resolve(ReleaseMode.Publish, string.Empty));
            StringAssert.Contains(ReleaseStoreErrorCodes.StoreNotConfigured, ex.Message);
        }

        [Test]
        public void Promote空目标_失败关闭()
        {
            Assert.Throws<ReleaseStoreNotConfiguredException>(
                () => ReleaseArtifactStoreFactory.Resolve(ReleaseMode.Promote, "   "));
        }

        [Test]
        public void Rollback空目标_失败关闭()
        {
            Assert.Throws<ReleaseStoreNotConfiguredException>(
                () => ReleaseArtifactStoreFactory.Resolve(ReleaseMode.Rollback, null));
        }

        [Test]
        public void VerifyOnly空目标_失败关闭()
        {
            Assert.Throws<ReleaseStoreNotConfiguredException>(
                () => ReleaseArtifactStoreFactory.Resolve(ReleaseMode.VerifyOnly, ""));
        }

        [Test]
        public void 非空目标_四类部署模式均返回目录型Store()
        {
            string root = Path.Combine(_root, "deploy");
            foreach (ReleaseMode mode in new[]
                     { ReleaseMode.Publish, ReleaseMode.Promote, ReleaseMode.Rollback, ReleaseMode.VerifyOnly })
            {
                IReleaseArtifactStore store = ReleaseArtifactStoreFactory.Resolve(mode, root);
                Assert.IsInstanceOf<LocalFileSystemReleaseStore>(store, $"{mode} 应解析目录型 Store");
                StringAssert.Contains("LocalFileSystemReleaseStore", store.Describe());
            }
        }
    }
}
