using System;
using UnityEngine;

namespace Framework
{
    /// <summary>
    /// 场景对象控制类公共基类，负责维护 View 引用和基础生命周期。
    /// </summary>
    /// <typeparam name="TView">当前控制类绑定的场景 View 类型。</typeparam>
    public abstract class SceneControllerBase<TView> : IDisposable where TView : MonoBehaviour
    {
        /// <summary>当前绑定的场景 View，释放后会被清空。</summary>
        protected TView View { get; private set; }

        /// <summary>当前 View 所在的 GameObject。</summary>
        protected GameObject GameObject => View != null ? View.gameObject : null;

        /// <summary>当前 View 所在的 Transform。</summary>
        protected Transform Transform => View != null ? View.transform : null;

        /// <summary>本次显示传入的业务数据。</summary>
        public object UserData { get; private set; }

        /// <summary>是否已经完成初始化。</summary>
        public bool IsInitialized { get; private set; }

        /// <summary>当前是否处于显示状态。</summary>
        public bool IsShowing { get; private set; }

        /// <summary>是否已经释放。</summary>
        public bool IsDisposed { get; private set; }

        /// <summary>
        /// 显示或隐藏时是否自动同步 View 根节点的 Active 状态。
        /// </summary>
        protected virtual bool AutoSetActive => true;

        /// <summary>
        /// 创建场景对象控制类并绑定 View。
        /// </summary>
        /// <param name="view">Inspector 中显式配置的场景 View。</param>
        /// <exception cref="ArgumentNullException">View 为空时抛出。</exception>
        protected SceneControllerBase(TView view)
        {
            if (view == null)
            {
                throw new ArgumentNullException(nameof(view));
            }

            View = view;
            IsInitialized = true;
            OnInit();
        }

        /// <summary>
        /// 显示场景对象并刷新展示数据。
        /// </summary>
        /// <param name="userData">本次显示传入的业务数据，可为空。</param>
        public void Show(object userData = null)
        {
            if (!EnsureReady("Show"))
            {
                return;
            }

            UserData = userData;
            if (!IsShowing)
            {
                IsShowing = true;
                if (AutoSetActive && GameObject != null)
                {
                    GameObject.SetActive(true);
                }
            }

            OnShow(userData);
        }

        /// <summary>
        /// 隐藏场景对象并清理展示期状态。
        /// </summary>
        public void Hide()
        {
            if (!IsInitialized || IsDisposed || !IsShowing)
            {
                return;
            }

            OnHide();
            IsShowing = false;
            UserData = null;

            if (AutoSetActive && GameObject != null)
            {
                GameObject.SetActive(false);
            }
        }

        /// <summary>
        /// 释放场景对象控制类，解除事件订阅并清空 View 引用。
        /// </summary>
        public void Dispose()
        {
            if (IsDisposed)
            {
                return;
            }

            if (IsShowing)
            {
                Hide();
            }

            if (IsInitialized)
            {
                OnDispose();
            }

            UserData = null;
            View = null;
            IsInitialized = false;
            IsDisposed = true;
        }

        /// <summary>
        /// 以强类型方式获取本次 Show 传入的业务数据。
        /// </summary>
        /// <typeparam name="T">期望的数据类型。</typeparam>
        /// <returns>类型匹配时返回业务数据，否则返回 null。</returns>
        protected T GetUserData<T>() where T : class => UserData as T;

        /// <summary>
        /// 确认当前生命周期状态允许执行指定操作。
        /// </summary>
        /// <param name="operation">当前操作名称。</param>
        /// <returns>允许执行时返回 true。</returns>
        private bool EnsureReady(string operation)
        {
            if (IsDisposed)
            {
                GameLog.Warning($"[SceneController] 已释放的场景控制类不能执行 {operation}: {GetType().Name}");
                return false;
            }

            if (!IsInitialized)
            {
                GameLog.Error($"[SceneController] 未初始化的场景控制类不能执行 {operation}: {GetType().Name}");
                return false;
            }

            return true;
        }

        /// <summary>初始化时调用一次，适合绑定事件和缓存长期引用。</summary>
        protected virtual void OnInit() { }

        /// <summary>
        /// 每次显示时调用，适合接收数据和刷新展示。
        /// </summary>
        /// <param name="userData">本次显示传入的业务数据，可为空。</param>
        protected virtual void OnShow(object userData) { }

        /// <summary>每次隐藏时调用，适合停止动画、取消展示期事件和清理临时状态。</summary>
        protected virtual void OnHide() { }

        /// <summary>最终释放时调用，适合解除长期订阅、释放动态对象和断开外部引用。</summary>
        protected virtual void OnDispose() { }
    }
}
