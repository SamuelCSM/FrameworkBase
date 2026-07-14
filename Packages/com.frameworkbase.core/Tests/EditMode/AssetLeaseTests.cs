using System;
using System.Threading;
using Cysharp.Threading.Tasks;
using NUnit.Framework;
using UnityEngine;

namespace Framework.Tests
{
    public class AssetLeaseTests
    {
        private static T Wait<T>(UniTask<T> task) => task.GetAwaiter().GetResult();

        [Test]
        public void Dispose_重复调用只归还一次()
        {
            var asset = ScriptableObject.CreateInstance<TestAsset>();
            int releases = 0;
            var lease = new AssetLease<TestAsset>("audio/click", asset, _ => releases++);

            lease.Dispose();
            lease.Dispose();

            Assert.AreEqual(1, releases);
            Assert.IsTrue(lease.IsDisposed);
            UnityEngine.Object.DestroyImmediate(asset);
        }

        [Test]
        public void 加载失败_不创建Lease也不重复归还()
        {
            int releases = 0;

            AssetLease<TestAsset> lease = Wait(AssetLeaseCoordinator.AcquireStartedAsync(
                "missing",
                UniTask.FromResult<TestAsset>(null),
                _ => releases++,
                CancellationToken.None));

            Assert.IsNull(lease);
            Assert.AreEqual(0, releases, "加载器自身负责失败回滚，Lease 协调器不能二次归还");
        }

        [Test]
        public void 取消共享加载等待_调用方立即取消且迟到资源自动归还()
        {
            var gate = new UniTaskCompletionSource<TestAsset>();
            var cts = new CancellationTokenSource();
            int releases = 0;

            UniTask<AssetLease<TestAsset>> pending = AssetLeaseCoordinator.AcquireStartedAsync(
                "audio/click",
                gate.Task,
                _ => releases++,
                cts.Token);

            cts.Cancel();
            bool canceled = false;
            try
            {
                Wait(pending);
            }
            catch (OperationCanceledException)
            {
                canceled = true;
            }
            Assert.IsTrue(canceled, "调用方必须立即观察到取消");
            Assert.AreEqual(0, releases, "底层加载未完成前不能提前释放共享句柄");

            var asset = ScriptableObject.CreateInstance<TestAsset>();
            gate.TrySetResult(asset);

            Assert.AreEqual(1, releases, "取消等待者预占的引用必须在迟到资源完成后归还");
            UnityEngine.Object.DestroyImmediate(asset);
            cts.Dispose();
        }

        private sealed class TestAsset : ScriptableObject { }
    }
}
