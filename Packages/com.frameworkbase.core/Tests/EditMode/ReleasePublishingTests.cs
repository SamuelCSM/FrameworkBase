using System;
using System.IO;
using Framework.Editor.Release;
using NUnit.Framework;

namespace Framework.Tests
{
    /// <summary>
    /// 文件系统发布提交测试，验证不可变载荷保护和 version.json 最后切换语义。
    /// </summary>
    public class ReleasePublishingTests
    {
        private string _root;
        private string _source;
        private string _target;

        [SetUp]
        public void SetUp()
        {
            _root = Path.Combine(Path.GetTempPath(), "FrameworkBase-PublishTests", Guid.NewGuid().ToString("N"));
            _source = Path.Combine(_root, "source");
            _target = Path.Combine(_root, "target");
            Directory.CreateDirectory(_source);
            Directory.CreateDirectory(_target);
        }

        [TearDown]
        public void TearDown()
        {
            if (Directory.Exists(_root)) Directory.Delete(_root, true);
        }

        [Test]
        public void 原子发布_签名与清单在载荷之后可见()
        {
            WriteRelease("new-manifest", "new-signature", "new-payload");
            File.WriteAllText(Path.Combine(_target, "version.json"), "old-manifest");

            CreateStep().Execute(CreateContext());

            Assert.AreEqual("new-payload", File.ReadAllText(Path.Combine(
                _target, "payloads", "1.0.0", "code_2", "hash", "HotUpdate.dll.bytes")));
            Assert.AreEqual("new-signature", File.ReadAllText(Path.Combine(_target, "version.json.sig")));
            Assert.AreEqual("new-manifest", File.ReadAllText(Path.Combine(_target, "version.json")));
            Assert.IsFalse(Directory.Exists(Path.Combine(_target, ".frameworkbase-staging", "release-test")));
        }

        [Test]
        public void 不可变路径内容冲突_拒绝覆盖且旧清单保持不变()
        {
            WriteRelease("new-manifest", "new-signature", "new-payload");
            string immutableTarget = Path.Combine(
                _target, "payloads", "1.0.0", "code_2", "hash", "HotUpdate.dll.bytes");
            Directory.CreateDirectory(Path.GetDirectoryName(immutableTarget));
            File.WriteAllText(immutableTarget, "different-existing-payload");
            File.WriteAllText(Path.Combine(_target, "version.json"), "old-manifest");

            Assert.Throws<IOException>(() => CreateStep().Execute(CreateContext()));

            Assert.AreEqual("different-existing-payload", File.ReadAllText(immutableTarget));
            Assert.AreEqual("old-manifest", File.ReadAllText(Path.Combine(_target, "version.json")));
        }

        private void WriteRelease(string manifest, string signature, string payload)
        {
            string payloadPath = Path.Combine(
                _source, "payloads", "1.0.0", "code_2", "hash", "HotUpdate.dll.bytes");
            Directory.CreateDirectory(Path.GetDirectoryName(payloadPath));
            File.WriteAllText(payloadPath, payload);
            File.WriteAllText(Path.Combine(_source, "version.json.sig"), signature);
            File.WriteAllText(Path.Combine(_source, "version.json"), manifest);
        }

        private ReleaseContext CreateContext() => new ReleaseContext
        {
            ReleaseId = "release-test",
            ServerDataDir = _source,
            Profile = new ReleaseProfile { UploadRoot = _target },
            Log = _ => { },
        };

        private static ReleasePublishingSteps.AtomicPublishArtifacts CreateStep() =>
            new ReleasePublishingSteps.AtomicPublishArtifacts();
    }
}
