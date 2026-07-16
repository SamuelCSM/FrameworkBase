using System.Threading;
using Cysharp.Threading.Tasks;
using PrimeTween;

namespace Framework
{
    /// <summary>
    /// PrimeTween ⇄ UniTask + <see cref="CancellationToken"/> 桥接。
    /// <para>
    /// 存在理由：框架的异步编排统一走 UniTask + CancellationToken（UI 关闭、场景切换、阶段拆卸都靠 ct 传播取消），
    /// 而 PrimeTween 原生 <c>await tween</c> 不接受 ct。本扩展把「令牌取消」翻译为「按目标停止补间」，语义与
    /// 框架既有动画一致：取消即<b>停在当前值</b>（<c>Stop</c>，非 <c>Complete</c>），且<b>不抛</b>
    /// <see cref="System.OperationCanceledException"/>——调用方在 await 之后自行收尾终态。
    /// </para>
    /// <para>
    /// 注意：C# async/await 本身会分配状态机（PrimeTween 补间自身零 GC，但 await 不是）。性能敏感的高频路径
    /// 请直接用 <c>Sequence</c> / <c>OnComplete</c> 回调，勿 await。
    /// </para>
    /// </summary>
    public static class TweenAsyncExtensions
    {
        /// <summary>
        /// 等待补间结束；<paramref name="cancellationToken"/> 取消时停止补间（停在当前值）并正常返回。
        /// </summary>
        public static async UniTask ToUniTask(this Tween tween, CancellationToken cancellationToken = default)
        {
            if (!tween.isAlive)
                return;

            if (!cancellationToken.CanBeCanceled)
            {
                await tween;
                return;
            }

            if (cancellationToken.IsCancellationRequested)
            {
                tween.Stop();
                return;
            }

            // ct 触发即停止该补间；await 在补间转为非存活后自然完成。
            CancellationTokenRegistration registration =
                cancellationToken.Register(() => { if (tween.isAlive) tween.Stop(); });
            try
            {
                await tween;
            }
            finally
            {
                registration.Dispose();
            }
        }

        /// <summary>
        /// 等待序列结束；<paramref name="cancellationToken"/> 取消时停止序列（停在当前值）并正常返回。
        /// </summary>
        public static async UniTask ToUniTask(this Sequence sequence, CancellationToken cancellationToken = default)
        {
            if (!sequence.isAlive)
                return;

            if (!cancellationToken.CanBeCanceled)
            {
                await sequence;
                return;
            }

            if (cancellationToken.IsCancellationRequested)
            {
                sequence.Stop();
                return;
            }

            CancellationTokenRegistration registration =
                cancellationToken.Register(() => { if (sequence.isAlive) sequence.Stop(); });
            try
            {
                await sequence;
            }
            finally
            {
                registration.Dispose();
            }
        }
    }
}
