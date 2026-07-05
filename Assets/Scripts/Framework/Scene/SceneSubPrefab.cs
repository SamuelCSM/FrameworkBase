using System;
using UnityEngine;

namespace Framework
{
    /// <summary>
    /// 运行时加载型场景子预制控制类非泛型基类。
    /// </summary>
    public abstract class SceneSubPrefabCore : IDisposable
    {
        /// <summary>当前绑定的子预制 View，释放后会被清空。</summary>
        protected SceneSubView ViewCore { get; private set; }

        /// <summary>子预制根对象。</summary>
        protected GameObject GameObject => ViewCore != null ? ViewCore.gameObject : null;

        /// <summary>子预制根节点。</summary>
        protected Transform Transform => ViewCore != null ? ViewCore.transform : null;

        /// <summary>本次显示传入的业务数据。</summary>
        public object UserData { get; private set; }

        /// <summary>子预制绑定的 View 类型。</summary>
        public abstract Type ViewType { get; }

        /// <summary>是否已经完成初始化。</summary>
        public bool IsInitialized { get; private set; }

        /// <summary>当前是否处于显示状态。</summary>
        public bool IsShowing { get; private set; }

        /// <summary>是否已经释放。</summary>
        public bool IsDisposed { get; private set; }

        /// <summary>
        /// 显示或隐藏时是否自动同步子预制根节点的 Active 状态。
        /// </summary>
        protected virtual bool AutoSetActive => true;

        /// <summary>
        /// 初始化运行时加载型场景子预制。
        /// </summary>
        /// <param name="view">加载出的子预制 View。</param>
        internal void Initialize(SceneSubView view)
        {
            if (IsDisposed)
            {
                Logger.Warning($"[SceneSubPrefab] 已释放的子预制不能重新初始化: {GetType().Name}");
                return;
            }

            if (IsInitialized)
            {
                return;
            }

            if (view == null)
            {
                Logger.Error($"[SceneSubPrefab] Initialize 失败，View 为空: {GetType().Name}");
                return;
            }

            if (!ViewType.IsInstanceOfType(view))
            {
                Logger.Error($"[SceneSubPrefab] Initialize 失败，View 类型不匹配: Prefab={GetType().Name}, Need={ViewType.Name}, Actual={view.GetType().Name}");
                return;
            }

            ViewCore = view;
            IsInitialized = true;
            OnInit();
        }

        /// <summary>
        /// 显示子预制并刷新展示数据。
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
        /// 隐藏子预制并清理展示期状态。
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
        /// 释放子预制控制类，解除事件订阅并清空 View 引用。
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
            ViewCore = null;
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
                Logger.Warning($"[SceneSubPrefab] 已释放的子预制不能执行 {operation}: {GetType().Name}");
                return false;
            }

            if (!IsInitialized)
            {
                Logger.Error($"[SceneSubPrefab] 未初始化的子预制不能执行 {operation}: {GetType().Name}");
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

    /// <summary>
    /// 运行时加载型场景子预制控制类，和 <see cref="SceneSubView"/> 一一对应。
    /// </summary>
    /// <typeparam name="TView">绑定的场景子 View 类型。</typeparam>
    public abstract class SceneSubPrefab<TView> : SceneSubPrefabCore where TView : SceneSubView
    {
        /// <summary>当前绑定的强类型子预制 View。</summary>
        protected TView View => ViewCore as TView;

        /// <summary>子预制绑定的 View 类型。</summary>
        public override Type ViewType => typeof(TView);
    }
}
