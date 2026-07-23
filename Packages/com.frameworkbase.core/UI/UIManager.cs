using System;
using System.Collections.Generic;
using UnityEngine;
using Cysharp.Threading.Tasks;

namespace Framework
{
    /// <summary>
    /// UI 窗口的注册信息，由 UIManager.RegisterUI 填写，OpenUIAsync 时读取。
    /// </summary>
    public class UIRegisterInfo
    {
        /// <summary>稳定窗口 ID；0 表示旧式仅按 Type 注册。</summary>
        public int     Id            { get; set; }

        /// <summary>窗口逻辑类类型（UIBase 子类）</summary>
        public Type    UIType        { get; set; }

        /// <summary>窗口逻辑对象工厂；配置驱动注册默认使用已校验 Type 的无参构造。</summary>
        internal Func<UIBaseCore> LogicFactory { get; set; }

        /// <summary>Addressables 地址，用于加载 Prefab</summary>
        public string  Address       { get; set; }

        /// <summary>
        /// 纯代码 UI 的根节点工厂。与 <see cref="Address"/> 二选一；
        /// 由 UIManager 传入目标层节点，工厂返回已挂载对应 UIView 的窗口根节点。
        /// </summary>
        public Func<Transform, GameObject> Factory { get; set; }

        /// <summary>窗口所在层级（决定 Canvas SortingOrder）</summary>
        public UILayer Layer         { get; set; }

        /// <summary>是否允许同类型多实例并存（如多个 Toast、多个对话框）</summary>
        public bool    AllowMultiple { get; set; }

        /// <summary>窗口在导航栈中的行为（默认 PushToStack）</summary>
        public UIStackBehavior StackBehavior { get; set; } = UIStackBehavior.PushToStack;

        /// <summary>窗口弹出时的遮罩模式（默认 None）</summary>
        public UIBlockerMode BlockerMode { get; set; } = UIBlockerMode.None;
    }

    /// <summary>
    /// UI 管理器
    ///
    /// 核心接口：
    ///   OpenUIAsync   — 加载并打开（带进入动画）
    ///   CloseUIAsync  — 播放退出动画后关闭（推荐）
    ///   CloseUI       — 立即关闭，跳过动画（批量操作 / Shutdown 用）
    ///   GoBackAsync   — 关闭栈顶并显示前一个（带动画）
    ///   GoToUIAsync   — 清空栈并打开指定 UI
    ///
    /// 初始化顺序：
    ///   1. GameEntry.InitializeManagers() 调用 AddComponent&lt;UIManager&gt;()，触发 OnInit()
    ///   2. GameEntry 紧接着调用 UI.SetBootstrap(uiBootstrap)，注入场景中已准备好的 Canvas 层级
    ///   3. Start() 之后才会调用 OpenUIAsync，届时 _bootstrap 已就绪
    ///
    /// 动画由各 UIBase 子类的 AnimConfig 属性决定：
    ///   protected override UIAnimationConfig AnimConfig => UIAnimationConfig.ScalePop();
    /// </summary>
    public class UIManager : Core.FrameworkComponent<UIManager>
    {
        private sealed class LifecycleSubscription : IDisposable
        {
            private UIManager _owner;
            public readonly Action<UIWindowLifecycleEvent> Handler;

            public LifecycleSubscription(UIManager owner, Action<UIWindowLifecycleEvent> handler)
            {
                _owner = owner;
                Handler = handler;
            }

            public void Dispose()
            {
                UIManager owner = _owner;
                if (owner == null) return;
                _owner = null;
                owner.RemoveLifecycleSubscription(this);
            }
        }

        // ── 内部状态 ─────────────────────────────────────────────────────────

        /// <summary>
        /// UI 基础设施引导组件（场景中放置，跨场景存活）。
        /// 持有各层级 Canvas，由 GameEntry 在初始化时通过 SetBootstrap 注入。
        /// </summary>
        private UIBootstrap _bootstrap;

        /// <summary>
        /// 稳定 UI TargetId → 当前激活 Rect/Button 实例的运行时目录（<see cref="UITargetAnchor"/> 挂载即注册）。
        /// 引导等编排能力按 TargetId 寻址控件；归 UIManager 持有而非 GameEntry——它是纯 UI 概念，
        /// 生命周期也与 UI 一致（框架关闭时随 UI 一起 Clear）。
        /// </summary>
        public UITargetRegistry Targets { get; } = new UITargetRegistry();

        /// <summary>窗口类型 → 注册信息（Addressables 地址、层级、是否允许多开）</summary>
        private readonly Dictionary<Type, UIRegisterInfo>   _registerInfos = new Dictionary<Type, UIRegisterInfo>();

        /// <summary>稳定窗口 ID → 注册信息；仅收录 Id &gt; 0 的配置化窗口。</summary>
        private readonly Dictionary<int, UIRegisterInfo> _registerInfosById = new Dictionary<int, UIRegisterInfo>();

        /// <summary>窗口类型 → 稳定 ID；保持泛型 API 与 ID API 走同一份注册信息。</summary>
        private readonly Dictionary<Type, int> _windowIdsByType = new Dictionary<Type, int>();

        /// <summary>窗口类型 → 当前所有已打开实例列表（AllowMultiple 时可能多个）</summary>
        private readonly Dictionary<Type, List<UIBaseCore>> _openedUIs     = new Dictionary<Type, List<UIBaseCore>>();

        /// <summary>逻辑窗口实例 → 对应的 GameObject（用于回收 / 销毁时查找）</summary>
        private readonly Dictionary<UIBaseCore, GameObject> _uiGameObjects = new Dictionary<UIBaseCore, GameObject>();

        /// <summary>
        /// UI 导航栈（使用 LinkedList 替代 Stack，支持 O(1) 随机移除）。
        /// Last 节点为栈顶（最后打开的窗口），First 为栈底。
        /// GoBackAsync 移除 Last 并激活前一个，实现类似页面路由的返回效果。
        /// 只有 StackBehavior == PushToStack 的窗口才会入栈。
        /// </summary>
        private readonly LinkedList<UIBaseCore>             _uiStack       = new LinkedList<UIBaseCore>();

        /// <summary>窗口类型 → 对象池（Close 时归还 GameObject，再次 Open 时复用，省去重复实例化开销）</summary>
        private readonly Dictionary<Type, GameObjectPool>   _uiPools       = new Dictionary<Type, GameObjectPool>();

        /// <summary>窗口逻辑实例 → 对应的遮罩 GameObject（用于关闭时同步销毁遮罩）</summary>
        private readonly Dictionary<UIBaseCore, GameObject> _uiBlockers    = new Dictionary<UIBaseCore, GameObject>();

        /// <summary>窗口生命周期订阅；窗口开关低频，通知时快照隔离回调内退订和嵌套导航。</summary>
        private readonly List<LifecycleSubscription> _lifecycleSubscriptions = new List<LifecycleSubscription>();

        // ── 生命周期 ─────────────────────────────────────────────────────────

        public override void OnInit()
        {
            // Canvas 层级由 UIBootstrap 在场景中负责创建。
            // SetBootstrap() 在 GameEntry.InitializeManagers() 结束后立即调用，
            // 早于任何 OpenUIAsync，因此无需在此创建节点。
            GameLog.Log("[UIManager] 初始化完成（等待 SetBootstrap 注入 Canvas 层级）");
        }

        public override void OnShutdown()
        {
            // Shutdown 时跳过动画，直接强制关闭
            foreach (var list in _openedUIs.Values)
                foreach (var ui in list)
                    ui.ForceClose();

            foreach (var blocker in _uiBlockers.Values)
                UIBlocker.Destroy(blocker);

            foreach (var pool in _uiPools.Values) pool.Clear();

            _openedUIs.Clear();
            _uiGameObjects.Clear();
            _uiBlockers.Clear();
            _uiStack.Clear();
            _registerInfos.Clear();
            _registerInfosById.Clear();
            _windowIdsByType.Clear();
            _lifecycleSubscriptions.Clear();
            _uiPools.Clear();

            GameLog.Log("[UIManager] 已关闭");
        }

        // ── Bootstrap 注入 ───────────────────────────────────────────────────

        /// <summary>
        /// 注入 UIBootstrap，提供各层级 Canvas。
        /// 由 GameEntry.InitializeManagers() 在创建 UIManager 后立即调用，
        /// 必须在任何 OpenUIAsync 之前完成。
        /// </summary>
        public void SetBootstrap(UIBootstrap bootstrap)
        {
            if (bootstrap == null)
            {
                GameLog.Error("[UIManager] SetBootstrap: bootstrap 为空，请在 GameEntry Inspector 中赋值 UIBootstrap");
                return;
            }
            _bootstrap = bootstrap;
            GameLog.Log("[UIManager] Bootstrap 注入完成，UI 层级就绪");
        }

        /// <summary>获取指定层级 Canvas 的父节点 Transform（用于在编辑器脚本或特殊 UI 中手动挂载）</summary>
        public Transform GetLayerRoot(UILayer layer)
        {
            if (_bootstrap == null)
            {
                GameLog.Error("[UIManager] GetLayerRoot: bootstrap 未注入");
                return null;
            }
            return _bootstrap.GetLayerRoot(layer);
        }

        // ── 注册 ─────────────────────────────────────────────────────────────

        /// <summary>
        /// 注册 UI 类型与 Addressables 地址的映射。
        /// 建议在 GameEntry 初始化阶段统一注册。
        /// </summary>
        /// <param name="address">Addressables 资源地址。</param>
        /// <param name="layer">窗口所在 UI 层级。</param>
        /// <param name="allowMultiple">是否允许同类型多实例并存。</param>
        /// <param name="stackBehavior">导航栈行为，默认入栈。</param>
        /// <param name="blockerMode">窗口弹出时的遮罩模式，默认无遮罩。</param>
        public void RegisterUI<T>(
            string address,
            UILayer layer,
            bool allowMultiple = false,
            UIStackBehavior stackBehavior = UIStackBehavior.PushToStack,
            UIBlockerMode blockerMode = UIBlockerMode.None)
        {
            RegisterUI(typeof(T), address, layer, allowMultiple, stackBehavior, blockerMode);
        }

        /// <summary>以稳定 ID 注册 Addressables UI；供配置目录 Bootstrap 使用。</summary>
        public void RegisterUI<T>(
            int windowId,
            string address,
            UILayer layer,
            bool allowMultiple = false,
            UIStackBehavior stackBehavior = UIStackBehavior.PushToStack,
            UIBlockerMode blockerMode = UIBlockerMode.None)
            where T : UIBaseCore
        {
            RegisterUI(windowId, typeof(T), address, layer, allowMultiple, stackBehavior, blockerMode);
        }

        /// <summary>
        /// 注册纯代码构建的 UI。窗口仍完整走 UIManager 的生命周期、导航栈、遮罩和动画，
        /// 只是根节点由工厂创建而非从 Addressables 实例化。
        /// </summary>
        /// <typeparam name="T">窗口逻辑类型，必须继承 UIBaseCore。</typeparam>
        /// <param name="factory">接收目标层父节点并返回窗口根节点的工厂。</param>
        /// <param name="layer">窗口所在 UI 层级。</param>
        /// <param name="allowMultiple">是否允许同类型多实例并存。</param>
        /// <param name="stackBehavior">窗口在导航栈中的行为。</param>
        /// <param name="blockerMode">窗口弹出时的遮罩模式。</param>
        public void RegisterCodeUI<T>(
            Func<Transform, GameObject> factory,
            UILayer layer,
            bool allowMultiple = false,
            UIStackBehavior stackBehavior = UIStackBehavior.PushToStack,
            UIBlockerMode blockerMode = UIBlockerMode.None)
            where T : UIBaseCore
        {
            RegisterCodeUIInternal(0, typeof(T), factory, layer, allowMultiple, stackBehavior, blockerMode);
        }

        /// <summary>以稳定 ID 注册纯代码 UI；窗口定义仍不加载任何 Prefab。</summary>
        public void RegisterCodeUI<T>(
            int windowId,
            Func<Transform, GameObject> factory,
            UILayer layer,
            bool allowMultiple = false,
            UIStackBehavior stackBehavior = UIStackBehavior.PushToStack,
            UIBlockerMode blockerMode = UIBlockerMode.None)
            where T : UIBaseCore
        {
            RegisterCodeUIInternal(windowId, typeof(T), factory, layer, allowMultiple, stackBehavior, blockerMode);
        }

        /// <summary>
        /// 以运行时类型注册 UI 窗口，用于配置表或代码生成清单驱动的窗口注册。
        /// </summary>
        /// <param name="uiType">窗口逻辑类型，必须继承 UIBaseCore。</param>
        /// <param name="address">Addressables 资源地址。</param>
        /// <param name="layer">窗口所在 UI 层级。</param>
        /// <param name="allowMultiple">是否允许同类型多实例并存。</param>
        /// <param name="stackBehavior">导航栈行为，默认入栈。</param>
        /// <param name="blockerMode">窗口弹出时的遮罩模式，默认无遮罩。</param>
        public void RegisterUI(
            Type uiType,
            string address,
            UILayer layer,
            bool allowMultiple = false,
            UIStackBehavior stackBehavior = UIStackBehavior.PushToStack,
            UIBlockerMode blockerMode = UIBlockerMode.None)
        {
            RegisterUIInternal(0, uiType, address, layer, allowMultiple, stackBehavior, blockerMode);
        }

        /// <summary>以稳定 ID + 运行时 Type 注册窗口；Type 只在 Bootstrap 解析一次。</summary>
        public void RegisterUI(
            int windowId,
            Type uiType,
            string address,
            UILayer layer,
            bool allowMultiple = false,
            UIStackBehavior stackBehavior = UIStackBehavior.PushToStack,
            UIBlockerMode blockerMode = UIBlockerMode.None)
        {
            RegisterUIInternal(windowId, uiType, address, layer, allowMultiple, stackBehavior, blockerMode);
        }

        // ── 打开 ─────────────────────────────────────────────────────────────

        /// <summary>
        /// 异步加载并打开 UI，播放进入动画后返回窗口实例。
        /// </summary>
        public async UniTask<TWindow> OpenUIAsync<TWindow>(
            object userData = null, bool usePool = true)
            where TWindow : UIBaseCore, new()
        {
            var type = typeof(TWindow);

            if (!_registerInfos.TryGetValue(type, out var info))
            {
                GameLog.Error($"[UIManager] 未注册: {type.Name}");
                return null;
            }

            return await OpenUIInternalAsync(info, userData, usePool) as TWindow;
        }

        /// <summary>按稳定 ID 打开窗口；配置、引导、任务等运行时编排使用。</summary>
        public async UniTask<UIBaseCore> OpenUIAsync(
            int windowId,
            object userData = null,
            bool usePool = true)
        {
            if (!_registerInfosById.TryGetValue(windowId, out UIRegisterInfo info))
            {
                GameLog.Error($"[UIManager] 未注册 WindowId={windowId}");
                return null;
            }
            return await OpenUIInternalAsync(info, userData, usePool);
        }

        // ── 关闭 ─────────────────────────────────────────────────────────────

        /// <summary>
        /// 异步关闭（播放退出动画后关闭）。推荐在 UI 内部关闭按钮使用。
        /// </summary>
        public async UniTask CloseUIAsync(UIBaseCore ui, bool destroy = false)
        {
            if (ui == null) return;

            var type = ui.GetType();
            _uiGameObjects.TryGetValue(ui, out GameObject uiObj);
            PublishLifecycle(GetWindowId(type), UIWindowPhase.Closing, ui, uiObj);
            RemoveFromTracking(ui, type);
            DestroyBlocker(ui);

            // 播放退出动画
            await ui.CloseWithAnimAsync();

            // 回收或销毁
            PublishLifecycle(GetWindowId(type), UIWindowPhase.Closed, ui, uiObj);
            RecycleOrDestroy(ui, type, uiObj, destroy);
            _uiGameObjects.Remove(ui);

            GameLog.Log($"[UIManager] 关闭: {type.Name}");
        }

        /// <summary>异步关闭指定类型的第一个实例。</summary>
        public async UniTask CloseUIAsync<T>(bool destroy = false) where T : UIBaseCore
        {
            var type = typeof(T);
            if (_openedUIs.TryGetValue(type, out var list) && list.Count > 0)
                await CloseUIAsync(list[0], destroy);
        }

        /// <summary>异步关闭稳定 ID 对应的第一个实例。</summary>
        public async UniTask CloseUIAsync(int windowId, bool destroy = false)
        {
            UIBaseCore ui = GetUI(windowId);
            if (ui != null) await CloseUIAsync(ui, destroy);
        }

        /// <summary>
        /// 同步关闭（跳过动画）。
        /// 用于批量关闭或不关心过渡效果的场景。
        /// </summary>
        public void CloseUI(UIBaseCore ui, bool destroy = false)
        {
            if (ui == null) return;

            var type = ui.GetType();
            _uiGameObjects.TryGetValue(ui, out GameObject uiObj);
            PublishLifecycle(GetWindowId(type), UIWindowPhase.Closing, ui, uiObj);
            RemoveFromTracking(ui, type);
            DestroyBlocker(ui);
            ui.ForceClose();

            PublishLifecycle(GetWindowId(type), UIWindowPhase.Closed, ui, uiObj);
            if (uiObj != null)
            {
                RecycleOrDestroy(ui, type, uiObj, destroy);
                _uiGameObjects.Remove(ui);
            }
        }

        /// <summary>同步关闭指定类型的第一个实例。</summary>
        public void CloseUI<T>(bool destroy = false) where T : UIBaseCore
        {
            var type = typeof(T);
            if (_openedUIs.TryGetValue(type, out var list) && list.Count > 0)
                CloseUI(list[0], destroy);
        }

        /// <summary>同步关闭稳定 ID 对应的第一个实例。</summary>
        public void CloseUI(int windowId, bool destroy = false)
        {
            UIBaseCore ui = GetUI(windowId);
            if (ui != null) CloseUI(ui, destroy);
        }

        /// <summary>同步关闭指定类型的所有实例。</summary>
        public void CloseAllUI<T>(bool destroy = false) where T : UIBaseCore
        {
            var type = typeof(T);
            if (!_openedUIs.TryGetValue(type, out var list)) return;
            foreach (var ui in new List<UIBaseCore>(list))
                CloseUI(ui, destroy);
        }

        /// <summary>同步关闭所有 UI（Shutdown / 切场景用）。</summary>
        /// <param name="drainPools">是否同时清空对象池（切场景时建议 true）。</param>
        public void CloseAllUI(bool drainPools = false)
        {
            foreach (var type in new List<Type>(_openedUIs.Keys))
            {
                if (!_openedUIs.TryGetValue(type, out var list)) continue;
                foreach (var ui in new List<UIBaseCore>(list))
                    CloseUI(ui, destroy: true);
            }
            _uiStack.Clear();

            if (drainPools)
            {
                foreach (var pool in _uiPools.Values) pool.Clear();
                _uiPools.Clear();
            }
        }

        /// <summary>
        /// 异步关闭指定窗口及其上方的所有导航栈窗口。
        /// </summary>
        /// <typeparam name="T">作为锚点的窗口类型。</typeparam>
        /// <param name="destroy">是否销毁窗口 GameObject；false 时优先回收到对象池。</param>
        public async UniTask CloseUIAndAboveAsync<T>(bool destroy = false) where T : UIBaseCore
        {
            List<UIBaseCore> targets = CollectStackWindowsFromTop(typeof(T), includeAnchor: true);
            if (targets.Count == 0)
            {
                await CloseUIAsync<T>(destroy);
                return;
            }

            for (int i = 0; i < targets.Count; i++)
            {
                await CloseUIAsync(targets[i], destroy);
            }
        }

        /// <summary>
        /// 异步关闭指定窗口上方的所有导航栈窗口，保留锚点窗口自身。
        /// </summary>
        /// <typeparam name="T">作为锚点的窗口类型。</typeparam>
        /// <param name="destroy">是否销毁窗口 GameObject；false 时优先回收到对象池。</param>
        public async UniTask CloseUIsAboveAsync<T>(bool destroy = false) where T : UIBaseCore
        {
            List<UIBaseCore> targets = CollectStackWindowsFromTop(typeof(T), includeAnchor: false);
            for (int i = 0; i < targets.Count; i++)
            {
                await CloseUIAsync(targets[i], destroy);
            }
        }

        // ── 导航 ─────────────────────────────────────────────────────────────

        /// <summary>关闭栈顶 UI（带动画），显示前一个。</summary>
        public async UniTask GoBackAsync()
        {
            if (_uiStack.Count <= 1)
            {
                GameLog.Warning("[UIManager] GoBack: 栈中不足两个 UI");
                return;
            }

            var current = _uiStack.Last.Value;
            _uiStack.RemoveLast();
            await CloseUIAsync(current);

            if (_uiStack.Count > 0)
            {
                var prev = _uiStack.Last.Value;
                if (_uiGameObjects.TryGetValue(prev, out var prevObj))
                    prevObj.SetActive(true);
            }
        }

        /// <summary>清空栈并打开指定 UI。</summary>
        public async UniTask<TWindow> GoToUIAsync<TWindow>(object userData = null)
            where TWindow : UIBaseCore, new()
        {
            CloseAllUI();
            return await OpenUIAsync<TWindow>(userData);
        }

        // ── 查询 ─────────────────────────────────────────────────────────────

        /// <summary>获取指定类型的第一个已打开实例，不存在则返回 null</summary>
        public T    GetUI<T>()         where T : UIBaseCore => _openedUIs.TryGetValue(typeof(T), out var l) && l.Count > 0 ? l[0] as T : null;

        /// <summary>指定类型是否有实例处于打开状态</summary>
        public bool IsUIOpened<T>()    where T : UIBaseCore => _openedUIs.TryGetValue(typeof(T), out var l) && l.Count > 0;

        /// <summary>获取指定类型当前打开的实例数量</summary>
        public int  GetUICount<T>()    where T : UIBaseCore => _openedUIs.TryGetValue(typeof(T), out var l) ? l.Count : 0;

        /// <summary>指定窗口类型是否已经注册。</summary>
        public bool IsUIRegistered<T>() where T : UIBaseCore => _registerInfos.ContainsKey(typeof(T));

        /// <summary>稳定窗口 ID 是否已注册。</summary>
        public bool IsUIRegistered(int windowId) => _registerInfosById.ContainsKey(windowId);

        /// <summary>获取稳定窗口 ID 对应的第一个已打开实例。</summary>
        public UIBaseCore GetUI(int windowId)
        {
            if (!_registerInfosById.TryGetValue(windowId, out UIRegisterInfo info)) return null;
            return _openedUIs.TryGetValue(info.UIType, out List<UIBaseCore> list) && list.Count > 0
                ? list[0]
                : null;
        }

        /// <summary>稳定窗口 ID 是否至少有一个已打开实例。</summary>
        public bool IsUIOpened(int windowId) => GetUI(windowId) != null;

        /// <summary>稳定窗口 ID 当前打开实例数。</summary>
        public int GetUICount(int windowId)
        {
            if (!_registerInfosById.TryGetValue(windowId, out UIRegisterInfo info)) return 0;
            return _openedUIs.TryGetValue(info.UIType, out List<UIBaseCore> list) ? list.Count : 0;
        }

        /// <summary>订阅统一窗口生命周期；返回句柄负责解绑。</summary>
        public IDisposable SubscribeWindowLifecycle(Action<UIWindowLifecycleEvent> handler)
        {
            if (handler == null) throw new ArgumentNullException(nameof(handler));
            var subscription = new LifecycleSubscription(this, handler);
            _lifecycleSubscriptions.Add(subscription);
            return subscription;
        }

        /// <summary>导航栈深度（主要用于判断是否还有历史页面可返回）</summary>
        public int  GetStackDepth()    => _uiStack.Count;

        /// <summary>获取当前栈顶 UI（最后打开的那个）</summary>
        public UIBaseCore GetTopUI()   => _uiStack.Last != null ? _uiStack.Last.Value : null;

        /// <summary>以强类型方式获取栈顶 UI，类型不匹配时返回 null</summary>
        public T    GetTopUI<T>()      where T : class => GetTopUI() as T;

        public bool IsUIInStack<T>() where T : UIBaseCore
        {
            var target = typeof(T);
            foreach (var ui in _uiStack)
                if (ui.GetType() == target) return true;
            return false;
        }

        public List<T> GetAllUI<T>() where T : UIBaseCore
        {
            var result = new List<T>();
            if (_openedUIs.TryGetValue(typeof(T), out var list))
                foreach (var ui in list)
                    if (ui is T t) result.Add(t);
            return result;
        }

        // ── 对象池 ────────────────────────────────────────────────────────────

        /// <summary>
        /// 从对象池取出 UI GameObject；池不存在则自动创建。
        /// 第一次取时 Addressables 加载 Prefab 并实例化，后续从池中直接复用。
        /// </summary>
        private async UniTask<GameObject> GetUIFromPool(Type type, string address, UILayer layer)
        {
            if (!_uiPools.TryGetValue(type, out var pool))
            {
                pool = new GameObjectPool(address, GetLayerCanvas(layer).transform);
                _uiPools[type] = pool;
            }
            return await pool.GetAsync();
        }

        // ── 内部工具 ─────────────────────────────────────────────────────────

        private void RegisterUIInternal(
            int windowId,
            Type uiType,
            string address,
            UILayer layer,
            bool allowMultiple,
            UIStackBehavior stackBehavior,
            UIBlockerMode blockerMode)
        {
            if (string.IsNullOrWhiteSpace(address))
            {
                GameLog.Error($"[UIManager] RegisterUI: {uiType?.Name ?? "(null)"} 的 Address 为空");
                return;
            }
            RegisterInternal(windowId, uiType, address, null, layer, allowMultiple, stackBehavior, blockerMode);
        }

        private void RegisterCodeUIInternal(
            int windowId,
            Type uiType,
            Func<Transform, GameObject> factory,
            UILayer layer,
            bool allowMultiple,
            UIStackBehavior stackBehavior,
            UIBlockerMode blockerMode)
        {
            if (factory == null)
            {
                GameLog.Error($"[UIManager] RegisterCodeUI: {uiType?.Name ?? "(null)"} 的 Factory 为空");
                return;
            }
            RegisterInternal(windowId, uiType, null, factory, layer, allowMultiple, stackBehavior, blockerMode);
        }

        private void RegisterInternal(
            int windowId,
            Type uiType,
            string address,
            Func<Transform, GameObject> factory,
            UILayer layer,
            bool allowMultiple,
            UIStackBehavior stackBehavior,
            UIBlockerMode blockerMode)
        {
            if (windowId < 0)
            {
                GameLog.Error($"[UIManager] WindowId 不能为负：{windowId}");
                return;
            }
            if (uiType == null)
            {
                GameLog.Error("[UIManager] RegisterUI: uiType 为空");
                return;
            }
            if (!typeof(UIBaseCore).IsAssignableFrom(uiType) || uiType.IsAbstract)
            {
                GameLog.Error($"[UIManager] RegisterUI: {uiType.FullName} 不是可实例化的 UIBaseCore 子类");
                return;
            }
            if (uiType.GetConstructor(Type.EmptyTypes) == null)
            {
                GameLog.Error($"[UIManager] RegisterUI: {uiType.FullName} 缺少公共无参构造");
                return;
            }
            if (_registerInfos.ContainsKey(uiType))
            {
                GameLog.Warning($"[UIManager] 窗口类型重复注册: {uiType.Name}");
                return;
            }
            if (windowId > 0 && _registerInfosById.ContainsKey(windowId))
            {
                GameLog.Warning($"[UIManager] WindowId 重复注册: {windowId}");
                return;
            }

            var info = new UIRegisterInfo
            {
                Id = windowId,
                UIType = uiType,
                LogicFactory = () => (UIBaseCore)Activator.CreateInstance(uiType),
                Address = address,
                Factory = factory,
                Layer = layer,
                AllowMultiple = allowMultiple,
                StackBehavior = stackBehavior,
                BlockerMode = blockerMode,
            };
            _registerInfos.Add(uiType, info);
            if (windowId > 0)
            {
                _registerInfosById.Add(windowId, info);
                _windowIdsByType.Add(uiType, windowId);
            }
            GameLog.Log($"[UIManager] 注册{(factory != null ? "代码 " : "")}" +
                        $"UI: id={windowId} type={uiType.Name} layer={layer}");
        }

        private async UniTask<UIBaseCore> OpenUIInternalAsync(
            UIRegisterInfo info,
            object userData,
            bool usePool)
        {
            Type type = info.UIType;
            if (!info.AllowMultiple && _openedUIs.TryGetValue(type, out List<UIBaseCore> existing)
                                    && existing.Count > 0)
            {
                GameLog.Warning($"[UIManager] 已打开且不允许多实例: {type.Name}");
                return existing[0];
            }

            GameObject uiObj;
            if (info.Factory != null)
            {
                Transform parent = GetLayerRoot(info.Layer);
                if (parent == null) return null;
                try { uiObj = info.Factory(parent); }
                catch (Exception ex)
                {
                    GameLog.Error($"[UIManager] 代码 UI 构建失败: {type.Name} - {ex.Message}");
                    return null;
                }
            }
            else
            {
                uiObj = usePool
                    ? await GetUIFromPool(type, info.Address, info.Layer)
                    : await Core.GameEntry.Resource.InstantiateAsync(
                        info.Address, GetLayerCanvas(info.Layer).transform);
            }

            if (uiObj == null)
            {
                GameLog.Error($"[UIManager] 加载失败: {type.Name}");
                return null;
            }

            UIBaseCore window;
            try { window = info.LogicFactory(); }
            catch (Exception ex)
            {
                GameLog.Error($"[UIManager] 创建窗口逻辑失败: {type.FullName} - {ex.Message}");
                GameObject.Destroy(uiObj);
                return null;
            }

            UIView view = uiObj.GetComponent(window.ViewType) as UIView;
            if (view == null)
            {
                GameLog.Error($"[UIManager] 缺少 View 组件: {window.ViewType.Name}");
                GameObject.Destroy(uiObj);
                return null;
            }

            window.Initialize(info.Layer, view, uiObj);
            if (!_openedUIs.TryGetValue(type, out List<UIBaseCore> list))
            {
                list = new List<UIBaseCore>();
                _openedUIs[type] = list;
            }
            list.Add(window);
            _uiGameObjects[window] = uiObj;

            switch (info.StackBehavior)
            {
                case UIStackBehavior.PushToStack:
                    SetStackTopActive(false);
                    _uiStack.AddLast(window);
                    break;
                case UIStackBehavior.ReplaceTop:
                    if (_uiStack.Last != null)
                    {
                        UIBaseCore replaced = _uiStack.Last.Value;
                        _uiStack.RemoveLast();
                        CloseUI(replaced);
                    }
                    _uiStack.AddLast(window);
                    break;
            }

            if (info.BlockerMode != UIBlockerMode.None)
            {
                Action onBlockerClick = info.BlockerMode == UIBlockerMode.ClickToClose
                    ? () => CloseUIAsync(window).Forget()
                    : (Action)null;
                GameObject blocker = UIBlocker.Create(uiObj, info.BlockerMode, onBlockerClick);
                if (blocker != null) _uiBlockers[window] = blocker;
            }

            PublishLifecycle(info.Id, UIWindowPhase.Opening, window, uiObj);
            await window.OpenWithAnimAsync(userData);
            PublishLifecycle(info.Id, UIWindowPhase.Ready, window, uiObj);
            GameLog.Log($"[UIManager] 打开: id={info.Id} type={type.Name}");
            return window;
        }

        private int GetWindowId(Type type)
            => type != null && _windowIdsByType.TryGetValue(type, out int id) ? id : 0;

        private void PublishLifecycle(int windowId, UIWindowPhase phase, UIBaseCore instance, GameObject root)
        {
            if (_lifecycleSubscriptions.Count == 0) return;
            LifecycleSubscription[] snapshot = _lifecycleSubscriptions.ToArray();
            var evt = new UIWindowLifecycleEvent(windowId, phase, instance, root);
            for (int i = 0; i < snapshot.Length; i++)
            {
                try { snapshot[i].Handler?.Invoke(evt); }
                catch (Exception ex)
                {
                    GameLog.Error($"[UIManager] 窗口生命周期订阅者异常: {ex}");
                }
            }
        }

        private void RemoveLifecycleSubscription(LifecycleSubscription subscription)
            => _lifecycleSubscriptions.Remove(subscription);

        /// <summary>
        /// 返回指定层级的 Canvas，委托给 UIBootstrap。
        /// 若 bootstrap 未注入（配置缺失），返回 null 并打印错误。
        /// </summary>
        private Canvas GetLayerCanvas(UILayer layer)
        {
            if (_bootstrap == null)
            {
                GameLog.Error("[UIManager] GetLayerCanvas: bootstrap 未注入，请确认 GameEntry 已调用 SetBootstrap");
                return null;
            }
            return _bootstrap.GetLayerCanvas(layer);
        }

        /// <summary>
        /// 从导航栈顶开始收集窗口，直到遇到指定类型的锚点窗口。
        /// </summary>
        /// <param name="anchorType">锚点窗口类型。</param>
        /// <param name="includeAnchor">是否把锚点窗口自身也加入结果。</param>
        /// <returns>按栈顶到栈底顺序排列的待关闭窗口列表；找不到锚点时返回空列表。</returns>
        private List<UIBaseCore> CollectStackWindowsFromTop(Type anchorType, bool includeAnchor)
        {
            var result = new List<UIBaseCore>();
            for (LinkedListNode<UIBaseCore> node = _uiStack.Last; node != null; node = node.Previous)
            {
                bool isAnchor = node.Value.GetType() == anchorType;
                if (isAnchor)
                {
                    if (includeAnchor)
                    {
                        result.Add(node.Value);
                    }

                    return result;
                }

                result.Add(node.Value);
            }

            result.Clear();
            return result;
        }

        /// <summary>
        /// 从 _openedUIs 列表和 _uiStack 中移除指定窗口的追踪记录。
        /// LinkedList 支持 O(1) 节点删除，无需重建整个栈。
        /// </summary>
        private void RemoveFromTracking(UIBaseCore ui, Type type)
        {
            if (_openedUIs.TryGetValue(type, out var list))
            {
                list.Remove(ui);
                if (list.Count == 0) _openedUIs.Remove(type);
            }

            var node = _uiStack.Find(ui);
            if (node != null)
            {
                bool wasTop = node == _uiStack.Last;
                _uiStack.Remove(node);

                // 关闭的是栈顶页面时，恢复显示新的栈顶，保持"入栈隐藏 / 出栈恢复"的页面路由对称性。
                // GoBackAsync 会先自行 RemoveLast 再关闭，故其关闭路径走不到这里（由它自己恢复显示），不会重复激活。
                if (wasTop)
                {
                    SetStackTopActive(true);
                }
            }
        }

        /// <summary>
        /// 设置当前导航栈顶窗口 GameObject 的激活状态。
        /// 用于 PushToStack 入栈时隐藏被覆盖页面、出栈时恢复显示，栈为空或对象缺失时安全跳过。
        /// </summary>
        /// <param name="active">目标激活状态。</param>
        private void SetStackTopActive(bool active)
        {
            if (_uiStack.Last != null
                && _uiGameObjects.TryGetValue(_uiStack.Last.Value, out var topObj)
                && topObj != null)
            {
                topObj.SetActive(active);
            }
        }

        /// <summary>
        /// 销毁并清理窗口对应的遮罩。
        /// </summary>
        private void DestroyBlocker(UIBaseCore ui)
        {
            if (_uiBlockers.TryGetValue(ui, out var blockerGO))
            {
                UIBlocker.Destroy(blockerGO);
                _uiBlockers.Remove(ui);
            }
        }

        /// <summary>
        /// 根据 destroy 参数决定 UI GameObject 的归宿：
        /// <list type="bullet">
        ///   <item><description>destroy=true  → 调用 DestroyUI() 触发 OnDestroy，然后销毁 GameObject</description></item>
        ///   <item><description>destroy=false → 优先归还对象池（SetActive=false），池不存在则 Destroy</description></item>
        /// </list>
        /// </summary>
        private void RecycleOrDestroy(UIBaseCore ui, Type type, GameObject uiObj, bool destroy)
        {
            if (destroy)
            {
                ui?.DestroyUI();
                if (uiObj != null) GameObject.Destroy(uiObj);
            }
            else
            {
                if (_uiPools.TryGetValue(type, out var pool) && uiObj != null)
                    pool.Release(uiObj);
                else if (uiObj != null)
                {
                    ui?.DestroyUI();
                    GameObject.Destroy(uiObj);
                }
            }
        }
    }
}
