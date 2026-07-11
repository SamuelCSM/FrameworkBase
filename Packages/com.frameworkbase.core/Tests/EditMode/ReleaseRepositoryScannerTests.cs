using System;
using System.IO;
using Framework.Editor.Release;
using Framework.HotUpdate;
using NUnit.Framework;
using UnityEngine;

namespace Framework.Tests
{
    /// <summary>
    /// Release Center 仓库扫描层测试：面板展示的一切事实（作用域、指针、状态推导）
    /// 都来自该层，必须可脱离 UI 验证（面板只做编排与展示的铁律前提）。
    /// </summary>
    public class ReleaseRepositoryScannerTests
    {
        private string _root;

        [SetUp]
        public void SetUp()
        {
            _root = Path.Combine(Path.GetTempPath(), "FrameworkBase-ScannerTests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_root);
        }

        [TearDown]
        public void TearDown()
        {
            if (Directory.Exists(_root)) Directory.Delete(_root, true);
        }

        [Test]
        public void 扫描作用域_只识别含releases或指针的渠道目录()
        {
            CreateRelease("dev/windows/default", "rid-a", "1.0", 2, complete: true);
            Directory.CreateDirectory(Path.Combine(_root, "dev", "windows", "not-a-channel-dir"));
            Directory.CreateDirectory(Path.Combine(_root, "qa", "android", "default"));
            File.WriteAllText(Path.Combine(_root, "qa", "android", "default", "current.json"), "{}");

            var scopes = ReleaseRepositoryScanner.ScanScopes(_root);
            CollectionAssert.AreEqual(new[] { "dev/windows/default", "qa/android/default" }, scopes);
        }

        [Test]
        public void 渠道快照_指针推导Active与Previous_缺文件判Incomplete()
        {
            CreateRelease("dev/windows/default", "rid-active", "1.0", 3, complete: true);
            CreateRelease("dev/windows/default", "rid-previous", "1.0", 2, complete: true);
            CreateRelease("dev/windows/default", "rid-broken", "1.0", 1, complete: false);
            WritePointer("dev/windows/default", "rid-active", "rid-previous");

            ChannelSnapshot snapshot = ReleaseRepositoryScanner.LoadChannel(_root, "dev/windows/default");
            Assert.IsNotNull(snapshot.Pointer);
            Assert.AreEqual("rid-active", snapshot.Pointer.ReleaseId);
            Assert.AreEqual(3, snapshot.Releases.Count);
            Assert.AreEqual(ReleaseDisplayState.Active, Find(snapshot, "rid-active").State);
            Assert.AreEqual(ReleaseDisplayState.Previous, Find(snapshot, "rid-previous").State);
            Assert.AreEqual(ReleaseDisplayState.Incomplete, Find(snapshot, "rid-broken").State);
            Assert.AreEqual(2, Find(snapshot, "rid-previous").CodeVersion);
        }

        [Test]
        public void 无指针渠道_完整目录一律Archived()
        {
            CreateRelease("qa/android/default", "rid-x", "2.0", 5, complete: true);
            ChannelSnapshot snapshot = ReleaseRepositoryScanner.LoadChannel(_root, "qa/android/default");
            Assert.IsNull(snapshot.Pointer);
            Assert.AreEqual(ReleaseDisplayState.Archived, Find(snapshot, "rid-x").State);
        }

        private static ReleaseEntryView Find(ChannelSnapshot snapshot, string releaseId) =>
            snapshot.Releases.Find(entry => entry.ReleaseId == releaseId);

        private void CreateRelease(string scope, string releaseId, string appVersion, int codeVersion, bool complete)
        {
            string dir = Path.Combine(
                _root, scope.Replace('/', Path.DirectorySeparatorChar), "releases", appVersion, releaseId);
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "version.json"), "{}");
            if (!complete) return;
            File.WriteAllText(Path.Combine(dir, "version.json.sig"), "sig");
            File.WriteAllText(Path.Combine(dir, "ledger.json"), JsonUtility.ToJson(new LedgerStub
            {
                ReleaseId = releaseId,
                AppVersion = appVersion,
                CodeVersion = codeVersion,
                ResourceVersion = 1,
                GitCommit = "abcdef1234567890",
                GeneratedAtUtc = DateTime.UtcNow.ToString("O"),
                Environment = scope.Split('/')[0],
            }));
        }

        private void WritePointer(string scope, string activeId, string previousId)
        {
            var pointer = new CurrentPointer
            {
                SchemaVersion = 1,
                ReleaseId = activeId,
                PreviousReleaseId = previousId,
                ManifestPath = $"releases/1.0/{activeId}/version.json",
            };
            File.WriteAllText(
                Path.Combine(_root, scope.Replace('/', Path.DirectorySeparatorChar), "current.json"),
                JsonUtility.ToJson(pointer));
        }

        [Serializable]
        private sealed class LedgerStub
        {
            public string ReleaseId;
            public string GeneratedAtUtc;
            public string GitCommit;
            public string Environment;
            public string AppVersion;
            public int ResourceVersion;
            public int CodeVersion;
        }
    }
}
