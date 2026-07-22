using System;
using System.Collections.Generic;
using System.Threading;
using Cysharp.Threading.Tasks;
using Framework;
using Framework.Core.Auth;
using Framework.Core.Errors;
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

        // 强制登出 / 玩家主动登出的清凭据编排订阅（进程级，退出时释放）。
        private IDisposable _forceLogoutSub;
        private IDisposable _playerLogoutSub;

        // 应用主状态机（Login ⇄ InGame）：登录、业务接管、登出拆卸、回登录全部由它串行编排。
        private AppFlow _appFlow;
        private CancellationTokenSource _appFlowCts;

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

        /// <summary>是否挂载性能 HUD（FPS/内存/GC/资源句柄/RTT 常驻叠加）。仅 Editor / Development Build 生效，正式包零开销。</summary>
        [SerializeField] private bool _enablePerfHud = true;

        /// <summary>是否挂载线上性能采样（全构建生效，约 1 条 perf_window 埋点/分钟，见 Performance/PERFORMANCE_GUIDE.md）。</summary>
        [SerializeField] private bool _enablePerfSampling = true;

        /// <summary>是否按设备分级自动映射 Quality Level（低端→最低档等，见 Performance/DEVICE_TIER_GUIDE.md）。关闭后仍分级（业务可读档位），只是不动画质。</summary>
        [SerializeField] private bool _autoQualityByDeviceTier = true;

        /// <summary>是否挂载本地通知生命周期接线（切后台排程/回前台清理，见 Notifications/NOTIFICATIONS_GUIDE.md）。</summary>
        [SerializeField] private bool _enableLocalNotifications = true;

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

        /// <summary>埋点管理器 — 事件缓冲/批量上报/断电落盘（后端可注入，默认按 AppConfig.AnalyticsUrl）。</summary>
        public static Framework.Analytics.AnalyticsManager Analytics { get; private set; }

        /// <summary>远程配置管理器 — 键值配置/功能开关/灰度放量（磁盘缓存 last-known-good，后端可注入）。</summary>
        public static Framework.RemoteConfig.RemoteConfigManager RemoteConfig { get; private set; }

        /// <summary>
        /// 调试命令总线 — 框架与业务的调试/GM 命令统一注册与执行入口。
        /// 授权 fail-closed：Editor / Development Build 由本组合根授予 Development；
        /// 正式包默认 None，GM 白名单账号经业务侧服务端验证后自行
        /// <c>Commands.SetGrantedAccess(CommandAccessLevel.Privileged)</c>，登出时撤销回 None。
        /// </summary>
        public static Framework.Diagnostics.CommandRegistry Commands { get; private set; }

        /// <summary>
        /// 配置驱动的共享红点 DAG — 业务只写稳定 ID 对应的 Signal，Aggregate 按目录关系自动聚合。
        /// UI 挂 <see cref="RedDotBadge"/> 或代码 Subscribe 绑定 ID；账号切换时框架统一清运行态并加载
        /// LocalAccount 已看版本。独立玩法仍可自建局部 <see cref="Framework.Foundation.RedDotTree"/>。
        /// </summary>
        public static Framework.Foundation.RedDotService RedDots { get; private set; }

        /// <summary>通用无副作用条件求值服务；业务模块只注册领域叶子 Evaluator。</summary>
        public static Framework.Foundation.RuleService Rules { get; private set; }

        /// <summary>通用作用域触发器服务；UI/计时器由框架内置，领域成功事件由业务注册。</summary>
        public static Framework.Foundation.TriggerService Triggers { get; private set; }

        /// <summary>通用异步动作执行服务；配置实例通过稳定 ActionId 寻址。</summary>
        public static Framework.Foundation.ActionService Actions { get; private set; }

        /// <summary>稳定 UI TargetId 到当前激活 Rect/Button 实例的运行时目录。</summary>
        public static UITargetRegistry UiTargets { get; private set; }

        /// <summary>
        /// 中间层「框架自带业务模块」宿主（ADR-008）。L3 在配置库就绪前经 <c>Use</c> 登记模块，
        /// GameEntry 在业务入口前驱动两阶段（RegisterCapabilities → 冻结编排 → StartAsync），退出时逆序 Dispose。
        /// 引导/红点等运行器由各模块自身持有并暴露访问点（如 <c>Guides.Runner</c>），不再挂在 GameEntry 上。
        /// </summary>
        public static FrameworkModuleHost Modules { get; private set; }

        // ── 生命周期 ─────────────────────────────────────────────────────────

        protected override void Awake()
        {
            base.Awake();

            // 命令总线最早就位：Manager 初始化期间即可注册自己的调试命令。
            // 授权 fail-closed：仅开发环境授予 Development；正式包保持 None，白名单授权由业务鉴权路径驱动。
            Commands = new Framework.Diagnostics.CommandRegistry();
#if UNITY_EDITOR || DEVELOPMENT_BUILD
            Commands.SetGrantedAccess(Framework.Diagnostics.CommandAccessLevel.Development);
#endif

            // 框架只创建空服务，不持有业务拓扑。UI 可在目录安装前先订阅；热更业务侧在配置库就绪后
            // 从标准 ConfigData 五张表组装目录，内容事务保证配置与引用它的代码处于同一发行版本。
            // 订阅者异常送日志诊断，单个 UI 回调不会中断其它订阅者。
            RedDots = new Framework.Foundation.RedDotService
            {
                ObserverErrorSink = ex =>
                {
                    Debug.LogError("[GameEntry] 红点订阅者异常（已隔离）");
                    Debug.LogException(ex);
                },
            };

            Rules = new Framework.Foundation.RuleService
            {
                ObserverErrorSink = ex => LogOrchestrationError("Rule", ex),
            };
            Triggers = new Framework.Foundation.TriggerService
            {
                ObserverErrorSink = ex => LogOrchestrationError("Trigger", ex),
            };
            Actions = new Framework.Foundation.ActionService
            {
                ObserverErrorSink = ex => LogOrchestrationError("Action", ex),
            };
            UiTargets = new UITargetRegistry
            {
                ObserverErrorSink = ex => LogOrchestrationError("UITarget", ex),
            };

#if !UNITY_EDITOR && DEVELOPMENT_BUILD
            // 非 Editor 的 Development Build 自动挂载屏幕日志面板（接入命令总线后带命令输入行）
            if (GetComponent<RuntimeConsole>() == null)
                gameObject.AddComponent<RuntimeConsole>();
            GetComponent<RuntimeConsole>().AttachCommands(Commands);
#endif

#if UNITY_EDITOR || DEVELOPMENT_BUILD
            // 性能 HUD：FPS/内存/GC/Addressables 句柄/RTT 常驻叠加（正式包整类被剥离，零开销）
            if (_enablePerfHud && GetComponent<PerfHud>() == null)
                gameObject.AddComponent<PerfHud>();
#endif

            Debug.Log("[GameEntry] 开始初始化框架...");
            ApplyPerformanceSettings();
            // 通用补间（PrimeTween）容量与默认缓动一次性引导：须早于任何 UI 过渡 / 场景动画。
            TweenBootstrap.Initialize();
            InitializeManagers();
            // 内置 UI/计时器 Rule、Trigger、Action 只依赖框架服务，在业务 Catalog 安装前一次性注册。
            // 引导表现（GuideFocus/Clear）等业务能力由对应模块在 RegisterCapabilities 阶段注册（ADR-008）。
            UIOrchestrationBuiltins.Register(Rules, Triggers, Actions, UI, UiTargets);

            // 中间层模块宿主（ADR-008）：L3 经 Modules.Use 登记红点/引导等自带业务模块，
            // 由 EnterBusinessSessionAsync 在配置库就绪后驱动两阶段。此处仅创建空宿主。
            Modules = new FrameworkModuleHost
            {
                ModuleErrorSink = (phase, module, ex) =>
                    LogOrchestrationError($"Module:{module?.GetType().Name}:{phase}", ex),
            };

            // 线上性能采样：全构建生效，窗口聚合后经 Analytics 低频上报（挂在 Manager 之后，
            // 上报时经静态访问点取 Analytics，未就绪则静默跳过）
            if (_enablePerfSampling && GetComponent<Framework.Performance.PerfSampler>() == null)
                gameObject.AddComponent<Framework.Performance.PerfSampler>();

            // 本地通知生命周期接线：切后台按注册表结算排程，回前台全部取消
            if (_enableLocalNotifications && GetComponent<Framework.Notifications.LocalNotificationRelay>() == null)
                gameObject.AddComponent<Framework.Notifications.LocalNotificationRelay>();

            Application.lowMemory += HandleLowMemory;
            Debug.Log("[GameEntry] 框架初始化完成");
        }

        /// <summary>
        /// 应用 Inspector 配置的帧率与休眠策略。vSync 开启时 targetFrameRate 不生效，
        /// 因此设置帧率前显式关闭 vSync（移动端本就忽略 vSync，PC 上以配置帧率为准）。
        /// </summary>
        private void ApplyPerformanceSettings()
        {
            // 设备分级先行：画质档位在任何重资产加载前定下，业务与 perf_window 埋点均可读档位
            Framework.Performance.DeviceTierService.Initialize(_autoQualityByDeviceTier);

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

        private static void LogOrchestrationError(string source, Exception error)
        {
            Debug.LogError($"[GameEntry] {source} 编排回调/执行器异常（已隔离）");
            if (error != null) Debug.LogException(error);
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

            Modules?.BroadcastLowMemory();
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
        /// 启动链路：LaunchFlow（热更/资源引导，只跑一次、不进状态机）完成并销毁 Loading 后，
        /// 把粗粒度生命周期交给 AppFlow 主状态机：Login ⇄ InGame，登出拆卸后自动回到登录页。
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

            // 组合根注入：AppFlow 是纯逻辑状态机，真实实现全部在此接线。
            // 业务入口钩子的说明与时序约束见 OnBusinessEntryAsync 文档：
            //   必须晚于 BindLoggedInIdentity——PlayerLoginSuccess 事件在登录过程中即发布、早于身份贯通，
            //   业务若在那时读存档会落到错误账号目录；读账号数据的接管逻辑一律走该钩子而非该事件。
            //   业务若接入 AB 实验扩展包（com.frameworkbase.experiment，主干不引用），
            //   请在入口内调用 Experiments.Instance.SetUnitId(loginResult.UserId) 切换分桶单元。
            _appFlowCts = new CancellationTokenSource();
            _appFlow = new AppFlow(new AppFlowHooks
            {
                RunLoginAsync = token =>
                    LoginFlow.RunAsync(_loginViewPrefab, systemRoot).AttachExternalCancellation(token),
                BindIdentity = result =>
                {
                    // 登录成功后统一贯通玩家身份到存档隔离 / 埋点 / 远配 / 崩溃归因（组合根职责）。
                    BindLoggedInIdentity(result);
                    Debug.Log($"[GameEntry] 登录完成 userId={result.UserId} token={(string.IsNullOrEmpty(result.SessionToken) ? "(none)" : "ok")}");
                },
                EnterBusinessAsync = EnterBusinessSessionAsync,
                ExitBusiness = NotifyBusinessExit,
                LogoutAuth = reason => Auth?.Logout(reason),
                ClearIdentity = ClearLoggedInIdentity,
                Info = message => Debug.Log($"[AppFlow] {message}"),
                Error = (message, ex) =>
                {
                    Debug.LogError($"[AppFlow] {message}");
                    if (ex != null) Debug.LogException(ex);
                },
            });
            await _appFlow.RunAsync(_appFlowCts.Token);
        }

        /// <summary>
        /// 登录后业务入口钩子（框架保留的唯一「业务接管」扩展点）。
        /// 框架在登录成功且 <see cref="BindLoggedInIdentity"/> 贯通玩家身份之后、每个登录会话调用一次
        /// （登出回登录页后再次登录会再次调用，业务须支持重入初始化）。
        /// 业务在此进主场景 / 开主界面 / 读账号存档。未注册时（纯框架壳）为 null、无副作用。
        /// 线程约定：在主线程 await 调用；实现内异常被框架捕获隔离。
        /// 入口 await 期间收到的登出请求会被 AppFlow 后置合并：入口完成后立即拆卸回登录。
        /// </summary>
        public static Func<LoginResult, Cysharp.Threading.Tasks.UniTask> OnBusinessEntryAsync { get; set; }

        /// <summary>
        /// 业务会话进入前的同步装配钩子。调用时配置数据库已经完成启动安装，且早于账号红点已看记录加载；
        /// 适合由热更侧初始化依赖 ConfigData 的业务基础服务。实现异常会阻止本次业务会话进入。
        /// </summary>
        public static Action OnBeforeBusinessEntry { get; set; }

        /// <summary>
        /// 编排 Catalog 冻结钩子（ADR-008）。L3 在此用配置构建并 Initialize 全局 Rule/Trigger/Action 目录；
        /// 由 <see cref="EnterBusinessSessionAsync"/> 在模块 RegisterCapabilities 之后、StartAsync 之前调用一次，
        /// 确保各模块的能力处理器（如引导表现 Action）已注册完毕再冻结校验。实现须幂等（重复登录会再次调用）。
        /// </summary>
        public static Action OnFreezeOrchestration { get; set; }

        /// <summary>
        /// 身份已经贯通后先加载当前账号的 LocalAccount 红点已看版本，再把会话交给业务层。
        /// 这样业务 Provider 首次提交快照时即可得到稳定的 EffectiveCount，不会先亮后灭。
        /// </summary>
        private static async UniTask EnterBusinessSessionAsync(
            LoginResult loginResult,
            CancellationToken cancellationToken)
        {
            OnBeforeBusinessEntry?.Invoke();
            // 中间层模块两阶段（ADR-008）：Phase 1 各模块注册能力 → 冻结全局编排 Catalog（L3 经
            // OnFreezeOrchestration 构建并 Initialize）→ Phase 2 各模块启动。模块清单为空时全为空跑。
            Modules.RegisterCapabilities();
            OnFreezeOrchestration?.Invoke();
            await Modules.StartAsync();
            // 账号进入（ADR-008）：宿主在业务入口前有序 await 各模块的账号级加载（如红点已看版本），
            // 避免先亮后灭。引导等模块此钩子为空实现。
            await Modules.OnAccountEnterAsync(cancellationToken);
            // TODO(ADR-008 步骤3a)：下面这行红点账号加载将迁入 RedDotModule.OnAccountEnterAsync 后删除。
            await Framework.RedDot.RedDotAccountSession.BeginAsync(RedDots);
            cancellationToken.ThrowIfCancellationRequested();
            if (OnBusinessEntryAsync != null)
                await OnBusinessEntryAsync(loginResult);
        }

        /// <summary>
        /// 业务会话退出钩子。框架在清空登录凭据与玩家身份之前同步调用，业务应在此取消账号级定时器、
        /// 保存当前账号数据并关闭业务 UI。实现必须快速完成；异常会被隔离，不能阻断框架清理。
        /// 参数为退出原因（player_logout / server_force_logout:* / sdk_session_invalidated:* / application_quit）。
        /// </summary>
        public static Action<string> OnBusinessExit { get; set; }

        /// <summary>
        /// 登录成功后把「玩家身份」统一贯通到所有需要按用户归因 / 隔离的框架子系统。
        /// 这是组合根的职责：必须在业务层接管（读写账号存档、拉取用户维度远配 / 实验）之前调用，
        /// 否则会出现存档落在 guest 目录、埋点 / 远配 / 崩溃归因缺失用户维度等隐性错配。
        /// <para>
        /// 贯通范围（框架主干可达子系统）：
        ///   1. <see cref="Framework.Save.SaveManager"/> — 切换存档目录到该账号，实现账号级存档隔离；
        ///   2. <see cref="Analytics"/> — 设置埋点用户维度；
        ///   3. <see cref="RemoteConfig"/> — 设置远程配置用户维度（服务端按用户定向）；
        ///   4. <see cref="Telemetry.CrashReporter"/> — 崩溃归因按玩家定位。
        /// AB 实验（com.frameworkbase.experiment）是可选扩展包、主干不引用，业务须在登录成功后
        /// 自行调用 Experiments.Instance.SetUnitId(userId) 切换分桶单元（见调用点注释）。
        /// </para>
        /// </summary>
        private void BindLoggedInIdentity(LoginResult loginResult)
        {
            if (!loginResult.Success || string.IsNullOrEmpty(loginResult.UserId))
                return;

            string userId = loginResult.UserId;

            // 存档账号目录隔离：必须早于业务读写账号存档，否则数据会默默落进 guest 目录。
            Framework.Save.SaveManager.Instance.SetCurrentUser(userId);
            // 运营维度贯通：埋点 / 远程配置按玩家归因与定向（Manager 未就绪时静默跳过）。
            Analytics?.SetUserId(userId);
            RemoteConfig?.SetUserId(userId);
            // 崩溃归因按玩家定位。
            Telemetry.CrashReporter.SetUser(userId);
        }

        /// <summary>
        /// 组合根编排：把「服务端强制登出（顶号/封禁/会话失效）」「玩家主动登出」「渠道会话失效」三条
        /// 信号统一改调 <see cref="RequestLogout"/>——只记原因 + 唤醒 AppFlow，不在事件回调栈里同步拆卸；
        /// 真正的拆卸只在 AppFlow 的 InGame→Login 转移中按序发生（同会话多信号天然合并）。
        /// 业务仍可各自订阅 <see cref="GameMessage.ServerForceLogout"/> 做界面跳转——与业务导航互补、互不替代。
        /// </summary>
        private void WireForcedLogoutHandling()
        {
            _forceLogoutSub = Event.Subscribe<int>(GameMessage.ServerForceLogout,
                code => RequestLogout($"server_force_logout:{code}"));
            _playerLogoutSub = Event.Subscribe(GameMessage.PlayerLogout,
                () => RequestLogout("player_logout"));
            Sdk.OnSessionInvalidated += OnSdkSessionInvalidated;
        }

        private void OnSdkSessionInvalidated(string reason)
            => RequestLogout($"sdk_session_invalidated:{reason}");

        /// <summary>登出请求统一入口：转交 AppFlow 主状态机。登录态 / 启动引导期收到的登出为 no-op。
        /// 登出属正常业务流，用普通日志（Warning 会污染「零告警」验收门禁）。</summary>
        private void RequestLogout(string reason)
        {
            Debug.Log($"[GameEntry] 收到登出请求 reason={reason}");
            if (_appFlow == null || !_appFlow.RequestLogout(reason))
                Debug.Log($"[GameEntry] 登出请求被忽略（当前不在登录会话中）reason={reason}");
        }

        /// <summary>先让业务释放账号态资源；无论业务是否异常，调用方都继续完成框架清理。</summary>
        private static void NotifyBusinessExit(string reason)
        {
            try
            {
                OnBusinessExit?.Invoke(reason);
            }
            catch (Exception ex)
            {
                Debug.LogError($"[GameEntry] 业务退出 OnBusinessExit 抛异常，继续清理框架身份 reason={reason}");
                Debug.LogException(ex);
            }
            finally
            {
                // 账号退出（ADR-008）：身份清除前驱动各模块收尾。引导等模块此钩子为空实现。
                Modules?.OnAccountExit();
                // 此时 SaveManager 仍指向旧账号目录：先异步触发已看版本落盘，再清运行态；
                // AppFlow 随后才调用 ClearLoggedInIdentity 切回 guest。
                // TODO(ADR-008 步骤3a)：下面这行红点收尾将迁入 RedDotModule.OnAccountExit 后删除。
                Framework.RedDot.RedDotAccountSession.End(RedDots);
            }
        }

        /// <summary>
        /// 登出 / 互踢时把 <see cref="BindLoggedInIdentity"/> 贯通的玩家身份逐一复位，避免残留身份错配到
        /// 下一个账号：存档切回 guest 目录、埋点 / 远配 / 崩溃归因清空用户维度。
        /// </summary>
        private void ClearLoggedInIdentity()
        {
            Framework.Save.SaveManager.Instance.ClearCurrentUser();
            Analytics?.SetUserId(string.Empty);
            RemoteConfig?.SetUserId(string.Empty);
            Telemetry.CrashReporter.SetUser(string.Empty);
        }

        // ── Manager 注册清单 ─────────────────────────────────────────────────

        /// <summary>
        /// Manager 注册项：类型 + 初始化时序硬依赖 + 装配动作。
        /// DependsOn 只声明「初始化时刻真实存在」的依赖（装配动作里会立即触达对方实例），
        /// 运行期才互调的软依赖不在此列——清单是时序契约，不是完整依赖图。
        /// </summary>
        public sealed class ManagerRegistration
        {
            internal ManagerRegistration(Type managerType, Type[] dependsOn, Action<GameEntry> install)
            {
                ManagerType = managerType;
                DependsOn = dependsOn ?? Array.Empty<Type>();
                Install = install;
            }

            /// <summary>Manager 具体类型。</summary>
            public Type ManagerType { get; }

            /// <summary>初始化前必须已就绪的 Manager 类型。</summary>
            public IReadOnlyList<Type> DependsOn { get; }

            /// <summary>构造 Manager、登记静态门面并完成装配期接线。</summary>
            internal Action<GameEntry> Install { get; }
        }

        /// <summary>
        /// Manager 注册清单（声明顺序即初始化顺序，关闭时逆序）。
        /// 新增 Manager 只在此登记一条：初始化循环、依赖校验与 EditMode 拓扑/完整性测试
        /// （GameEntryManagerManifestTests）都以本清单为唯一事实源，漏登记或顺序违规会被测试挡下。
        /// </summary>
        public static IReadOnlyList<ManagerRegistration> ManagerManifest { get; } = new[]
        {
            Reg<EventManager>(g => Event = g.AddComponent<EventManager>()),
            Reg<TimerManager>(g => Timer = g.AddComponent<TimerManager>()),
            Reg<ResourceManager>(g => Resource = g.AddComponent<ResourceManager>()),

            // SDK 管理器尽早就位：渠道实现由业务组合根注册（RegisterProvider），
            // 初始化时机由业务显式驱动（通常在 LaunchFlow 前 / HotfixEntry 内）。
            Reg<Framework.Sdk.SdkManager>(g => Sdk = g.AddComponent<Framework.Sdk.SdkManager>()),

            // 埋点管道紧随其后：LaunchFlow 的启动阶段指标依赖它上报。
            Reg<Framework.Analytics.AnalyticsManager>(
                g => Analytics = g.AddComponent<Framework.Analytics.AnalyticsManager>()),

            // 远程配置紧随埋点：磁盘缓存值启动早期即可读，拉取由 LaunchFlow 并行发起。
            Reg<Framework.RemoteConfig.RemoteConfigManager>(
                g => RemoteConfig = g.AddComponent<Framework.RemoteConfig.RemoteConfigManager>()),

            // UIBootstrap 随建随注：确保后续任何 OpenUIAsync 调用时 Canvas 层级已就绪。
            Reg<UIManager>(g =>
            {
                UI = g.AddComponent<UIManager>();
                UI.SetBootstrap(g._uiBootstrap);
            }),
            Reg<InputManager>(g =>
            {
                Input = g.AddComponent<InputManager>();
                Input.SetBootstrap(g._uiBootstrap);
            }),

            Reg<NetworkManager>(g =>
            {
                Network = g.AddComponent<NetworkManager>();
                g.ApplyNetworkLogSettings();
            }),
            Reg<TipManager>(g => Tips = g.AddComponent<TipManager>()),

            // 组合根注入：让网络层在断线重连后，经鉴权层静默重放登录握手恢复服务端会话身份，
            // 否则重连得到的匿名连接会导致快照/落子等业务请求被服务端静默丢弃（断线重连失效）。
            Reg<AuthManager>(g =>
            {
                Auth = g.AddComponent<AuthManager>();
                Network.SetReauthenticationProvider(() => Auth.ReauthenticateWithResultAsync());

                // 遥测上报签名凭据：逐次求值读当前会话，登录/登出/换号无需重新注入；
                // 未登录时无有效凭据即不签名（埋点/崩溃端点按未签名通道从严限流）。
                Framework.Http.TelemetryRequestSigner.SetCredentialsProvider(() =>
                    new Framework.Http.TelemetrySigningCredentials(
                        AuthSession.UserId, AuthSession.SessionToken));
            }, dependsOn: new[] { typeof(NetworkManager) }),

            Reg<ConfigManager>(g => RefData = g.AddComponent<ConfigManager>()),
            Reg<AudioManager>(g => Audio = g.AddComponent<AudioManager>()),
            Reg<SceneManager>(g => Scene = g.AddComponent<SceneManager>()),
            Reg<GameStageManager>(g => StageManager = g.AddComponent<GameStageManager>()),

            // 导航栈操作的是 GameStageManager.Instance，属初始化时序硬依赖。
            Reg<GameStageNavigationManager>(
                g => StageNavigation = g.AddComponent<GameStageNavigationManager>(),
                dependsOn: new[] { typeof(GameStageManager) }),

            Reg<HotUpdateManager>(g => HotUpdate = g.AddComponent<HotUpdateManager>()),
        };

        /// <summary>清单声明糖：类型参数即注册类型，杜绝「清单类型与实际构造类型不一致」的笔误。</summary>
        private static ManagerRegistration Reg<T>(Action<GameEntry> install, Type[] dependsOn = null)
            where T : FrameworkComponent
        {
            return new ManagerRegistration(typeof(T), dependsOn, install);
        }

        /// <summary>
        /// 按注册清单初始化全部 Manager，随后完成跨模块组合根接线。
        /// 依赖顺序违规立即抛出（装配错误属启动即炸），与 EditMode 拓扑测试同源双保险。
        /// </summary>
        private void InitializeManagers()
        {
            // 崩溃回捞最早挂接：后续任何 Manager 初始化抛出的未捕获异常也能落盘；
            // 积压记录的上报在全部 Manager 就绪后异步尝试（端点未配置时仅本地缓存）。
            Telemetry.CrashReporter.Install();

            var initialized = new HashSet<Type>();
            foreach (ManagerRegistration registration in ManagerManifest)
            {
                for (int i = 0; i < registration.DependsOn.Count; i++)
                {
                    Type dependency = registration.DependsOn[i];
                    if (!initialized.Contains(dependency))
                    {
                        throw new InvalidOperationException(
                            $"Manager 注册清单顺序错误：{registration.ManagerType.Name} 依赖 {dependency.Name}，但后者尚未初始化。");
                    }
                }
                registration.Install(this);
                initialized.Add(registration.ManagerType);
            }

            // ── 跨模块组合根接线（全部 Manager 就绪后；仍在 Awake 内，先于任何事件发布）──

            // 组合根注入：ErrorCenter 属 Kernel 层、不认识 Tips/Analytics（ADR-002），
            // 由此处注入 UI 呈现器并把限流后的错误上报转发给埋点管道。
            ErrorCenter.Shared.SetPresenter(new DefaultErrorPresenter());
            ErrorCenter.Shared.ErrorReported += decision =>
            {
                Analytics?.Track("server_error", new Dictionary<string, object>
                {
                    { "code", decision.Code },
                    { "reaction", decision.Reaction.ToString() },
                });
                // 服务端错误留面包屑：崩溃前的错误码链常是根因线索。
                Telemetry.CrashReporter.LeaveBreadcrumb($"error:{decision.Code} reaction={decision.Reaction}");
            };

            // 组合根编排：强制登出 / 玩家登出 / 渠道会话失效统一清鉴权凭据与跨模块身份。
            WireForcedLogoutHandling();

            // 上一次运行留下的崩溃记录：后台尝试上报（不阻塞启动，失败静默保留下次再试）。
            // 上报端点由后端自读（默认后端读 AppConfig.CrashReportUrl，原生后端走自身管道）。
            Telemetry.CrashReporter.TryUploadPendingAsync().Forget();

            // 内置调试命令（help/version/loglevel/perfhud 等安全项）；业务命令由业务侧自行注册。
            // GM 命令使用审计：每次执行尝试转发埋点 + 崩溃面包屑（命令常是复现路径的关键线索）。
            Framework.Diagnostics.BuiltinCommands.RegisterAll(Commands);
            Commands.Executed += record =>
            {
                Analytics?.Track("gm_command", new Dictionary<string, object>
                {
                    { "name", record.Name },
                    { "success", record.Success },
                });
                Telemetry.CrashReporter.LeaveBreadcrumb($"cmd:{record.Name} ok={record.Success}");
            };

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

            // 中间层模块帧末回调（ADR-008）：如红点在此做帧末合并结算。引导等模块此钩子为空实现。
            Modules?.BroadcastLateUpdate(dt);

            // TODO(ADR-008 步骤3a)：下面这段红点帧末结算将迁入 RedDotModule.OnLateUpdate 后删除。
            // 帧末统一结算红点：本帧内多个来源对同一子树的写入合并为一次聚合与 UI 通知，
            // 避免一帧内重复计算和多次刷新。目录初始化后自动开启合并模式；读接口仍按需即时结算。
            var redDots = RedDots;
            if (redDots != null && redDots.IsInitialized)
            {
                if (!redDots.IsFrameCoalescingEnabled) redDots.SetFrameCoalescing(true);
                redDots.FlushPending();
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

            // Timer / Save 等 Manager 尚可用时先让业务保存并释放账号态资源。
            // 应用退出不经 AppFlow 循环（保留持久会话供冷启动恢复），直接走业务退出通知。
            NotifyBusinessExit("application_quit");

            // 停止应用主状态机循环：登录 / 业务入口 / 等登出的 await 经取消令牌干净解绑。
            try { _appFlowCts?.Cancel(); } catch (Exception ex) { Debug.LogException(ex); }
            _appFlowCts?.Dispose();
            _appFlowCts = null;
            _appFlow?.Dispose();
            _appFlow = null;

            Application.lowMemory -= HandleLowMemory;

            // 释放登出编排订阅，避免组件关闭后回调仍被触发。
            if (Sdk != null)
                Sdk.OnSessionInvalidated -= OnSdkSessionInvalidated;
            _forceLogoutSub?.Dispose();
            _playerLogoutSub?.Dispose();
            _forceLogoutSub = null;
            _playerLogoutSub = null;

            // 先逆序拆中间层模块（引导/红点运行器由各模块 Dispose 释放），再拆其依赖的 L1 Target 目录。
            Modules?.DisposeAll();
            UiTargets?.Clear();

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
