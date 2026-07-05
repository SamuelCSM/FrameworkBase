using System;

namespace Framework.Network
{
    /// <summary>
    /// 网络消息订阅句柄，用于按组件生命周期精确释放单条协议监听。
    /// </summary>
    public sealed class MessageSubscription : IDisposable
    {
        /// <summary>
        /// 空订阅释放委托，订阅失败或空订阅时为空。
        /// </summary>
        private Action disposeAction;

        /// <summary>
        /// 获取订阅是否已经释放。
        /// </summary>
        public bool IsDisposed { get; private set; }

        /// <summary>
        /// 创建网络消息订阅句柄。
        /// </summary>
        /// <param name="disposeAction">释放订阅时执行的回调。</param>
        internal MessageSubscription(Action disposeAction)
        {
            this.disposeAction = disposeAction;
        }

        /// <summary>
        /// 创建一个已释放的空订阅句柄。
        /// </summary>
        /// <returns>已释放状态的订阅句柄。</returns>
        internal static MessageSubscription CreateDisposed()
        {
            return new MessageSubscription(null)
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
            Action action = disposeAction;
            disposeAction = null;
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
