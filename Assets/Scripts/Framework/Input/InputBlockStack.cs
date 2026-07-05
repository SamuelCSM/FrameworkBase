using System;
using System.Collections.Generic;

namespace Framework.Input
{
    /// <summary>
    /// 输入屏蔽句柄，Dispose 或显式 Pop 后解除对应屏蔽层。
    /// </summary>
    public sealed class InputBlockHandle : IDisposable
    {
        /// <summary>所属屏蔽栈。</summary>
        internal InputBlockStack Owner;

        /// <summary>屏蔽原因，便于排查。</summary>
        internal string Reason = string.Empty;

        /// <summary>是否仍有效。</summary>
        public bool IsActive { get; internal set; }

        /// <summary>
        /// 解除当前屏蔽层。
        /// </summary>
        public void Dispose()
        {
            Owner?.Pop(this);
        }
    }

    /// <summary>
    /// 输入屏蔽栈，用于 Loading、弹窗、提交中等全局输入冻结场景。
    /// </summary>
    public sealed class InputBlockStack
    {
        /// <summary>当前活跃屏蔽层。</summary>
        private readonly List<InputBlockHandle> activeBlocks = new List<InputBlockHandle>(4);

        /// <summary>是否存在任意屏蔽层。</summary>
        public bool IsBlocked => activeBlocks.Count > 0;

        /// <summary>
        /// 压入一层输入屏蔽。
        /// </summary>
        /// <param name="reason">屏蔽原因，便于日志排查。</param>
        /// <returns>可用于 Dispose 的屏蔽句柄。</returns>
        public InputBlockHandle Push(string reason)
        {
            var handle = new InputBlockHandle
            {
                Owner = this,
                Reason = reason ?? string.Empty,
            };
            handle.IsActive = true;
            activeBlocks.Add(handle);
            return handle;
        }

        /// <summary>
        /// 解除指定屏蔽层。
        /// </summary>
        /// <param name="handle">要解除的句柄。</param>
        public void Pop(InputBlockHandle handle)
        {
            if (handle == null || !handle.IsActive)
            {
                return;
            }

            handle.IsActive = false;
            handle.Owner = null;
            activeBlocks.Remove(handle);
        }
    }
}
