using System;
using System.Collections.Generic;
using Cysharp.Threading.Tasks;
using Framework;
using Framework.Core.Auth;
using Framework.HotUpdate;
using Framework.Input;
using Framework.UI;
using UnityEngine;

namespace Framework.Core
{
    /// <summary>
    /// 游戏入口单例。
    /// 职责：初始化所有框架 Manager，并将启动流程交给 LaunchFlow 执行。
    /// 不包含任何业务逻辑（版本检查、热更等由 LaunchFlow 负责）。
    /// </summary>
    public class GameEntry : MonoSingleton<GameEntry>
    {
        // 所有框架组件列表
        private List<FrameworkComponent> _components = new List<FrameworkComponent>();

        // ── Inspector 序列化字段 ──────────────────────────────────────────────

        /// <summary>UI 基础设施引导组件（场景中放置的 UIBootstrap Prefab 实例）。持有 UIRoot Canvas，在 InitializeManagers 时注入给 UIManager。</summary>
        [SerializeField] private UIBootstrap _uiBootstrap;

        /// <summary>LoadingScreen Prefab（不含 Canvas 组件）。Start() 时实例化到 Canvas_System 层，生命周期早于 Addressables。</summary>
        [SerializeField] private LoadingView _loadingViewPrefab;

        /// <summary>LoginScreen Prefab（不含 Canvas）。LaunchFlow 完成后实例化，与 Loading 分离。</summary>
        [SerializeField] private LoginView _loginViewPrefab;

        /// <summary>ReconnectPanel Prefab（不含独立 Canvas 组件）。Start() 时预实例化到 Canvas_System 层，默认隐藏，断线时由 NetworkManager 事件驱动显示。</summary>
        [SerializeField] private GameObject _reconnectPanelPrefab;

        /// <summary>NetworkWaiting Prefab（不含独立 Canvas 组件）。Start() 时预实例化到 Canvas_System 层，默认隐藏。挂载 NetworkWaitingUI 组件，自动订阅 NetworkManager 事件驱动转圈显隐。</summary>
        [SerializeField] private GameObject _networkWaitingPrefab;

        /// <summary>控制网络协议 SEND/RECV 调试日志的总开关，由 GameEntry Inspector 配置。</summary>
        [Header("网络协议日志")]
        [SerializeField] private bool _enableProtocolLog = true;

        /// <summary>控制心跳协议是否参与协议日志打印，默认关闭以避免控制台刷屏。</summary>
        [SerializeField] private bool _enableHeartbeatProtocolLog = false;

        /// <summary>目标帧率。移动端 Unity 默认 30fps，必须显式设置；0 或负值表示不干预（保持平台默认）。</summary>
        [Header("应用性能")]
        [SerializeField] private int _targetFrameRate = 60;

        /// <summary>是否禁止屏幕自动休眠。联机对局中等待对手时通常无输入，不设会锁屏断线。</summary>
        [SerializeField] private bool _neverSleep = true;

        // ── Manager 静态访问点 ────────────────────────────────────────────────

        /// <summary>资源管理器 — Addressables 加载、实例化、释放</summary>
        public static ResourceManager      Resource  { get; private set; }

        /// <summary>UI 管理器 — 窗口打开/关闭/导航/动画/对象池</summary>
        public static UIManager            UI        { get; private set; }

        /// <summary>网络管理器 — TCP 连接、心跳、断线重连</summary>
        public static NetworkManager       Network   { get; private set; }

        /// <summary>轻提示管理器 — 全局 Toast / 飘字请求调度</summary>
        public static TipManager           Tips      { get; private set; }

        /// <summary>配置管理器 — 游戏配置表数据读取</summary>
        public static ConfigManager        RefData   { get; private set; }

        /// <summary>事件管理器 — 全局事件订阅与派发</summary>
        public static EventManager         Event     { get; private set; }

        /// <summary>音频管理器 — BGM / SFX 播放控制</summary>
        public static AudioManager         Audio     { get; private set; }

        /// <summary>场景管理器 — 异步加载场景、过场管理</summary>
        public static SceneManager         Scene     { get; private set; }

        /// <summary>定时器管理器 — 延迟调用、循环定时</summary>
        public static TimerManager         Timer     { get; private set; }

        /// <summary>热更新管理器 — 版本检查、资源/代码热更、HybridCLR 程序集加载</summary>
        public static HotUpdateManager     HotUpdate { get; private set; }
        /// <summary>登录状态机管理器 — 登录流程状态、错误码映射、弹窗策略</summary>
        public static AuthManager          Auth      { get; private set; }

        /// <summary>游戏阶段管理器 — 执行单次阶段切换，不维护历史栈。</summary>
        public static GameStageManager           StageManager    { get; private set; }

        /// <summary>游戏阶段导航管理器 — 维护阶段返回栈，处理 Push / Back。</summary>
        public static GameStageNavigationManager StageNavigation { get; private set; }

        /// <summary>输入管理器 — 统一指针采样、手势采样与输入门禁。</summary>
        public static InputManager Input { get; private set; }

        /// <summary>平台 SDK 管理器 — 渠道登录/支付/推送/合规统一访问点（未注册渠道时 Mock 兜底）。</summary>
        public static Framework.Sdk.SdkManager Sdk { get; private set; }

        // ── 生命周期 ─────────────────────────────────────────────────────────

        protected override void Awake()
        {
            base.Awake();

#if !UNITY_EDITOR
            // 非 Editor 环境（EXE / 真机）自动挂载屏幕日志面板
            if (GetComponent<RuntimeConsole>() == null)
                gameObject.AddComponent<RuntimeConsole>();
#endif

            Debug.Log("[GameEntry] 开始初始化框架...");
            ApplyPerformanceSettings();
            InitializeManagers();
            Application.lowMemory += HandleLowMemory;
            Debug.Log("[GameEntry] 框架初始化完成");
        }

        /// <summary>
        /// 应用 Inspector 配置的帧率与休眠策略。vSync 开启时 targetFrameRate 不生效，
        /// 因此设置帧率前显式关闭 vSync（移动端本就忽略 vSync，PC 上以配置帧率为准）。
        /// </summary>
        private void ApplyPerformanceSettings()
        {
            if (_targetFrameRate > 0)
            {
                QualitySettings.vSyncCount = 0;
                Application.targetFrameRate = _targetFrameRate;
            }

            if (_neverSleep)
            {
                Screen.sleepTimeout = SleepTimeout.NeverSleep;
            }
        }

        /// <summary>
        /// 系统低内存回调：先让各框架组件清理自持缓存（对象池等），再广播事件让业务层跟进，
        /// 最后卸载未引用资产。异步卸载可能引起短暂卡顿，但低内存时避免被系统杀进程优先。
        /// </summary>
        private void HandleLowMemory()
        {
            Debug.LogWarning("[GameEntry] 收到系统低内存警告，开始释放可重建缓存");

            for (int i = 0; i < _components.Count; i++)
            {
                try { _components[i].OnLowMemory(); }
                catch (Exception ex) { LogComponentError("OnLowMemory", _components[i], ex); }
            }

            Event?.Publish(GameMessage.LowMemoryWarning);
            Resources.UnloadUnusedAssets();
        }

        /// <summary>
        /// 实例化随包 UI Prefab（LoadingScreen / ReconnectPanel）并交给 LaunchFlow 启动。
        /// Manager 初始化已在 Awake 完成，Start 只负责驱动业务启动序列。
        /// </summary>
        private void Start()
        {
            var systemRoot = UI.GetLayerRoot(UILayer.System);

            if (_loadingViewPrefab == null)
            {
                Debug.LogError("[GameEntry] _loadingViewPrefab 未赋值，请在 Inspector 中拖拽赋值");
                return;
            }

            var loadingGO   = Instantiate(_loadingViewPrefab.gameObject, systemRoot);
            var loadingView = loadingGO.GetComponent<LoadingView>();
            var loading     = new LoadingWindow(loadingView);

            if (_reconnectPanelPrefab != null)
                Instantiate(_reconnectPanelPrefab, systemRoot);

            if (_networkWaitingPrefab != null)
                Instantiate(_networkWaitingPrefab, systemRoot);

            RunLaunchAndLoginAsync(loading, systemRoot).Forget();
        }

        /// <summary>
        /// 启动链路：LaunchFlow 完成并销毁 Loading 后，进入随包 Login 流程。
        /// </summary>
        private async UniTaskVoid RunLaunchAndLoginAsync(LoadingWindow loading, Transform systemRoot)
        {
            LaunchFlowOutcome outcome = await LaunchFlow.RunAsync(loading);
            if (outcome != LaunchFlowOutcome.ReadyForLogin)
                return;

            if (_loginViewPrefab == null)
            {
                Debug.LogError("[GameEntry] _loginViewPrefab 未赋值，请在 Inspector 中拖拽 LoginView.prefab");
                return;
            }

            LoginResult loginResult = await LoginFlow.RunAsync(_loginViewPrefab, systemRoot);
            Debug.Log($"[GameEntry] 登录完成 userId={loginResult.UserId} token={(string.IsNullOrEmpty(loginResult.SessionToken) ? "(none)" : "ok")}");
            // 登录成功后由业务层接管（如 StageNavigation.ReplaceStageAsync）。
        }

        /// <summary>
        /// 初始化所有 Manager（按依赖顺序）。
        /// UIManager 创建后立即注入 UIBootstrap，确保后续 OpenUIAsync 调用时 Canvas 层级已就绪。
        /// </summary>
        private void InitializeManagers()
        {
            // 崩溃回捞最早挂接：后续任何 Manager 初始化抛出的未捕获异常也能落盘；
            // 积压记录的上报在全部 Manager 就绪后异步尝试（端点未配置时仅本地缓存）。
            Telemetry.CrashReporter.Install();

            Event     = AddComponent<EventManager>();
            Timer     = AddComponent<TimerManager>();
            Resource  = AddComponent<ResourceManager>();

            // SDK 管理器尽早就位：渠道实现由业务组合根注册（RegisterProvider），
            // 初始化时机由业务显式驱动（通常在 LaunchFlow 前 / HotfixEntry 内）。
            Sdk = AddComponent<Framework.Sdk.SdkManager>();

            UI = AddComponent<UIManager>();
            UI.SetBootstrap(_uiBootstrap);

            Input = AddComponent<InputManager>();
            Input.SetBootstrap(_uiBootstrap);

            Network         = AddComponent<NetworkManager>();
            ApplyNetworkLogSettings();
            Tips            = AddComponent<TipManager>();
            Auth            = AddComponent<AuthManager>();

            // 组合根注入：让网络层在断线重连后，经鉴权层静默重放登录握手恢复服务端会话身份，
            // 否则重连得到的匿名连接会导致快照/落子等业务请求被服务端静默丢弃（断线重连失效）。
            Network.SetReauthenticator(() => Auth.ReauthenticateAsync());

            RefData         = AddComponent<ConfigManager>();
            Audio           = AddComponent<AudioManager>();
            Scene           = AddComponent<SceneManager>();
            StageManager    = AddComponent<GameStageManager>();
            StageNavigation = AddComponent<GameStageNavigationManager>();
            HotUpdate       = AddComponent<HotUpdateManager>();

            // 上一次运行留下的崩溃记录：后台尝试上报（不阻塞启动，失败静默保留下次再试）。
            Telemetry.CrashReporter.TryUploadPendingAsync(AppConfig.Load().CrashReportUrl).Forget();

            Debug.Log("[GameEntry] 所有 Manager 初始化完成");
        }

        /// <summary>
        /// 应用 GameEntry Inspector 上配置的网络协议日志开关。
        /// </summary>
        private void ApplyNetworkLogSettings()
        {
            Network.EnableProtocolLog(_enableProtocolLog);
            Network.EnableHeartbeatProtocolLog(_enableHeartbeatProtocolLog);
        }

        /// <summary>
        /// 添加框架组件
        /// </summary>
        /// <typeparam name="T">组件类型</typeparam>
        /// <returns>组件实例</returns>
        private T AddComponent<T>() where T : FrameworkComponent, new()
        {
            T component = new T();
            _components.Add(component);
            component.OnInit();
            Debug.Log($"[GameEntry] 初始化组件: {typeof(T).Name}");
            return component;
        }

        // ── Unity 事件转发 ────────────────────────────────────────────────────

        private void Update()
        {
            float dt = Time.deltaTime;
            for (int i = 0; i < _components.Count; i++)
            {
                try { _components[i].OnUpdate(dt); }
                catch (Exception ex) { LogComponentError("OnUpdate", _components[i], ex); }
            }
        }

        private void LateUpdate()
        {
            float dt = Time.deltaTime;
            for (int i = 0; i < _components.Count; i++)
            {
                try { _components[i].OnLateUpdate(dt); }
                catch (Exception ex) { LogComponentError("OnLateUpdate", _components[i], ex); }
            }
        }

        private void FixedUpdate()
        {
            float dt = Time.fixedDeltaTime;
            for (int i = 0; i < _components.Count; i++)
            {
                try { _components[i].OnFixedUpdate(dt); }
                catch (Exception ex) { LogComponentError("OnFixedUpdate", _components[i], ex); }
            }
        }

        private void OnApplicationPause(bool isPaused)
        {
            for (int i = 0; i < _components.Count; i++)
            {
                try { _components[i].OnApplicationPause(isPaused); }
                catch (Exception ex) { LogComponentError("OnApplicationPause", _components[i], ex); }
            }
        }

        private void OnApplicationFocus(bool hasFocus)
        {
            for (int i = 0; i < _components.Count; i++)
            {
                try { _components[i].OnApplicationFocus(hasFocus); }
                catch (Exception ex) { LogComponentError("OnApplicationFocus", _components[i], ex); }
            }
        }

        /// <summary>
        /// 记录单个框架组件在某生命周期阶段抛出的异常。
        /// 仅在异常发生时构造字符串，正常帧无开销；用于隔离单个组件异常，避免拖垮整轮遍历。
        /// </summary>
        /// <param name="phase">生命周期阶段名（如 OnUpdate）。</param>
        /// <param name="component">抛出异常的组件。</param>
        /// <param name="ex">捕获到的异常。</param>
        private static void LogComponentError(string phase, FrameworkComponent component, Exception ex)
        {
            Debug.LogError($"[GameEntry] 组件 {component.GetType().Name} {phase} 异常，已隔离不影响其它组件");
            Debug.LogException(ex);
        }

        /// <summary>
        /// 应用退出时清理
        /// </summary>
        protected override void OnApplicationQuit()
        {
            Debug.Log("[GameEntry] 开始清理框架...");

            Application.lowMemory -= HandleLowMemory;

            // 反向顺序关闭所有组件；逐个 try/catch 隔离，确保某组件清理异常不影响其余组件关闭
            for (int i = _components.Count - 1; i >= 0; i--)
            {
                try
                {
                    _components[i].OnShutdown();
                    Debug.Log($"[GameEntry] 关闭组件: {_components[i].GetType().Name}");
                }
                catch (Exception ex)
                {
                    LogComponentError("OnShutdown", _components[i], ex);
                }
            }

            _components.Clear();

            Debug.Log("[GameEntry] 框架清理完成");

            base.OnApplicationQuit();
        }
    }
}
