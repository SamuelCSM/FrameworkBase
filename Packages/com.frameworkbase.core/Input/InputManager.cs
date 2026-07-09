using Framework.Core;
using UnityEngine;

namespace Framework.Input
{
    /// <summary>
    /// 全局输入管理器，负责采样指针/手势并提供统一门禁。
    /// </summary>
    /// <remarks>
    /// Login 等 UI 模块后续可通过 <see cref="Gate"/> 与 <see cref="Blocks"/> 接入同一套输入基础设施。
    /// </remarks>
    public sealed class InputManager : FrameworkComponent<InputManager>
    {
        /// <summary>Legacy 指针采样源。</summary>
        private IPointerInputSource pointerSource;

        /// <summary>Legacy 双指/滚轮手势采样源。</summary>
        private IPinchPanGestureSource pinchPanSource;

        /// <summary>全局输入屏蔽栈。</summary>
        private InputBlockStack blockStack;

        /// <summary>场景输入门禁。</summary>
        private InputGate gate;

        /// <summary>全局输入屏蔽栈。</summary>
        public InputBlockStack Blocks => blockStack;

        /// <summary>场景输入门禁。</summary>
        public InputGate Gate => gate;

        /// <summary>当前主指针快照。</summary>
        public PointerSnapshot PrimaryPointer => pointerSource != null ? pointerSource.PrimaryPointer : PointerSnapshot.None;

        /// <summary>本帧双指/滚轮手势采样。</summary>
        public PinchPanFrame PinchPan => pinchPanSource != null ? pinchPanSource.CurrentFrame : PinchPanFrame.None;

        /// <summary>当前是否处于多指或双指手势态，场景单指交互应让路。</summary>
        public bool IsMultiPointerGestureActive =>
            (pointerSource != null && pointerSource.IsMultiPointerActive)
            || (pinchPanSource != null && pinchPanSource.CurrentFrame.IsActive && pointerSource != null && pointerSource.ActivePointerCount >= 2);

        /// <inheritdoc/>
        public override void OnInit()
        {
            blockStack = new InputBlockStack();
            gate = new InputGate(blockStack);
            pointerSource = new LegacyPointerInputSource();
            pinchPanSource = new LegacyPinchPanGestureSource();
            GameLog.Log("[InputManager] 初始化完成（Legacy 指针与手势采样）");
        }

        /// <summary>
        /// 注入 UI Bootstrap，供门禁检测 UGUI 命中。
        /// </summary>
        /// <param name="bootstrap">启动场景中的 UIBootstrap。</param>
        public void SetBootstrap(UIBootstrap bootstrap)
        {
            gate?.SetEventSystem(bootstrap != null ? bootstrap.UIEventSystem : null);
        }

        /// <inheritdoc/>
        public override void OnUpdate(float deltaTime)
        {
            pointerSource?.Collect();
            pinchPanSource?.Collect();
        }

        /// <inheritdoc/>
        public override void OnShutdown()
        {
            pointerSource = null;
            pinchPanSource = null;
            gate = null;
            blockStack = null;
        }
    }
}
