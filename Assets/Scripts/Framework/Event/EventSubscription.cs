using System;

namespace Framework
{
    /// <summary>
    /// 事件订阅句柄，用于取消单条事件监听。
    /// </summary>
    public sealed class EventSubscription : IDisposable
    {
        /// <summary>
        /// 取消订阅时执行的回调。
        /// </summary>
        private Action unsubscribeAction;

        /// <summary>
        /// 获取订阅是否已经取消。
        /// </summary>
        public bool IsDisposed { get; private set; }

        /// <summary>
        /// 创建事件订阅句柄。
        /// </summary>
        /// <param name="unsubscribeAction">取消订阅时执行的回调。</param>
        internal EventSubscription(Action unsubscribeAction)
        {
            this.unsubscribeAction = unsubscribeAction;
        }

        /// <summary>
        /// 创建一个已取消的空订阅句柄。
        /// </summary>
        /// <returns>已取消状态的订阅句柄。</returns>
        internal static EventSubscription CreateDisposed()
        {
            return new EventSubscription(null)
            {
                IsDisposed = true
            };
        }

        /// <summary>
        /// 取消订阅。该操作具备幂等性，重复调用不会重复取消。
        /// </summary>
        public void Unsubscribe()
        {
            if (IsDisposed)
            {
                return;
            }

            IsDisposed = true;
            Action action = unsubscribeAction;
            unsubscribeAction = null;
            action?.Invoke();
        }

        /// <summary>
        /// 释放订阅句柄，等价于 <see cref="Unsubscribe"/>。
        /// </summary>
        public void Dispose()
        {
            Unsubscribe();
        }
    }
}
