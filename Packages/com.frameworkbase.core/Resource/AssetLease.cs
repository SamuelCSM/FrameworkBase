using System;
using System.Threading;
using Cysharp.Threading.Tasks;
using UnityEngine;

namespace Framework
{
    /// <summary>
    /// 一次资源借用的所有权凭证。每次成功借用都必须独立释放；同一地址的多个 Lease
    /// 可以共享底层 Addressables 句柄，但彼此的逻辑所有权互不影响。
    /// </summary>
    /// <typeparam name="T">资源类型。</typeparam>
    public sealed class AssetLease<T> : IDisposable where T : UnityEngine.Object
    {
        private Action<string> _release;

        internal AssetLease(string address, T asset, Action<string> release)
        {
            if (string.IsNullOrWhiteSpace(address))
                throw new ArgumentException("资源地址不能为空。", nameof(address));

            Address = address;
            Asset = asset != null ? asset : throw new ArgumentNullException(nameof(asset));
            _release = release ?? throw new ArgumentNullException(nameof(release));
        }

        /// <summary>资源地址。</summary>
        public string Address { get; }

        /// <summary>本次借用持有的资源。</summary>
        public T Asset { get; }

        /// <summary>本 Lease 是否已经释放。</summary>
        public bool IsDisposed => Volatile.Read(ref _release) == null;

        /// <summary>
        /// 幂等释放本次逻辑引用。允许 Stop、自然结束、Shutdown 等竞态路径同时调用，
        /// 最终只有第一个调用者执行底层引用归还。
        /// </summary>
        public void Dispose()
        {
            Action<string> release = Interlocked.Exchange(ref _release, null);
            release?.Invoke(Address);
        }
    }

    /// <summary>
    /// 将共享的异步加载转换成调用方独占的 Lease，并负责取消等待后的迟到引用归还。
    /// 取消某个等待者不会中止共享底层加载，也不会影响同地址的其它等待者。
    /// </summary>
    internal static class AssetLeaseCoordinator
    {
        internal static async UniTask<AssetLease<T>> AcquireStartedAsync<T>(
            string address,
            UniTask<T> startedLoad,
            Action<string> release,
            CancellationToken cancellationToken) where T : UnityEngine.Object
        {
            if (string.IsNullOrWhiteSpace(address))
                throw new ArgumentException("资源地址不能为空。", nameof(address));
            if (release == null)
                throw new ArgumentNullException(nameof(release));

            // Preserve 允许取消分支在调用方已经返回后继续观察同一个加载结果，并在资源
            // 最终到达时归还这名等待者预占的引用。UniTask 默认不保证可被 await 两次。
            UniTask<T> preservedLoad = startedLoad.Preserve();

            try
            {
                T asset = await preservedLoad.AttachExternalCancellation(cancellationToken);
                if (asset == null)
                    return null; // ResourceManager 的失败路径已经回滚引用。

                return new AssetLease<T>(address, asset, release);
            }
            catch (OperationCanceledException)
            {
                ReleaseWhenLoadCompletesAsync(preservedLoad, address, release).Forget();
                throw;
            }
        }

        private static async UniTaskVoid ReleaseWhenLoadCompletesAsync<T>(
            UniTask<T> preservedLoad,
            string address,
            Action<string> release) where T : UnityEngine.Object
        {
            try
            {
                T asset = await preservedLoad;
                if (asset != null)
                    release(address);
            }
            catch (Exception ex)
            {
                // ResourceManager 正常会把加载异常转换为 null；此处仍做最后一道隔离，
                // 避免取消后的后台收尾产生未观察异常。
                GameLog.Warning($"[AssetLease] 取消后的加载收尾异常 [{address}]：{ex.Message}");
            }
        }
    }
}
