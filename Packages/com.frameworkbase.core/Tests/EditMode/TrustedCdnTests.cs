using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Threading;
using Cysharp.Threading.Tasks;
using Framework.Core;
using Framework.HotUpdate;
using NUnit.Framework;

namespace Framework.Tests
{
    public class TrustedCdnTests
    {
        private sealed class FakeTransport : IFileDownloadTransport
        {
            public readonly List<string> Urls = new List<string>();
            public readonly List<bool> DestinationExistedAtStart = new List<bool>();
            public Func<string, byte[]> Payload;

            public UniTask<bool> DownloadFileAsync(
                string url,
                string savePath,
                Action<float> onProgress,
                bool forceRefresh,
                CancellationToken cancellationToken)
            {
                cancellationToken.ThrowIfCancellationRequested();
                Urls.Add(url);
                DestinationExistedAtStart.Add(File.Exists(savePath));
                byte[] payload = Payload?.Invoke(url);
                if (payload == null) return UniTask.FromResult(false);
                Directory.CreateDirectory(Path.GetDirectoryName(savePath));
                File.WriteAllBytes(savePath, payload);
                onProgress?.Invoke(1f);
                return UniTask.FromResult(true);
            }
        }

        private string _root;

        [SetUp]
        public void SetUp()
        {
            _root = Path.Combine(Path.GetTempPath(), "FrameworkBase-CdnTests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_root);
        }

        [TearDown]
        public void TearDown()
        {
            if (Directory.Exists(_root)) Directory.Delete(_root, true);
        }

        [Test]
        public void 包内可信端点_同一相对路径映射不同Host()
        {
            TrustedCdnRouteSet routes = CreateRoutes();
            IReadOnlyList<TrustedCdnRoute> candidates = routes.Resolve("releases/r1/HotUpdate.dll.bytes");

            Assert.AreEqual(2, candidates.Count);
            Assert.AreEqual("https://primary.example.com/updates/prod/windows/default/releases/r1/HotUpdate.dll.bytes", candidates[0].Url);
            Assert.AreEqual("https://backup.example.net/mirror/prod/windows/default/releases/r1/HotUpdate.dll.bytes", candidates[1].Url);
        }

        [Test]
        public void 环境错配_明文传输_重复Origin_全部失败关闭()
        {
            Assert.IsFalse(TrustedCdnRouteSet.TryCreate(
                "https://primary.example.com/root/windows/default",
                Array.Empty<UpdateCdnEndpointDefinition>(),
                "prod", null, out _, out _),
                "主更新根也必须包含独立环境路径段");

            Assert.IsFalse(TrustedCdnRouteSet.TryCreate(
                "https://primary.example.com/root/prod/windows/default",
                new[] { Endpoint("bad-env", "qa", "https://qa.example.net/root/qa/windows/default") },
                "prod", null, out _, out _));

            Assert.IsFalse(TrustedCdnRouteSet.TryCreate(
                "https://primary.example.com/root/prod/windows/default",
                new[] { Endpoint("plain", "prod", "http://backup.example.net/root/prod/windows/default") },
                "prod", null, out _, out _));

            Assert.IsFalse(TrustedCdnRouteSet.TryCreate(
                "https://primary.example.com/root-a/prod/windows/default",
                new[] { Endpoint("same-origin", "prod", "https://primary.example.com/root-b/prod/windows/default") },
                "prod", null, out _, out _));
        }

        [Test]
        public void 相对路径与主根边界_拒绝跨源_Query和目录穿越()
        {
            TrustedCdnRouteSet routes = CreateRoutes();
            Assert.IsTrue(routes.TryGetPrimaryRelativePath(
                "https://primary.example.com/updates/prod/windows/default/releases/r1/a.dll.bytes",
                out string relative));
            Assert.AreEqual("releases/r1/a.dll.bytes", relative);

            Assert.IsFalse(routes.TryGetPrimaryRelativePath(
                "https://evil.example.com/updates/prod/windows/default/releases/r1/a.dll.bytes",
                out _));
            Assert.IsFalse(routes.TryGetPrimaryRelativePath(
                "https://primary.example.com/updates/prod/windows/default/releases/r1/a.dll.bytes?token=x",
                out _));
            Assert.IsFalse(routes.TryGetPrimaryRelativePath(
                "https://primary.example.com/updates/prod/windows/default/releases%2Fr1%2Fa.dll.bytes",
                out _));
            Assert.Throws<InvalidDataException>(() => routes.Resolve("releases/../secret"));
        }

        [Test]
        public void 主CDN网络失败_回退备用CDN并保留内容身份()
        {
            byte[] expected = { 1, 2, 3, 4 };
            var transport = new FakeTransport
            {
                Payload = url => url.Contains("backup.example.net") ? expected : null,
            };
            string path = Path.Combine(_root, "patch.bin");
            var client = new TrustedCdnDownloadClient(CreateRoutes(), transport);

            CdnDownloadResult result = Wait(client.DownloadAsync(
                "releases/r1/HotUpdate.dll.bytes",
                path,
                (route, downloaded, token) => UniTask.FromResult(
                    BytesEqual(File.ReadAllBytes(downloaded), expected)
                        ? CdnValidationResult.Valid()
                        : CdnValidationResult.Failed(CdnFailureKind.Integrity, "hash mismatch"))));

            Assert.IsTrue(result.Success);
            Assert.AreEqual("backup", result.EndpointName);
            Assert.AreEqual(2, result.Attempts);
            Assert.AreEqual(2, transport.Urls.Count);
            CollectionAssert.AreEqual(expected, File.ReadAllBytes(path));
        }

        [Test]
        public void 清单与伴生签名_绑定同一可信端点()
        {
            TrustedCdnRouteSet routes = CreateRoutes();
            TrustedCdnRoute manifest = routes.Resolve("releases/r1/version.json")[1];
            TrustedCdnRoute signature = routes.ResolveForEndpoint(
                manifest.Endpoint,
                "releases/r1/version.json.sig");

            Assert.AreSame(manifest.Endpoint, signature.Endpoint);
            Assert.AreEqual("backup.example.net", new Uri(signature.Url).Host);
        }

        [Test]
        public void 主CDN哈希异常_立即隔离且跨Host禁止复用部分文件()
        {
            byte[] bad = { 9, 9, 9 };
            byte[] expected = { 1, 2, 3 };
            TrustedCdnRouteSet routes = CreateRoutes();
            var transport = new FakeTransport
            {
                Payload = url => url.Contains("primary.example.com") ? bad : expected,
            };
            string path = Path.Combine(_root, "patch.bin");
            var client = new TrustedCdnDownloadClient(routes, transport);

            CdnDownloadResult result = Wait(client.DownloadAsync(
                "releases/r1/HotUpdate.dll.bytes",
                path,
                (route, downloaded, token) => UniTask.FromResult(
                    BytesEqual(File.ReadAllBytes(downloaded), expected)
                        ? CdnValidationResult.Valid()
                        : CdnValidationResult.Failed(CdnFailureKind.Integrity, "sha256 mismatch"))));

            Assert.IsTrue(result.Success);
            CollectionAssert.AreEqual(new[] { false, false }, transport.DestinationExistedAtStart,
                "没有 ETag/不可变性证明时，跨 Host 必须全量重下而非拼接旧文件");
            IReadOnlyList<TrustedCdnRoute> next = routes.Resolve("releases/r1/next.dll.bytes");
            Assert.AreEqual(1, next.Count, "完整性异常 Host 应立即进入隔离期");
            Assert.AreEqual("backup", next[0].Endpoint.Name);
        }

        [Test]
        public void 熔断器_连续网络失败后冷却并允许半开探测()
        {
            long now = 1000;
            var health = new CdnHealthTracker(
                transportFailureThreshold: 2,
                transportCooldownMilliseconds: 100,
                integrityCooldownMilliseconds: 500,
                nowMilliseconds: () => now);
            TrustedCdnRouteSet routes = CreateRoutes(health);
            TrustedCdnEndpoint primary = routes.Endpoints[0];

            routes.ReportFailure(primary, CdnFailureKind.Transport);
            Assert.AreEqual(2, routes.Resolve("current.json").Count);
            routes.ReportFailure(primary, CdnFailureKind.Transport);
            Assert.AreEqual(1, routes.Resolve("current.json").Count);

            now += 101;
            Assert.AreEqual(2, routes.Resolve("current.json").Count, "冷却后允许一次恢复探测");
            routes.ReportSuccess(primary);
            Assert.AreEqual(2, routes.Resolve("current.json").Count);
        }

        [Test]
        public void 内容身份_必须绑定ManifestId相对路径长度和SHA256()
        {
            string hash = Sha256(new byte[] { 1 });
            Assert.IsTrue(TrustedContentIdentity.TryCreate(
                Guid.NewGuid().ToString("D"), "releases/r1/a.dll.bytes", 1, hash,
                out TrustedContentIdentity identity, out string reason), reason);
            Assert.AreEqual("releases/r1/a.dll.bytes", identity.RelativePath);

            Assert.IsFalse(TrustedContentIdentity.TryCreate(
                "not-guid", "releases/r1/a.dll.bytes", 1, hash, out _, out _));
            Assert.IsFalse(TrustedContentIdentity.TryCreate(
                Guid.NewGuid().ToString("D"), "../a.dll.bytes", 1, hash, out _, out _));
        }

        private static TrustedCdnRouteSet CreateRoutes(CdnHealthTracker health = null)
        {
            bool valid = TrustedCdnRouteSet.TryCreate(
                "https://primary.example.com/updates/prod/windows/default",
                new[]
                {
                    Endpoint("backup", "prod", "https://backup.example.net/mirror/prod/windows/default"),
                },
                "prod",
                health,
                out TrustedCdnRouteSet routes,
                out string reason);
            Assert.IsTrue(valid, reason);
            return routes;
        }

        private static UpdateCdnEndpointDefinition Endpoint(string name, string env, string url) =>
            new UpdateCdnEndpointDefinition { Name = name, AppEnv = env, BaseUrl = url };

        private static T Wait<T>(UniTask<T> task) => task.GetAwaiter().GetResult();

        private static bool BytesEqual(byte[] left, byte[] right)
        {
            if (left == null || right == null || left.Length != right.Length) return false;
            for (int i = 0; i < left.Length; i++)
                if (left[i] != right[i]) return false;
            return true;
        }

        private static string Sha256(byte[] bytes)
        {
            using (SHA256 sha = SHA256.Create())
                return BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", string.Empty).ToLowerInvariant();
        }
    }
}
