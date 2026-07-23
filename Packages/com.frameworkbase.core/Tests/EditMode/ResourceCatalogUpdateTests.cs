using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using Cysharp.Threading.Tasks;
using Framework.Core;
using Framework.HotUpdate;
using NUnit.Framework;

namespace Framework.Tests
{
    /// <summary>
    /// Catalog 更新失败传播单测（任务书第三章第 10 点）：
    /// 检查成功无更新 / 检查成功有更新 / 检查失败 / 更新失败 / 更新抛异常 / 操作取消 /
    /// Catalog 失败不得推进 / 尺寸查询失败不得推进 / 下载失败不得推进。
    /// 底层 Addressables 经 IAddressablesCatalogService 假实现注入，全程不碰真实 Addressables。
    /// </summary>
    public class ResourceCatalogUpdateTests
    {
        /// <summary>可编程假实现：逐项注入检查/更新/尺寸查询行为，复现所有失败路径。</summary>
        private sealed class FakeCatalogService : IAddressablesCatalogService
        {
            public Func<List<string>> CheckBehavior = () => new List<string>();
            public Action UpdateBehavior = () => { };
            public Func<long> SizeBehavior = () => 0;
            public Func<string, byte[]> CatalogBytesBehavior = _ => Array.Empty<byte>();
            public int CheckCalls;
            public int UpdateCalls;
            public int CatalogBytesCalls;

            public UniTask<List<string>> CheckForCatalogUpdatesAsync(CancellationToken cancellationToken)
            {
                CheckCalls++;
                cancellationToken.ThrowIfCancellationRequested();
                return UniTask.FromResult(CheckBehavior());
            }

            public UniTask UpdateCatalogsAsync(IReadOnlyList<string> catalogIds, CancellationToken cancellationToken)
            {
                UpdateCalls++;
                cancellationToken.ThrowIfCancellationRequested();
                UpdateBehavior();
                return UniTask.CompletedTask;
            }

            public UniTask<long> GetDownloadSizeAsync(object key, CancellationToken cancellationToken)
            {
                cancellationToken.ThrowIfCancellationRequested();
                return UniTask.FromResult(SizeBehavior());
            }

            public UniTask<byte[]> DownloadRemoteCatalogBytesAsync(string catalogId, CancellationToken cancellationToken)
            {
                CatalogBytesCalls++;
                cancellationToken.ThrowIfCancellationRequested();
                return UniTask.FromResult(CatalogBytesBehavior(catalogId));
            }
        }

        private FakeCatalogService _service;
        private CatalogUpdateFlow _flow;

        [SetUp]
        public void SetUp()
        {
            _service = new FakeCatalogService();
            _flow = new CatalogUpdateFlow(_service);
        }

        private static T Wait<T>(UniTask<T> task) => task.GetAwaiter().GetResult();

        // ── Catalog 检查/更新六种终态 ─────────────────────────────────────────

        [Test]
        public void 检查成功且无更新_返回UpToDate且允许继续()
        {
            CatalogUpdateResult result = Wait(_flow.CheckAndUpdateAsync());

            Assert.AreEqual(CatalogUpdateStatus.UpToDate, result.Status);
            Assert.IsTrue(result.Succeeded, "无更新是成功终态，允许继续资源下载");
            Assert.IsFalse(result.CatalogChanged);
            Assert.AreEqual(0, result.UpdatedCatalogCount);
            Assert.AreEqual(0, _service.UpdateCalls, "无更新时不得调用 UpdateCatalogs");
        }

        [Test]
        public void 检查成功且有更新_更新成功_返回Updated()
        {
            _service.CheckBehavior = () => new List<string> { "catalog_a", "catalog_b" };

            CatalogUpdateResult result = Wait(_flow.CheckAndUpdateAsync());

            Assert.AreEqual(CatalogUpdateStatus.Updated, result.Status);
            Assert.IsTrue(result.Succeeded);
            Assert.IsTrue(result.CatalogChanged);
            Assert.AreEqual(2, result.UpdatedCatalogCount);
            Assert.AreEqual(1, _service.UpdateCalls);
        }

        [Test]
        public void 检查操作失败_返回CheckFailed_绝不折叠成无更新()
        {
            _service.CheckBehavior = () => throw new CatalogOperationException("模拟 CDN 404");

            CatalogUpdateResult result = Wait(_flow.CheckAndUpdateAsync());

            Assert.AreEqual(CatalogUpdateStatus.CheckFailed, result.Status);
            Assert.IsFalse(result.Succeeded, "检查失败必须是失败终态");
            Assert.AreEqual(CatalogUpdateErrorCodes.CheckOperationFailed, result.ErrorCode);
            StringAssert.Contains("404", result.Message);
        }

        [Test]
        public void 检查抛通用异常_返回CheckFailed_错误码区分异常路径()
        {
            _service.CheckBehavior = () => throw new InvalidOperationException("模拟 Addressables 内部异常");

            CatalogUpdateResult result = Wait(_flow.CheckAndUpdateAsync());

            Assert.AreEqual(CatalogUpdateStatus.CheckFailed, result.Status);
            Assert.AreEqual(CatalogUpdateErrorCodes.CheckException, result.ErrorCode);
        }

        [Test]
        public void 更新操作失败_返回UpdateFailed()
        {
            _service.CheckBehavior = () => new List<string> { "catalog_a" };
            _service.UpdateBehavior = () => throw new CatalogOperationException("模拟 catalog 下载失败");

            CatalogUpdateResult result = Wait(_flow.CheckAndUpdateAsync());

            Assert.AreEqual(CatalogUpdateStatus.UpdateFailed, result.Status);
            Assert.IsFalse(result.Succeeded);
            Assert.AreEqual(CatalogUpdateErrorCodes.UpdateOperationFailed, result.ErrorCode);
        }

        [Test]
        public void 更新抛通用异常_返回UpdateFailed_错误码区分异常路径()
        {
            _service.CheckBehavior = () => new List<string> { "catalog_a" };
            _service.UpdateBehavior = () => throw new NullReferenceException("模拟激活期空引用");

            CatalogUpdateResult result = Wait(_flow.CheckAndUpdateAsync());

            Assert.AreEqual(CatalogUpdateStatus.UpdateFailed, result.Status);
            Assert.AreEqual(CatalogUpdateErrorCodes.UpdateException, result.ErrorCode);
        }

        [Test]
        public void 操作取消_返回Canceled终态而非抛异常()
        {
            using var cts = new CancellationTokenSource();
            cts.Cancel();

            CatalogUpdateResult result = Wait(_flow.CheckAndUpdateAsync(cts.Token));

            Assert.AreEqual(CatalogUpdateStatus.Canceled, result.Status);
            Assert.IsTrue(result.WasCanceled);
            Assert.IsFalse(result.Succeeded, "取消必须中止本次启动更新");
            Assert.AreEqual(CatalogUpdateErrorCodes.Canceled, result.ErrorCode);
        }

        [Test]
        public void 检查返回null_判定Invalid失败关闭()
        {
            _service.CheckBehavior = () => null;

            CatalogUpdateResult result = Wait(_flow.CheckAndUpdateAsync());

            Assert.AreEqual(CatalogUpdateStatus.Invalid, result.Status);
            Assert.IsFalse(result.Succeeded);
            Assert.AreEqual(CatalogUpdateErrorCodes.InvalidResult, result.ErrorCode);
        }

        // ── ADR-009：应用前对已验签 Catalog 内容身份校验 ─────────────────────

        [Test]
        public void 资源版本增长_Catalog身份匹配_验签通过并激活()
        {
            byte[] bytes = SampleCatalogBytes();
            _service.CheckBehavior = () => new List<string> { "https://cdn.example.com/addr/catalog_x.json" };
            _service.CatalogBytesBehavior = _ => bytes;

            CatalogUpdateResult result = Wait(_flow.CheckAndUpdateAsync(default, IdentityFor(bytes)));

            Assert.AreEqual(CatalogUpdateStatus.Updated, result.Status);
            Assert.AreEqual(1, _service.CatalogBytesCalls, "应用前必须下载并验签 Catalog 字节");
            Assert.AreEqual(1, _service.UpdateCalls, "验签通过才允许激活");
        }

        [Test]
        public void 资源版本增长_Catalog哈希不符_IntegrityFailed且绝不激活()
        {
            _service.CheckBehavior = () => new List<string> { "https://cdn.example.com/addr/catalog_x.json" };
            _service.CatalogBytesBehavior = _ => Encoding.UTF8.GetBytes("被篡改的 catalog 内容");

            CatalogUpdateResult result = Wait(_flow.CheckAndUpdateAsync(default, IdentityFor(SampleCatalogBytes())));

            Assert.AreEqual(CatalogUpdateStatus.IntegrityFailed, result.Status);
            Assert.IsFalse(result.Succeeded);
            Assert.AreEqual(CatalogUpdateErrorCodes.IntegrityFailed, result.ErrorCode);
            Assert.AreEqual(0, _service.UpdateCalls, "身份不符绝不允许激活未签名的资源目录");
        }

        [Test]
        public void 资源版本增长_更新集无匹配身份的Catalog_IntegrityFailed()
        {
            // 多个待更新 catalog 但无一匹配已验签身份的文件名：定位不到即失败关闭，且不下载任何字节。
            _service.CheckBehavior = () => new List<string> { "catalog_a.json", "catalog_b.json" };

            var identity = IdentityFor(SampleCatalogBytes());
            identity.FileName = "catalog_x.json";
            CatalogUpdateResult result = Wait(_flow.CheckAndUpdateAsync(default, identity));

            Assert.AreEqual(CatalogUpdateStatus.IntegrityFailed, result.Status);
            Assert.AreEqual(0, _service.CatalogBytesCalls);
            Assert.AreEqual(0, _service.UpdateCalls);
        }

        [Test]
        public void Catalog字节下载失败_IntegrityFailed失败关闭()
        {
            _service.CheckBehavior = () => new List<string> { "https://cdn.example.com/addr/catalog_x.json" };
            _service.CatalogBytesBehavior = _ => throw new CatalogOperationException("模拟 Catalog 下载 500");

            CatalogUpdateResult result = Wait(_flow.CheckAndUpdateAsync(default, IdentityFor(SampleCatalogBytes())));

            Assert.AreEqual(CatalogUpdateStatus.IntegrityFailed, result.Status);
            Assert.AreEqual(0, _service.UpdateCalls);
        }

        [Test]
        public void 无期望身份_不触发验签_保持原行为()
        {
            // 纯代码更新/老项目：expectedCatalog 为 null，不下载不验签，行为与历史一致。
            _service.CheckBehavior = () => new List<string> { "catalog_a" };

            CatalogUpdateResult result = Wait(_flow.CheckAndUpdateAsync());

            Assert.AreEqual(CatalogUpdateStatus.Updated, result.Status);
            Assert.AreEqual(0, _service.CatalogBytesCalls, "无期望身份时不应触发 Catalog 验签下载");
            Assert.AreEqual(1, _service.UpdateCalls);
        }

        // ── 下载尺寸查询：失败与"无需下载"是两个结果 ─────────────────────────

        [Test]
        public void 尺寸查询成功零字节_是无需下载而非失败()
        {
            DownloadSizeResult result = Wait(_flow.GetDownloadSizeAsync("remote"));

            Assert.IsTrue(result.Succeeded);
            Assert.AreEqual(0, result.Bytes);
        }

        [Test]
        public void 尺寸查询抛异常_返回Failed_不得吞成零字节()
        {
            _service.SizeBehavior = () => throw new CatalogOperationException("模拟尺寸查询网络失败");

            DownloadSizeResult result = Wait(_flow.GetDownloadSizeAsync("remote"));

            Assert.AreEqual(DownloadSizeStatus.Failed, result.Status);
            Assert.IsFalse(result.Succeeded, "查询失败绝不能与无需下载混同");
        }

        [Test]
        public void 尺寸查询取消_返回Canceled()
        {
            using var cts = new CancellationTokenSource();
            cts.Cancel();

            DownloadSizeResult result = Wait(_flow.GetDownloadSizeAsync("remote", cts.Token));

            Assert.AreEqual(DownloadSizeStatus.Canceled, result.Status);
            Assert.IsFalse(result.Succeeded);
        }

        // ── Step4 失败传播（不得推进 ResourceVersion 的判定源头）──────────────
        // LaunchFlow 对 Success=false 一律返回 Failed 并跳过 CommitHotUpdate，
        // 因此这里对 EvaluateResourceUpdate 的断言即"失败不得提交版本"的单元级证明；
        // 事务级二次防线（VersionManager 只按已确认事实提交）由内容事务测试覆盖。

        [Test]
        public void Catalog失败_Step4结论失败_禁止提交版本()
        {
            CatalogUpdateResult catalogFailed = CatalogUpdateResult.Failed(
                CatalogUpdateStatus.CheckFailed, CatalogUpdateErrorCodes.CheckOperationFailed, "模拟检查失败");

            var step4 = LaunchFlowUpdateExecutor.EvaluateResourceUpdate(catalogFailed, null, null);

            Assert.IsFalse(step4.Success, "Catalog 失败必须让整个 Step4 失败");
            Assert.AreEqual(LaunchFlowUpdateExecutor.ResourceUpdateStageErrors.CatalogFailed, step4.ErrorCode);
            StringAssert.Contains(CatalogUpdateErrorCodes.CheckOperationFailed, step4.Message);
        }

        [Test]
        public void 尺寸查询失败_Step4结论失败_禁止提交版本()
        {
            var step4 = LaunchFlowUpdateExecutor.EvaluateResourceUpdate(
                CatalogUpdateResult.UpToDate(),
                DownloadSizeResult.Failed("模拟查询失败"),
                null);

            Assert.IsFalse(step4.Success, "尺寸查询失败不能被当成无需下载");
            Assert.AreEqual(LaunchFlowUpdateExecutor.ResourceUpdateStageErrors.SizeQueryFailed, step4.ErrorCode);
        }

        [Test]
        public void 下载失败_Step4结论失败_禁止提交版本()
        {
            var step4 = LaunchFlowUpdateExecutor.EvaluateResourceUpdate(
                CatalogUpdateResult.Updated(1),
                DownloadSizeResult.Ok(1024),
                downloadOk: false);

            Assert.IsFalse(step4.Success);
            Assert.AreEqual(LaunchFlowUpdateExecutor.ResourceUpdateStageErrors.DownloadFailed, step4.ErrorCode);
        }

        [Test]
        public void 全链路成功_Step4结论成功()
        {
            var noDownload = LaunchFlowUpdateExecutor.EvaluateResourceUpdate(
                CatalogUpdateResult.UpToDate(), DownloadSizeResult.Ok(0), null);
            var downloaded = LaunchFlowUpdateExecutor.EvaluateResourceUpdate(
                CatalogUpdateResult.Updated(1), DownloadSizeResult.Ok(2048), downloadOk: true);

            Assert.IsTrue(noDownload.Success, "Catalog 成功 + 0 字节 = 真正的无需下载");
            Assert.IsTrue(downloaded.Success);
            Assert.IsEmpty(noDownload.ErrorCode);
            Assert.IsEmpty(downloaded.ErrorCode);
        }

        [Test]
        public void 取消终态_Step4结论失败()
        {
            CatalogUpdateResult canceled = CatalogUpdateResult.Failed(
                CatalogUpdateStatus.Canceled, CatalogUpdateErrorCodes.Canceled, "取消");

            var step4 = LaunchFlowUpdateExecutor.EvaluateResourceUpdate(canceled, null, null);

            Assert.IsFalse(step4.Success, "取消必须中止启动更新");
        }

        // ── ADR-009 验签测试辅助 ─────────────────────────────────────────────

        private static byte[] SampleCatalogBytes() => Encoding.UTF8.GetBytes("{\"m_LocatorId\":\"catalog\",\"v\":2}");

        /// <summary>为给定字节构造与之匹配的已验签身份（Size + 真实 SHA-256）。</summary>
        private static ResourceCatalogFile IdentityFor(byte[] bytes, string fileName = "catalog_x.json")
        {
            using (SHA256 sha = SHA256.Create())
            {
                byte[] hash = sha.ComputeHash(bytes);
                var sb = new StringBuilder(hash.Length * 2);
                foreach (byte b in hash)
                    sb.Append(b.ToString("x2"));
                return new ResourceCatalogFile { FileName = fileName, Size = bytes.Length, SHA256 = sb.ToString() };
            }
        }
    }
}
