using Framework.Core;

namespace Framework.Input
{
    /// <summary>
    /// 输入屏蔽作用域，配合 using 在代码块结束时自动解除屏蔽。
    /// </summary>
    public readonly struct InputBlockScope : System.IDisposable
    {
        /// <summary>当前作用域持有的屏蔽句柄。</summary>
        private readonly InputBlockHandle handle;

        /// <summary>
        /// 创建输入屏蔽作用域。
        /// </summary>
        /// <param name="blockHandle">屏蔽句柄，可为 null。</param>
        private InputBlockScope(InputBlockHandle blockHandle)
        {
            handle = blockHandle;
        }

        /// <summary>
        /// 压入一层输入屏蔽，并在 Dispose 时自动解除。
        /// </summary>
        /// <param name="reason">屏蔽原因，便于日志排查。</param>
        /// <returns>可配合 using 使用的作用域。</returns>
        public static InputBlockScope Begin(string reason)
        {
            InputManager input = GameEntry.Input;
            InputBlockHandle blockHandle = input != null ? input.Blocks.Push(reason) : null;
            return new InputBlockScope(blockHandle);
        }

        /// <summary>
        /// 解除当前作用域对应的输入屏蔽。
        /// </summary>
        public void Dispose()
        {
            handle?.Dispose();
        }
    }
}
