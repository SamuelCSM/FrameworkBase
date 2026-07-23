using System;
using UnityEngine;

namespace Framework
{
    /// <summary>统一窗口生命周期阶段；Ready/Closed 均发生在对应过渡动画完成之后。</summary>
    public enum UIWindowPhase
    {
        Opening = 0,
        Ready = 1,
        Closing = 2,
        Closed = 3,
    }

    /// <summary>窗口生命周期信号。WindowId=0 表示尚未迁移到稳定 ID 的旧式注册。</summary>
    public readonly struct UIWindowLifecycleEvent
    {
        public UIWindowLifecycleEvent(
            int windowId,
            UIWindowPhase phase,
            UIBaseCore instance,
            GameObject root)
        {
            WindowId = windowId;
            Phase = phase;
            Instance = instance;
            Root = root;
        }

        public int WindowId { get; }
        public UIWindowPhase Phase { get; }
        public UIBaseCore Instance { get; }
        public GameObject Root { get; }
        public Type WindowType => Instance?.GetType();
    }
}
