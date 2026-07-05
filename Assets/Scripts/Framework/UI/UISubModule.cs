using System;
using UnityEngine;

namespace Framework
{
    /// <summary>
    /// UI 子模块逻辑基类。
    /// <para>
    /// 适用于嵌套在窗口 Prefab 内的 HUD、页签内容、按钮区、列表区等轻量模块。
    /// 子模块不参与全局 UI 注册、层级、回退栈和窗口池，只由父窗口或父 Presenter 显式驱动。
    /// </para>
    /// </summary>
    /// <typeparam name="TView">子模块绑定的视图类型。</typeparam>
    public abstract class UISubModule<TView> : IUISubModule where TView : UISubView
    {
        /// <summary>当前绑定的子视图引用，释放后会被清空。</summary>
        protected TView View { get; private set; }

        /// <summary>子视图所在的 GameObject，视图为空时返回 null。</summary>
        protected GameObject GameObject => View != null ? View.gameObject : null;

        /// <summary>子视图所在的 Transform，视图为空时返回 null。</summary>
        protected Transform Transform => View != null ? View.transform : null;

        /// <summary>本次 Show 传入的业务数据，可通过 <see cref="GetUserData{T}"/> 获取强类型结果。</summary>
        public object UserData { get; private set; }

        /// <summary>是否已经完成初始化。</summary>
        public bool IsInitialized { get; private set; }

        /// <summary>当前是否处于显示状态。</summary>
        public bool IsShowing { get; private set; }

        /// <summary>是否已经释放。</summary>
        public bool IsDisposed { get; private set; }

        /// <summary>
        /// 显示或隐藏时是否自动同步子视图根节点的 Active 状态。
        /// <para>
        /// 大多数 UI 子模块应保持默认值；如果子模块只是逻辑聚合，不希望驱动 GameObject 显隐，可在子类中返回 false。
        /// </para>
        /// </summary>
        protected virtual bool AutoSetActive => true;

        /// <summary>
        /// 创建 UI 子模块并绑定视图。
        /// </summary>
        /// <param name="view">Inspector 中显式配置的子视图。</param>
        /// <exception cref="ArgumentNullException">视图为空时抛出。</exception>
        protected UISubModule(TView view)
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
        /// 显示子模块并刷新展示数据。
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
        /// 隐藏子模块并清理展示期状态。
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
        /// 释放子模块，解除事件订阅并清空视图引用。
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
        /// 确认子模块可执行指定生命周期操作。
        /// </summary>
        /// <param name="operation">当前操作名称，用于日志。</param>
        /// <returns>可执行时返回 true。</returns>
        private bool EnsureReady(string operation)
        {
            if (IsDisposed)
            {
                GameLog.Warning($"[UISubModule] 已释放的子模块不能执行 {operation}: {GetType().Name}");
                return false;
            }

            if (!IsInitialized)
            {
                GameLog.Error($"[UISubModule] 未初始化的子模块不能执行 {operation}: {GetType().Name}");
                return false;
            }

            return true;
        }

        /// <summary>初始化时调用一次，适合绑定按钮、缓存固定引用和创建内部长期对象。</summary>
        protected virtual void OnInit() { }

        /// <summary>
        /// 每次显示时调用，适合接收数据、刷新 UI、订阅展示期事件。
        /// </summary>
        /// <param name="userData">本次显示传入的业务数据，可为空。</param>
        protected virtual void OnShow(object userData) { }

        /// <summary>每次隐藏时调用，适合取消展示期事件、停止动画和清理临时状态。</summary>
        protected virtual void OnHide() { }

        /// <summary>最终释放时调用，适合解除长期订阅、释放动态对象和断开外部引用。</summary>
        protected virtual void OnDispose() { }
    }

    /// <summary>
    /// UI 加载型子面板非泛型基类，供 <see cref="UISubPanelHost{TPanel}"/> 统一创建和驱动。
    /// <para>
    /// 业务代码请继承 <see cref="UISubPanel{TView}"/>；Host 会先 new 子面板逻辑，再把加载出的 View 交给 Initialize。
    /// </para>
    /// </summary>
    public abstract class UISubPanelCore : IUISubModule
    {
        /// <summary>当前绑定的子面板视图引用，释放后会被清空。</summary>
        protected UISubView ViewCore { get; private set; }

        /// <summary>子面板根对象，视图为空时返回 null。</summary>
        protected GameObject GameObject => ViewCore != null ? ViewCore.gameObject : null;

        /// <summary>子面板根节点，视图为空时返回 null。</summary>
        protected Transform Transform => ViewCore != null ? ViewCore.transform : null;

        /// <summary>本次 Show 传入的业务数据，可通过 <see cref="GetUserData{T}"/> 获取强类型结果。</summary>
        public object UserData { get; private set; }

        /// <summary>子面板绑定的 View 类型，由 <see cref="UISubPanel{TView}"/> 自动提供。</summary>
        public abstract Type ViewType { get; }

        /// <summary>是否已经完成初始化。</summary>
        public bool IsInitialized { get; private set; }

        /// <summary>当前是否处于显示状态。</summary>
        public bool IsShowing { get; private set; }

        /// <summary>是否已经释放。</summary>
        public bool IsDisposed { get; private set; }

        /// <summary>
        /// 显示或隐藏时是否自动同步子面板根节点的 Active 状态。
        /// </summary>
        protected virtual bool AutoSetActive => true;

        /// <summary>
        /// 初始化子面板并绑定视图，由 <see cref="UISubPanelHost{TPanel}"/> 内部调用。
        /// </summary>
        /// <param name="view">动态加载出的子面板视图。</param>
        internal void Initialize(UISubView view)
        {
            if (IsDisposed)
            {
                GameLog.Warning($"[UISubPanel] 已释放的子面板不能重新初始化: {GetType().Name}");
                return;
            }

            if (IsInitialized)
            {
                return;
            }

            if (view == null)
            {
                GameLog.Error($"[UISubPanel] Initialize 失败，视图为空: {GetType().Name}");
                return;
            }

            if (!ViewType.IsInstanceOfType(view))
            {
                GameLog.Error($"[UISubPanel] Initialize 失败，视图类型不匹配: Panel={GetType().Name}, Need={ViewType.Name}, Actual={view.GetType().Name}");
                return;
            }

            ViewCore = view;
            IsInitialized = true;
            OnInit();
        }

        /// <summary>
        /// 显示子面板并刷新展示数据。
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
        /// 隐藏子面板并清理展示期状态。
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
        /// 释放子面板，解除事件订阅并清空视图引用。
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
        /// 确认子面板可执行指定生命周期操作。
        /// </summary>
        /// <param name="operation">当前操作名称，用于日志。</param>
        /// <returns>可执行时返回 true。</returns>
        private bool EnsureReady(string operation)
        {
            if (IsDisposed)
            {
                GameLog.Warning($"[UISubPanel] 已释放的子面板不能执行 {operation}: {GetType().Name}");
                return false;
            }

            if (!IsInitialized)
            {
                GameLog.Error($"[UISubPanel] 未初始化的子面板不能执行 {operation}: {GetType().Name}");
                return false;
            }

            return true;
        }

        /// <summary>初始化时调用一次，适合绑定按钮、缓存固定引用和创建内部长期对象。</summary>
        protected virtual void OnInit() { }

        /// <summary>
        /// 每次显示时调用，适合接收数据、刷新 UI、订阅展示期事件。
        /// </summary>
        /// <param name="userData">本次显示传入的业务数据，可为空。</param>
        protected virtual void OnShow(object userData) { }

        /// <summary>每次隐藏时调用，适合取消展示期事件、停止动画和清理临时状态。</summary>
        protected virtual void OnHide() { }

        /// <summary>最终释放时调用，适合解除长期订阅、释放动态对象和断开外部引用。</summary>
        protected virtual void OnDispose() { }
    }

    /// <summary>
    /// UI 加载型子面板逻辑基类。
    /// </summary>
    /// <typeparam name="TView">子面板绑定的视图类型。</typeparam>
    public abstract class UISubPanel<TView> : UISubPanelCore where TView : UISubView
    {
        /// <summary>子面板绑定的强类型视图。</summary>
        protected TView View => ViewCore as TView;

        /// <summary>子面板绑定的 View 类型。</summary>
        public override Type ViewType => typeof(TView);
    }
}
