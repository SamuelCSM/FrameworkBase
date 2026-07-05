using System;
using System.Collections.Generic;
using Cysharp.Threading.Tasks;
using Framework.Core;
using UnityEngine;

namespace Framework
{
    /// <summary>
    /// UIBase 的非泛型基类，供 UIManager 统一操作所有窗口类型，避免反射调用。
    /// 业务代码请继承 <see cref="UIBase{TView}"/>，不要直接继承此类。
    /// </summary>
    public abstract class UIBaseCore
    {
        /// <summary>当前窗口是否处于打开状态（Open 之后、Close 完成之前为 true）</summary>
        public abstract bool    IsOpened      { get; }

        /// <summary>窗口所在的 UI 层级，在 Initialize 时由 UIManager 指定</summary>
        public abstract UILayer Layer         { get; }

        /// <summary>是否已完成初始化（Initialize 调用后为 true，整个生命周期内不会重置）</summary>
        public abstract bool    IsInitialized { get; }

        /// <summary>窗口绑定的 View 类型，由 <see cref="UIBase{TView}"/> 自动提供。</summary>
        public abstract Type    ViewType      { get; }

        /// <summary>
        /// 初始化窗口（UIManager 内部调用，每个实例仅调用一次）。
        /// 完成后触发 OnInit 钩子。
        /// </summary>
        internal abstract void    Initialize(UILayer layer, UIView view, GameObject go);

        /// <summary>
        /// 异步打开窗口并播放进入动画（UIManager.OpenUIAsync 内部调用）。
        /// 流程：SetActive(true) → OnOpen → 播放进入动画。
        /// </summary>
        internal abstract UniTask OpenWithAnimAsync(object userData = null);

        /// <summary>
        /// 异步关闭窗口并播放退出动画（UIManager.CloseUIAsync 内部调用）。
        /// 流程：OnClose → 播放退出动画 → SetActive(false) → 重置 CanvasGroup。
        /// </summary>
        internal abstract UniTask CloseWithAnimAsync();

        /// <summary>
        /// 强制关闭，不播放动画（UIManager.CloseUI 同步版本 / OnShutdown 使用）。
        /// </summary>
        internal abstract void    ForceClose();

        /// <summary>
        /// 销毁窗口，释放引用（UIManager 回收到对象池前调用）。
        /// 触发 OnDestroy 钩子，清空 View / GameObject 引用。
        /// </summary>
        internal abstract void    DestroyUI();
    }

    /// <summary>
    /// UI 窗口逻辑基类。
    /// 所有业务 UI 逻辑类继承此类，不继承 MonoBehaviour。
    ///
    /// 动画配置（重写 AnimConfig）：
    ///   protected override UIAnimationConfig AnimConfig => UIAnimationConfig.ScalePop();
    ///   protected override UIAnimationConfig AnimConfig => UIAnimationConfig.SlideUp();
    ///   protected override UIAnimationConfig AnimConfig => UIAnimationConfig.None;
    ///
    /// 打开/关闭由 UIManager 调用，不要在子类中直接调用 Open/Close。
    /// 如需在逻辑内关闭自身，调用 UIManager：
    ///   GameEntry.UI.CloseUIAsync(this).Forget();
    /// </summary>
    public abstract class UIBase<TView> : UIBaseCore where TView : UIView
    {
        // ── 属性 ─────────────────────────────────────────────────────────────

        /// <summary>UI 视图组件（持有所有序列化 UI 引用的 MonoBehaviour）</summary>
        protected TView      View      { get; private set; }

        /// <summary>UI 根 GameObject（由 UIManager 实例化并管理生命周期）</summary>
        protected GameObject GameObject { get; private set; }

        /// <summary>UI 根 Transform，等同于 GameObject.transform</summary>
        protected Transform  Transform  => GameObject?.transform;

        private UILayer _layer;
        private object  _userData;
        private bool    _isInitialized;
        private bool    _isOpened;

        /// <summary>当前窗口持有的事件订阅句柄列表，OnClose 时自动释放。</summary>
        private List<EventSubscription> _eventSubscriptions;

        public override UILayer Layer         => _layer;
        public override bool    IsInitialized => _isInitialized;
        public override bool    IsOpened      => _isOpened;
        public override Type    ViewType      => typeof(TView);

        /// <summary>本次打开时传入的业务数据，使用 GetUserData&lt;T&gt;() 获取强类型版本</summary>
        public object UserData => _userData;

        // ── 动画配置（子类重写）──────────────────────────────────────────────
        /// <summary>
        /// 当前窗口的过渡动画配置。默认淡入淡出。
        /// 子类重写此属性来指定动画效果：
        ///   弹窗 → UIAnimationConfig.ScalePop()
        ///   面板 → UIAnimationConfig.SlideUp()
        ///   无   → UIAnimationConfig.None
        /// </summary>
        protected virtual UIAnimationConfig AnimConfig => UIAnimationConfig.Fade();

        // ── UIManager 内部调用 ───────────────────────────────────────────────

        internal override void Initialize(UILayer layer, UIView view, GameObject go)
        {
            if (_isInitialized) return;

            _layer          = layer;
            View            = view as TView;
            GameObject      = go;
            _isInitialized  = true;

            OnInit();
        }

        internal override async UniTask OpenWithAnimAsync(object userData = null)
        {
            if (!_isInitialized)
            {
                GameLog.Error($"[UIBase] OpenWithAnimAsync: 未初始化 — {GetType().Name}");
                return;
            }

            _userData = userData;
            _isOpened = true;

            GameObject.SetActive(true);

            // OnOpen 先设置好数据和初始状态，再播放动画
            OnOpen(userData);

            await UIAnimator.PlayOpenAsync(GameObject, AnimConfig);

            OnOpenReady();
        }

        internal override async UniTask CloseWithAnimAsync()
        {
            if (!_isOpened) return;
            _isOpened = false;

            OnClose();
            DisposeAllSubscriptions();

            await UIAnimator.PlayCloseAsync(GameObject, AnimConfig);

            GameObject.SetActive(false);

            // 动画结束后重置 CanvasGroup，确保下次 Open 时状态干净
            var cg = GameObject.GetComponent<CanvasGroup>();
            if (cg != null)
            {
                cg.alpha          = 1f;
                cg.interactable   = true;
                cg.blocksRaycasts = true;
            }

            OnCloseComplete();
        }

        internal override void ForceClose()
        {
            if (!_isOpened) return;
            _isOpened = false;
            OnClose();
            DisposeAllSubscriptions();
            GameObject.SetActive(false);
        }

        internal override void DestroyUI()
        {
            if (_isOpened) ForceClose();
            OnDestroy();
            View       = null;
            GameObject = null;
        }

        // ── 事件订阅辅助（窗口关闭时自动注销）────────────────────────────────

        /// <summary>
        /// 订阅无参数消息，窗口关闭时自动注销。
        /// </summary>
        /// <param name="message">消息枚举。</param>
        /// <param name="callback">消息回调。</param>
        /// <param name="priority">优先级，数值越大越先触发。</param>
        /// <returns>订阅句柄，通常无需手动持有。</returns>
        protected EventSubscription ListenEvent(GameMessage message, Action callback, int priority = 0)
        {
            EventSubscription sub = GameEntry.Event.Subscribe(message, callback, priority);
            AddSubscription(sub);
            return sub;
        }

        /// <summary>
        /// 订阅一个参数的消息，窗口关闭时自动注销。
        /// </summary>
        /// <typeparam name="T">参数类型。</typeparam>
        /// <param name="message">消息枚举。</param>
        /// <param name="callback">消息回调。</param>
        /// <param name="priority">优先级，数值越大越先触发。</param>
        /// <returns>订阅句柄，通常无需手动持有。</returns>
        protected EventSubscription ListenEvent<T>(GameMessage message, Action<T> callback, int priority = 0)
        {
            EventSubscription sub = GameEntry.Event.Subscribe(message, callback, priority);
            AddSubscription(sub);
            return sub;
        }

        /// <summary>
        /// 订阅两个参数的消息，窗口关闭时自动注销。
        /// </summary>
        /// <typeparam name="T1">第一个参数类型。</typeparam>
        /// <typeparam name="T2">第二个参数类型。</typeparam>
        /// <param name="message">消息枚举。</param>
        /// <param name="callback">消息回调。</param>
        /// <param name="priority">优先级，数值越大越先触发。</param>
        /// <returns>订阅句柄，通常无需手动持有。</returns>
        protected EventSubscription ListenEvent<T1, T2>(GameMessage message, Action<T1, T2> callback, int priority = 0)
        {
            EventSubscription sub = GameEntry.Event.Subscribe(message, callback, priority);
            AddSubscription(sub);
            return sub;
        }

        /// <summary>
        /// 将订阅句柄加入自动管理列表。
        /// </summary>
        /// <param name="sub">事件订阅句柄。</param>
        private void AddSubscription(EventSubscription sub)
        {
            if (sub == null || sub.IsDisposed)
            {
                return;
            }

            if (_eventSubscriptions == null)
            {
                _eventSubscriptions = new List<EventSubscription>();
            }

            _eventSubscriptions.Add(sub);
        }

        /// <summary>
        /// 释放所有通过 ListenEvent 注册的事件订阅。
        /// 由 CloseWithAnimAsync / ForceClose 在 OnClose 之后自动调用。
        /// </summary>
        private void DisposeAllSubscriptions()
        {
            if (_eventSubscriptions == null || _eventSubscriptions.Count == 0)
            {
                return;
            }

            for (int i = _eventSubscriptions.Count - 1; i >= 0; i--)
            {
                _eventSubscriptions[i]?.Unsubscribe();
            }

            _eventSubscriptions.Clear();
        }

        // ── 生命周期钩子（子类重写）──────────────────────────────────────────

        /// <summary>初始化（仅调用一次）。查找组件、注册事件等。</summary>
        protected virtual void OnInit() { }

        /// <summary>
        /// 打开时调用（动画播放前）。
        /// 设置 UI 数据、刷新显示内容等。
        /// </summary>
        protected virtual void OnOpen(object userData) { }

        /// <summary>
        /// 打开动画播放完毕后调用。
        /// 适合需要等待过渡结束才执行的逻辑，例如播放引导高亮、启动轮询等。
        /// </summary>
        protected virtual void OnOpenReady() { }

        /// <summary>关闭时调用（动画播放前）。清理临时数据、取消监听等。</summary>
        protected virtual void OnClose() { }

        /// <summary>
        /// 关闭动画播放完毕后调用。
        /// 适合需要等待过渡结束才执行的清理逻辑，例如释放动画期间仍需保持的引用。
        /// </summary>
        protected virtual void OnCloseComplete() { }

        /// <summary>销毁时调用。注销事件、释放资源等。</summary>
        protected virtual void OnDestroy() { }

        // ── 辅助方法 ─────────────────────────────────────────────────────────

        /// <summary>
        /// 以强类型方式获取本次 Open 传入的 userData。
        /// 类型不匹配时返回 null，不抛异常。
        /// </summary>
        protected T GetUserData<T>() where T : class => _userData as T;
    }
}
