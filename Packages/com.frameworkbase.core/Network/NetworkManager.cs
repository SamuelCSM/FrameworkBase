using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using Cysharp.Threading.Tasks;
using Framework.Core;
using Framework.Network;

namespace Framework
{
    /// <summary>
    /// 网络管理器
    ///
    /// 完整的断线重连机制：
    ///   1. 心跳包：定时发送，服务端任意回包都算"存活"信号
    ///   2. 心跳超时：超过阈值未收到服务端数据，主动断开并触发重连
    ///   3. 指数退避重连：1 → 2 → 5 → 10 → 30s
    ///   4. 回前台检测：切后台连接断开后，回到前台自动重连
    ///   5. 手动重连：调用 ReconnectAsync() 可在 UI 层主动触发
    ///
    /// 新增事件（UI 层订阅）：
    ///   OnReconnecting(attempt, maxAttempts, waitSeconds) — 每次重连尝试前
    ///   OnReconnectSucceeded                              — 重连成功
    ///   OnReconnectFailed                                 — 达到最大次数，放弃
    ///   OnWaitingStart                                    — 请求等待超时，显示转圈
    ///   OnWaitingEnd                                      — 所有请求等待结束，隐藏转圈
    ///   OnRequestTimeout                                  — 请求超时提示
    /// </summary>
    public class NetworkManager : FrameworkComponent
    {
        private TcpClient _client;
        private MessageDispatcher _dispatcher;
        private NetworkRequestTracker _requestTracker;

        /// <summary>断线待发队列（opt-in 幂等请求重连后补发）+ 其时钟（累计秒）。</summary>
        private readonly OfflineRequestQueue _offlineQueue = new OfflineRequestQueue();
        private double _offlineQueueClock;
        private NetworkMessageTypeRegistry _messageTypeRegistry;

        // ── 连接状态事件跨线程编组 ────────────────────────────────────────────
        // TcpClient 的 OnConnected/OnDisconnected/OnError 可能在收/发后台线程触发，
        // 这里仅入队，由主线程 OnUpdate 统一排空处理，确保下游 UI 事件、请求清理、
        // 重连流程全部在主线程执行，杜绝从非主线程调用 Unity API。
        private enum ConnectionEventType { Connected, Disconnected, Error }

        /// <summary>连接状态事件，跨线程从 TcpClient 回调传递到主线程。</summary>
        private readonly struct ConnectionEvent
        {
            public readonly ConnectionEventType Type;
            public readonly string Error;
            public ConnectionEvent(ConnectionEventType type, string error = null)
            {
                Type = type;
                Error = error;
            }
        }

        private readonly ConcurrentQueue<ConnectionEvent> _connectionEvents =
            new ConcurrentQueue<ConnectionEvent>();

        /// <summary>消息分发前拦截委托，初始化后复用，避免 Update 中创建委托。</summary>
        private Func<byte, byte, ushort, byte[], bool> _messageInterceptHandler;

        /// <summary>请求响应完成委托，初始化后复用，避免 Update 中创建 lambda。</summary>
        private Action<ushort, byte[]> _seqResponseHandler;

        /// <summary>协议接收日志委托，初始化后复用，避免 Update 中创建委托。</summary>
        private Action<byte, byte, ushort, byte[]> _protocolReceiveLogHandler;

        /// <summary>全局错误码拦截器。返回 true 表示已处理，错误响应不会继续下发业务。</summary>
        private Func<int, bool> _globalErrorInterceptor;

        /// <summary>是否启用协议收发日志，默认开启方便联调定位网络问题。</summary>
        private bool _enableProtocolLog = true;

        /// <summary>是否打印心跳协议日志，默认关闭，避免低信息量高频协议刷屏。</summary>
        private bool _enableHeartbeatProtocolLog = false;

        /// <summary>协议日志屏蔽表，Key 为完整协议 ID，用于按 mainId/subId 过滤指定协议。</summary>
        private readonly HashSet<ushort> _ignoredProtocolLogMessageIds = new HashSet<ushort>();

        // ── 心跳 ─────────────────────────────────────────────────────────────
        private float _heartbeatInterval    = 30f;
        private float _heartbeatTimer       = 0f;
        private bool  _enableHeartbeat      = true;

        /// <summary>框架心跳固定使用的主协议号。</summary>
        private const byte HeartbeatMainId = MessageModule.System;

        /// <summary>框架心跳固定使用的子协议号，请求和响应同号，通过方向区分。</summary>
        private const byte HeartbeatSubId = 1;

        /// <summary>心跳协议体内的自增序号，用于服务端回显后定位请求/响应对应关系。</summary>
        private int _heartbeatSequenceId = 0;

        // ── 心跳超时检测 ──────────────────────────────────────────────────────
        // 用 volatile flag 安全跨线程传递"收到数据"信号
        private volatile bool _dataReceivedFlag  = false;
        private float         _timeSinceLastData = 0f;
        // 默认 = 心跳间隔 × 2.5，即连续 2.5 个心跳周期没有回应则判定超时
        private float _heartbeatTimeoutSeconds = 75f;
        private bool  _heartbeatTimeoutEnabled = true;

        // ── 重连 ─────────────────────────────────────────────────────────────
        private bool    _enableAutoReconnect    = true;
        private int     _maxReconnectAttempts   = 5;
        private int     _currentReconnectAttempt = 0;
        private float[] _reconnectIntervals     = { 1f, 2f, 5f, 10f, 30f };
        private bool    _isReconnecting         = false;
        private string  _lastHost;
        private int     _lastPort;

        /// <summary>
        /// 重连后的应用层重新鉴权钩子（由组合根注入，框架网络层不依赖具体鉴权实现）。
        /// 传输层重连成功后，服务端会把新建立的连接视为未登录的匿名会话，必须重放登录
        /// 握手让其重新绑定会话身份并交还对局控制权，否则后续业务请求（快照 / 落子等）
        /// 会因身份缺失被服务端静默丢弃。返回 true 表示会话已恢复，可对外宣告"重连成功"；
        /// 返回 false 表示鉴权失败，按本次重连失败处理并继续退避重试。未注入（null）时跳过。
        /// </summary>
        private Func<UniTask<bool>> _reauthenticator;

        /// <summary>
        /// 心跳消息工厂（由业务层组合根注入）：参数为 (clientTimeMs, sequenceId)，返回待发送的心跳协议消息。
        /// 框架网络层只负责定时与序号自增，不依赖具体心跳协议类型。未注入时跳过心跳发送（并告警一次）。
        /// </summary>
        private Func<long, int, INetMessage> _heartbeatMessageFactory;

        /// <summary>心跳工厂缺失告警去重标记，避免每个心跳周期刷屏。</summary>
        private bool _heartbeatFactoryMissingWarned;

        /// <summary>
        /// 心跳响应解析器（由业务层组合根注入）：入参为响应 payload，返回其中的服务端毫秒时间戳。
        /// 注入后框架自动把每次心跳往返喂给 <see cref="ServerTime"/> 完成服务器校时；未注入时跳过校时。
        /// </summary>
        private Func<byte[], long> _heartbeatResponseParser;

        /// <summary>最近一次心跳请求发出时的本地毫秒时间戳；0 表示当前没有等待配对的心跳（防迟到/重复响应污染采样）。</summary>
        private long _lastHeartbeatSentLocalMs;

        // ── 属性 ─────────────────────────────────────────────────────────────
        public bool IsConnected    => _client != null && _client.IsConnected;
        public bool IsReconnecting => _isReconnecting;

        // ── 事件 ─────────────────────────────────────────────────────────────
        /// <summary>首次连接成功 / 重连成功后触发</summary>
        public event Action OnConnected;

        /// <summary>连接断开时触发（主动断开和被动断开都会触发）</summary>
        public event Action OnDisconnected;

        /// <summary>
        /// 每次重连尝试开始时触发。
        /// 参数：当前尝试次数, 最大次数, 本轮等待秒数
        /// </summary>
        public event Action<int, int, float> OnReconnecting;

        /// <summary>重连成功触发</summary>
        public event Action OnReconnectSucceeded;

        /// <summary>达到最大重连次数仍失败时触发</summary>
        public event Action OnReconnectFailed;

        /// <summary>网络层错误</summary>
        public event Action<string> OnError;

        /// <summary>存在请求等待超过配置延迟，UI 层应显示等待转圈。</summary>
        public event Action OnWaitingStart;

        /// <summary>所有请求等待结束，UI 层应隐藏等待转圈。</summary>
        public event Action OnWaitingEnd;

        /// <summary>请求超时，UI 层应提示用户。参数：提示文案。</summary>
        public event Action<string> OnRequestTimeout;

        // ── 生命周期 ─────────────────────────────────────────────────────────

        public override void OnInit()
        {
            _dispatcher = new MessageDispatcher();
            _messageTypeRegistry = new NetworkMessageTypeRegistry();
            // 协议类型改为惰性登记：首次 Subscribe<T>/RequestAsync<TResp> 时以具体类型登记，避免启动期全程序集反射扫描（IL2CPP 不安全）。
            _messageInterceptHandler = ShouldConsumeBeforeDispatch;
            _seqResponseHandler = CompleteSeqResponse;
            _protocolReceiveLogHandler = LogReceivedProtocol;
            _requestTracker = new NetworkRequestTracker
            {
                OnShowWaiting = () => OnWaitingStart?.Invoke(),
                OnHideWaiting = () => OnWaitingEnd?.Invoke(),
                OnShowTimeoutTip = msg => OnRequestTimeout?.Invoke(msg),
            };
            GameLog.Log("[NetworkManager] 初始化完成");
        }

        public override void OnUpdate(float deltaTime)
        {
            // 先在主线程排空后台线程投递的连接状态事件，再处理消息与请求
            DrainConnectionEvents();

            _dispatcher?.ProcessMessageQueue(_messageInterceptHandler, _seqResponseHandler, _protocolReceiveLogHandler);
            _requestTracker?.Update(deltaTime);

            // 断线待发队列 TTL 驱动：等太久没能补发的请求按失败收尾
            _offlineQueueClock += deltaTime;
            _offlineQueue.Update(_offlineQueueClock);

            if (!IsConnected) return;

            // ── 接收数据 flag 消费（由接收线程写入，主线程消费）──────────
            if (_dataReceivedFlag)
            {
                _dataReceivedFlag  = false;
                _timeSinceLastData = 0f;
            }

            // ── 心跳超时检测 ──────────────────────────────────────────────
            if (_heartbeatTimeoutEnabled)
            {
                _timeSinceLastData += deltaTime;
                if (_timeSinceLastData > _heartbeatTimeoutSeconds)
                {
                    GameLog.Warning($"[NetworkManager] 心跳超时 ({_heartbeatTimeoutSeconds:0}s 未收到数据)，主动断开重连");
                    _timeSinceLastData = 0f;
                    // 直接断 TCP，不走公开 Disconnect()（公开方法会关闭自动重连）
                    _client.Disconnect();
                    return;
                }
            }

            // ── 定时发送心跳包 ────────────────────────────────────────────
            if (_enableHeartbeat)
            {
                _heartbeatTimer += deltaTime;
                if (_heartbeatTimer >= _heartbeatInterval)
                {
                    _heartbeatTimer = 0f;
                    SendHeartbeat();
                }
            }
        }

        /// <summary>
        /// 回到前台时，如果连接已断开则自动触发重连。
        /// 手机切后台 → TCP 被系统切断 → 回前台自动修复。
        /// </summary>
        public override void OnApplicationFocus(bool hasFocus)
        {
            if (hasFocus
                && !IsConnected
                && !_isReconnecting
                && _enableAutoReconnect
                && !string.IsNullOrEmpty(_lastHost))
            {
                GameLog.Log("[NetworkManager] 回到前台，检测到断线，自动触发重连...");
                TryReconnectAsync().Forget();
            }
        }

        // ── 连接 / 断开 ──────────────────────────────────────────────────────

        /// <summary>连接服务器</summary>
        public async UniTask ConnectAsync(string host, int port)
        {
            if (IsConnected)
            {
                GameLog.Warning("[NetworkManager] 已连接，跳过");
                return;
            }

            _lastHost = host;
            _lastPort = port;
            _currentReconnectAttempt = 0;
            _enableAutoReconnect     = true;

            await ConnectInternalAsync(host, port);
        }

        /// <summary>
        /// 手动触发重连（用于 UI 失败按钮回调）。
        /// 重置计数器，重新跑一轮指数退避。
        /// </summary>
        public async UniTask ReconnectAsync()
        {
            if (string.IsNullOrEmpty(_lastHost))
            {
                GameLog.Warning("[NetworkManager] 无法重连：尚未连接过任何服务器");
                return;
            }
            if (_isReconnecting)
            {
                GameLog.Warning("[NetworkManager] 重连已在进行中");
                return;
            }

            _currentReconnectAttempt = 0;
            _enableAutoReconnect     = true;
            await TryReconnectAsync();
        }

        /// <summary>主动断开（关闭自动重连）。断线待发队列一并按失败收尾——不会再有重连补发它们。</summary>
        public void Disconnect()
        {
            _enableAutoReconnect     = false;
            _isReconnecting          = false;
            _currentReconnectAttempt = 0;
            _offlineQueue.FailAll();
            _client?.Disconnect();
        }

        // ── 消息收发 ─────────────────────────────────────────────────────────

        /// <summary>
        /// 发送一条不需要匹配响应的通知消息（seqId = 0）。
        /// 适用场景：投降、聊天发送、操作确认等服务端不回响应或由推送通道广播的消息。
        /// </summary>
        public void Notify<T>(T message) where T : class, INetMessage
        {
            SendMessageInternal(message, 0);
        }

        /// <summary>
        /// 发送请求并等待对应类型的响应。通过包头 SeqId 精确匹配请求与响应。
        /// <para>
        /// 未连接时的行为：默认直接返回 null；config 开启
        /// <see cref="NetworkRequestConfig.QueueWhileDisconnected"/>（仅限幂等请求）则挂入
        /// 断线待发队列，重连 + 重鉴权成功后按 FIFO 补发，超过 QueueTtlMs 未发出按失败收尾。
        /// </para>
        /// </summary>
        public UniTask<TResp> RequestAsync<TReq, TResp>(TReq request, NetworkRequestConfig config = null)
            where TReq : class, INetMessage
            where TResp : class, INetMessage, new()
        {
            if (config == null) config = NetworkRequestConfig.Default;

            if (!IsConnected)
            {
                if (config.QueueWhileDisconnected)
                    return EnqueueOfflineRequest<TReq, TResp>(request, config);

                GameLog.Error("[NetworkManager] 未连接，无法发送请求");
                return UniTask.FromResult<TResp>(null);
            }

            return RequestConnectedAsync<TReq, TResp>(request, config);
        }

        /// <summary>已连接状态下的请求-响应核心流程（注册 pending → 发送 → 等待配对）。</summary>
        private UniTask<TResp> RequestConnectedAsync<TReq, TResp>(TReq request, NetworkRequestConfig config)
            where TReq : class, INetMessage
            where TResp : class, INetMessage, new()
        {
            var tcs = new UniTaskCompletionSource<TResp>();
            _messageTypeRegistry?.Register<TResp>();

            ushort seqId = _requestTracker.Register(
                payload =>
                {
                    try
                    {
                        TResp response = ProtobufUtil.Deserialize<TResp>(payload);
                        tcs.TrySetResult(response);
                    }
                    catch (Exception ex)
                    {
                        GameLog.Error($"[NetworkManager] 反序列化响应失败: {typeof(TResp).Name}, 错误={ex.Message}");
                        tcs.TrySetResult(null);
                    }
                },
                () =>
                {
                    if (config.ShowTimeoutTip)
                    {
                        string msg = config.TimeoutMessage ?? "网络请求超时，请检查网络后重试";
                        _requestTracker.OnShowTimeoutTip?.Invoke(msg);
                    }
                    tcs.TrySetResult(null);
                },
                config,
                () => tcs.TrySetResult(null));

            // 发送失败时立即放弃该 pending，避免调用方硬等到超时（默认 15s）才拿到 null。
            if (!SendMessageInternal(request, seqId))
            {
                _requestTracker.Cancel(seqId);
                tcs.TrySetResult(null);
            }

            return tcs.Task;
        }

        /// <summary>
        /// 把断线期间的 opt-in 请求挂入待发队列，返回其完成器任务。
        /// 补发只给一次机会：重连补发瞬间又断线时直接按失败收尾，不二次入队（避免无限徘徊）。
        /// </summary>
        private UniTask<TResp> EnqueueOfflineRequest<TReq, TResp>(TReq request, NetworkRequestConfig config)
            where TReq : class, INetMessage
            where TResp : class, INetMessage, new()
        {
            var tcs = new UniTaskCompletionSource<TResp>();

            bool queued = _offlineQueue.TryEnqueue(
                send: () => ForwardQueuedRequestAsync(request, config, tcs).Forget(),
                fail: () => tcs.TrySetResult(null),
                ttlSeconds: config.QueueTtlMs / 1000.0,
                now: _offlineQueueClock);

            if (!queued)
            {
                GameLog.Warning($"[NetworkManager] 断线待发队列已满（{_offlineQueue.MaxItems}），请求直接失败: {typeof(TReq).Name}");
                return UniTask.FromResult<TResp>(null);
            }

            GameLog.Log($"[NetworkManager] 未连接，请求已入队等待重连补发: {typeof(TReq).Name}（队列 {_offlineQueue.Count} 条）");
            return tcs.Task;
        }

        /// <summary>补发一条排队请求并把结果转交给原调用方的完成器。</summary>
        private async UniTaskVoid ForwardQueuedRequestAsync<TReq, TResp>(
            TReq request, NetworkRequestConfig config, UniTaskCompletionSource<TResp> tcs)
            where TReq : class, INetMessage
            where TResp : class, INetMessage, new()
        {
            if (!IsConnected)
            {
                tcs.TrySetResult(null); // 补发窗口又断线：只给一次机会
                return;
            }

            TResp response = await RequestConnectedAsync<TReq, TResp>(request, config);
            tcs.TrySetResult(response);
        }

        /// <summary>
        /// 发送请求并等待响应（单泛型版本）。
        /// 请求类型实现 <see cref="IRequest{TResp}"/> 时可省略显式泛型参数，由编译器自动推断。
        /// </summary>
        /// <typeparam name="TResp">响应消息类型（自动推断）。</typeparam>
        /// <param name="request">实现了 IRequest&lt;TResp&gt; 的请求消息。</param>
        /// <param name="config">请求配置。为 null 时使用默认配置。</param>
        /// <returns>响应消息实例；超时或取消时返回 null。</returns>
        public UniTask<TResp> RequestAsync<TResp>(IRequest<TResp> request, NetworkRequestConfig config = null)
            where TResp : class, INetMessage, new()
        {
            return RequestAsync<IRequest<TResp>, TResp>(request, config);
        }

        /// <summary>
        /// 设置全局错误码拦截器。
        /// 当响应实现了 IResponse 且 ResultCode 非零时，拦截器被调用。
        /// 返回 true 表示已处理（业务订阅不触发，RequestAsync 收到 null），false 继续正常返回。
        /// 典型用途：登录过期(401)弹重登窗口、频率限制(429)显示提示。
        /// </summary>
        /// <param name="interceptor">拦截器函数，参数为 ResultCode。</param>
        public void SetGlobalErrorInterceptor(Func<int, bool> interceptor)
        {
            _globalErrorInterceptor = interceptor;
        }

        /// <summary>
        /// 订阅类型化消息，协议号从消息类型自身读取。
        /// </summary>
        /// <typeparam name="T">反序列化后的协议消息类型。</typeparam>
        /// <param name="handler">类型化消息处理器。</param>
        /// <param name="priority">订阅优先级，值越大越先触发。</param>
        /// <returns>用于释放本次订阅的句柄。</returns>
        public MessageSubscription Subscribe<T>(Action<T> handler, int priority = 0)
            where T : class, INetMessage, new()
        {
            _messageTypeRegistry?.Register<T>();
            return _dispatcher.Subscribe(handler, priority);
        }

        // ── 配置接口 ─────────────────────────────────────────────────────────

        public void SetHeartbeatInterval(float interval)
        {
            if (interval <= 0) return;
            _heartbeatInterval        = interval;
            _heartbeatTimeoutSeconds  = interval * 2.5f;
        }

        public void EnableHeartbeat(bool enable)        => _enableHeartbeat = enable;
        public void EnableHeartbeatTimeout(bool enable) => _heartbeatTimeoutEnabled = enable;
        public void EnableAutoReconnect(bool enable)    => _enableAutoReconnect = enable;

        /// <summary>
        /// 启用或关闭控制台协议收发日志。
        /// </summary>
        /// <param name="enable">true 表示打印 SEND/RECV 协议日志，false 表示关闭。</param>
        public void EnableProtocolLog(bool enable) => _enableProtocolLog = enable;

        /// <summary>
        /// 启用或关闭心跳协议日志。默认关闭，避免心跳请求/响应刷屏。
        /// </summary>
        /// <param name="enable">true 表示打印心跳协议日志，false 表示屏蔽。</param>
        public void EnableHeartbeatProtocolLog(bool enable) => _enableHeartbeatProtocolLog = enable;

        /// <summary>
        /// 屏蔽指定协议号的收发日志。
        /// </summary>
        /// <param name="mainId">主消息 ID。</param>
        /// <param name="subId">子消息 ID。</param>
        public void IgnoreProtocolLog(byte mainId, byte subId)
        {
            _ignoredProtocolLogMessageIds.Add(MessagePacket.CombineMessageId(mainId, subId));
        }

        /// <summary>
        /// 恢复指定协议号的收发日志。
        /// </summary>
        /// <param name="mainId">主消息 ID。</param>
        /// <param name="subId">子消息 ID。</param>
        public void UnignoreProtocolLog(byte mainId, byte subId)
        {
            _ignoredProtocolLogMessageIds.Remove(MessagePacket.CombineMessageId(mainId, subId));
        }

        /// <summary>
        /// 清空协议日志屏蔽表，不影响心跳协议日志的独立开关。
        /// </summary>
        public void ClearIgnoredProtocolLogs()
        {
            _ignoredProtocolLogMessageIds.Clear();
        }

        /// <summary>
        /// 注入重连后的应用层重新鉴权钩子。由组合根（GameEntry）在鉴权管理器就绪后调用，
        /// 使框架网络层无需依赖具体鉴权实现，即可在传输层重连成功后重放登录握手恢复会话身份。
        /// </summary>
        /// <param name="reauthenticator">重新鉴权委托；返回会话是否恢复成功。传 null 可清除。</param>
        public void SetReauthenticator(Func<UniTask<bool>> reauthenticator)
        {
            _reauthenticator = reauthenticator;
        }

        /// <summary>
        /// 注入心跳消息工厂。由业务层组合根（HotfixEntry）在启动时调用，使框架网络层无需依赖
        /// 具体心跳协议类型即可定时发送保活心跳。参数：(clientTimeMs, sequenceId)。
        /// </summary>
        /// <param name="factory">心跳消息工厂；返回的消息须为 System 通道协议。传 null 可清除。</param>
        public void SetHeartbeatProvider(Func<long, int, INetMessage> factory)
        {
            _heartbeatMessageFactory = factory;
        }

        /// <summary>
        /// 注入心跳响应解析器，开启服务器校时。入参为心跳响应的 payload 字节，返回其中携带的
        /// 服务端毫秒时间戳（无效时返回 0）。注入后框架在每次心跳往返时自动更新 <see cref="ServerTime"/>。
        /// </summary>
        /// <param name="parser">心跳响应解析器；传 null 关闭校时。</param>
        public void SetHeartbeatResponseParser(Func<byte[], long> parser)
        {
            _heartbeatResponseParser = parser;
        }

        public void SetMaxReconnectAttempts(int max)
        {
            if (max >= 0) _maxReconnectAttempts = max;
        }

        public void SetReconnectIntervals(float[] intervals)
        {
            if (intervals != null && intervals.Length > 0)
                _reconnectIntervals = intervals;
        }

        /// <summary>
        /// 依据"服务端重连宽限窗口"自动编排重连退避：保证所有重连尝试都落在窗口内
        /// （并预留一段时间给重连后的重新登录 + 拉取快照往返），从而让窗口内的任意一次
        /// 成功重连都能交还会话控制权。这样客户端退避不再与服务端窗口各自硬编码而悄悄漂移。
        /// 早期密集重试（1/2/3s…）以快速命中常见的瞬时网络抖动恢复。
        /// </summary>
        /// <param name="windowSeconds">服务端重连宽限窗口（秒），通常取自 battle_rule_general.ReconnectWindowSec。</param>
        /// <param name="reserveSeconds">
        /// 末次尝试前为"重新登录 + 快照恢复"往返预留的余量（秒）。传 &lt;=0 时按窗口的 20%（且不低于 3 秒）自适应。
        /// </param>
        public void ConfigureReconnectWithinWindow(float windowSeconds, float reserveSeconds = -1f)
        {
            if (windowSeconds <= 0f)
            {
                GameLog.Warning("[NetworkManager] ConfigureReconnectWithinWindow 收到非法窗口值，沿用默认退避");
                return;
            }

            // 末次尝试前预留登录+快照往返余量；预算 = 窗口 - 余量，至少留 1 秒可重试。
            float reserve = reserveSeconds > 0f ? reserveSeconds : Math.Max(3f, windowSeconds * 0.2f);
            float budget = Math.Max(1f, windowSeconds - reserve);

            // 渐增但有上限的退避步长（秒）：前段密集快速重试，后段拉长避免无谓频繁重连。
            float[] ladder = { 1f, 2f, 3f, 5f, 8f, 8f, 10f };

            var intervals = new List<float>(ladder.Length);
            float elapsed = 0f;
            foreach (float step in ladder)
            {
                // 下一次尝试相对断线的发生时刻 = 已累计等待 + 本次步长；超出预算且已有尝试则停止。
                if (elapsed + step > budget && intervals.Count > 0)
                    break;

                intervals.Add(step);
                elapsed += step;
            }

            // 极小窗口兜底：至少保留一次尝试。
            if (intervals.Count == 0)
                intervals.Add(1f);

            _reconnectIntervals = intervals.ToArray();
            _maxReconnectAttempts = intervals.Count;

            GameLog.Log($"[NetworkManager] 重连退避按窗口编排: 窗口={windowSeconds:0}s, 预算={budget:0}s, " +
                       $"次数={_maxReconnectAttempts}, 间隔(s)=[{string.Join(",", _reconnectIntervals)}]");
        }

        public void SetConnectTimeout(int seconds)
        {
            if (_client != null) _client.ConnectTimeoutSeconds = seconds;
        }

        // ── 内部实现 ─────────────────────────────────────────────────────────

        /// <summary>
        /// 内部发送消息（携带 seqId）。
        /// </summary>
        /// <returns>发送成功返回 true；未连接或序列化/发送异常返回 false，供请求方据此立即失败收尾。</returns>
        private bool SendMessageInternal<T>(T message, ushort seqId) where T : class, INetMessage
        {
            if (!IsConnected)
            {
                GameLog.Error("[NetworkManager] 未连接，无法发送消息");
                return false;
            }

            try
            {
                byte[] payload = ProtobufUtil.Serialize(message);
                byte[] packet = MessagePacket.Pack(message, payload, seqId);
                _client.Send(packet);

                if (ShouldLogProtocol(message.GetMainId(), message.GetSubId()))
                {
                    NetworkProtocolLogger.LogSend(message, packet.Length, seqId);
                }

                return true;
            }
            catch (Exception ex)
            {
                GameLog.Error($"[NetworkManager] 发送失败: {ex.Message}");
                OnError?.Invoke($"发送失败: {ex.Message}");
                return false;
            }
        }

        private async UniTask ConnectInternalAsync(string host, int port)
        {
            try
            {
                if (_client == null)
                {
                    _client = new TcpClient();
                    // TLS 从 AppConfig 装配一次，重连沿用同一配置（同一 TcpClient 实例）。
                    var appCfg = Core.AppConfig.Load();
                    if (appCfg != null && appCfg.UseTls)
                    {
                        _client.Tls = new TlsClientOptions
                        {
                            Enabled = true,
                            TargetHost = string.IsNullOrEmpty(appCfg.TlsServerName)
                                ? "clientbase-gs"
                                : appCfg.TlsServerName,
                            CertSha256 = appCfg.TlsCertSha256 ?? string.Empty,
                        };
                    }

                    _client.OnConnected    += OnClientConnected;
                    _client.OnDisconnected += OnClientDisconnected;
                    _client.OnReceive      += OnClientReceive;
                    _client.OnError        += OnClientError;
                }

                await _client.ConnectAsync(host, port);
            }
            catch (Exception ex)
            {
                GameLog.Error($"[NetworkManager] 连接失败: {ex.Message}");
                OnError?.Invoke($"连接失败: {ex.Message}");

                if (_enableAutoReconnect && !_isReconnecting)
                    await TryReconnectAsync();
            }
        }

        // ── TcpClient 回调：可能在后台线程触发，仅入队，处理推迟到主线程 ──────
        private void OnClientConnected()
            => _connectionEvents.Enqueue(new ConnectionEvent(ConnectionEventType.Connected));

        private void OnClientDisconnected()
            => _connectionEvents.Enqueue(new ConnectionEvent(ConnectionEventType.Disconnected));

        private void OnClientError(string error)
            => _connectionEvents.Enqueue(new ConnectionEvent(ConnectionEventType.Error, error));

        /// <summary>主线程排空连接状态事件队列，按投递顺序处理。</summary>
        private void DrainConnectionEvents()
        {
            while (_connectionEvents.TryDequeue(out ConnectionEvent ev))
            {
                switch (ev.Type)
                {
                    case ConnectionEventType.Connected:    HandleConnected();      break;
                    case ConnectionEventType.Disconnected: HandleDisconnected();   break;
                    case ConnectionEventType.Error:        HandleError(ev.Error);  break;
                }
            }
        }

        private void HandleConnected()
        {
            // 传输层已连接：无论首连还是重连都重置心跳计时，避免刚连上就误判超时。
            _heartbeatTimer    = 0f;
            _timeSinceLastData = 0f;

            // 重连流程中：传输层连上 ≠ 会话已恢复。此时不清 _isReconnecting、不对外广播 OnConnected，
            // 统一交由 TryReconnectAsync 在"重新鉴权成功"后宣告重连成功（OnReconnectSucceeded）。
            // 否则会出现两类竞态问题：
            //   1. 业务在会话尚未恢复的匿名连接上抢跑发请求，被服务端静默丢弃；
            //   2. 鉴权往返期间若再次断线，因 _isReconnecting 已被提前清零，HandleDisconnected 会
            //      并发启动第二轮 TryReconnectAsync，破坏退避状态机。
            if (_isReconnecting)
            {
                GameLog.Log("[NetworkManager] 传输层已连接（重连中，等待重新鉴权恢复会话）");
                return;
            }

            _currentReconnectAttempt = 0;
            GameLog.Log("[NetworkManager] 连接成功");
            OnConnected?.Invoke();
        }

        private void HandleDisconnected()
        {
            GameLog.Log("[NetworkManager] 连接断开");
            _requestTracker?.CancelAll();
            OnDisconnected?.Invoke();

            if (_enableAutoReconnect && !_isReconnecting)
                TryReconnectAsync().Forget();
        }

        private void OnClientReceive(byte[] packet)
        {
            _dataReceivedFlag = true;

            if (MessagePacket.Unpack(packet, out byte mainId, out byte subId, out ushort seqId, out byte[] payload))
            {
                // 统一入队到主线程处理，保证 Subscribe 和 RequestAsync 的时序一致
                _dispatcher.EnqueueMessage(mainId, subId, payload, seqId);
            }
            else
            {
                GameLog.Error("[NetworkManager] 消息包解析失败");
            }
        }

        /// <summary>
        /// 打印收到的协议日志。该方法在主线程消息处理前调用，便于安全地展开协议字段。
        /// </summary>
        /// <param name="mainId">主消息 ID。</param>
        /// <param name="subId">子消息 ID。</param>
        /// <param name="seqId">请求序列号。</param>
        /// <param name="payload">协议消息体字节数据。</param>
        private void LogReceivedProtocol(byte mainId, byte subId, ushort seqId, byte[] payload)
        {
            if (!ShouldLogProtocol(mainId, subId))
            {
                return;
            }

            NetworkProtocolLogger.LogReceive(_messageTypeRegistry, mainId, subId, seqId, payload);
        }

        /// <summary>
        /// 在协议进入业务分发或请求回调前处理框架层协议和统一错误码。
        /// </summary>
        /// <param name="mainId">主消息 ID。</param>
        /// <param name="subId">子消息 ID。</param>
        /// <param name="seqId">请求序列号。</param>
        /// <param name="payload">协议消息体字节数据。</param>
        /// <returns>消息已被框架层消费时返回 true。</returns>
        private bool ShouldConsumeBeforeDispatch(byte mainId, byte subId, ushort seqId, byte[] payload)
        {
            if (IsHeartbeatMessage(mainId, subId))
            {
                // 收方向的心跳一定是响应（请求只有上行）：先取服务端时间做校时采样，再消费掉不下发业务
                HandleHeartbeatTimeSample(payload);
                return true;
            }

            // seqId>0 的响应必须对应一个仍在飞的请求；若对应 pending 已不存在（请求已超时/取消/被处理完毕），
            // 说明这是迟到或重复响应，直接消费丢弃，避免它绕过 RequestAsync 单方面进入 Subscribe 多播刷新业务缓存，
            // 造成"调用方已按超时返回 null、缓存却被迟到响应更新"的状态分叉。
            if (seqId > 0 && _requestTracker != null && !_requestTracker.HasPending(seqId))
            {
                return true;
            }

            return TryInterceptNetworkError(mainId, subId, seqId, payload);
        }

        /// <summary>
        /// 用一次心跳往返更新服务器校时。解析器未注入或当前没有等待配对的心跳时跳过；
        /// 解析器是业务注入代码，任何异常都记录后跳过本次采样，不影响心跳保活本身。
        /// </summary>
        /// <param name="payload">心跳响应的消息体字节。</param>
        private void HandleHeartbeatTimeSample(byte[] payload)
        {
            if (_heartbeatResponseParser == null || _lastHeartbeatSentLocalMs <= 0)
            {
                return;
            }

            long sentLocalMs = _lastHeartbeatSentLocalMs;
            _lastHeartbeatSentLocalMs = 0; // 一次发送只配对一次采样，迟到/重复响应不再计入

            try
            {
                long serverTimeMs = _heartbeatResponseParser(payload);
                if (serverTimeMs > 0)
                {
                    ServerTime.AddSample(serverTimeMs, sentLocalMs, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
                }
            }
            catch (Exception ex)
            {
                GameLog.Warning($"[NetworkManager] 心跳响应解析失败，跳过本次校时采样: {ex.Message}");
            }
        }

        /// <summary>
        /// 判断协议是否为框架心跳协议，心跳只用于保活，不下发业务分发器。
        /// </summary>
        /// <param name="mainId">主消息 ID。</param>
        /// <param name="subId">子消息 ID。</param>
        /// <returns>属于当前心跳请求或响应协议时返回 true。</returns>
        private bool IsHeartbeatMessage(byte mainId, byte subId)
        {
            return mainId == HeartbeatMainId && subId == HeartbeatSubId;
        }

        /// <summary>
        /// 在协议进入业务分发或请求回调前执行统一错误码拦截。
        /// </summary>
        /// <param name="mainId">主消息 ID。</param>
        /// <param name="subId">子消息 ID。</param>
        /// <param name="seqId">请求序列号。</param>
        /// <param name="payload">协议消息体字节数据。</param>
        /// <returns>消息已被全局拦截器消费时返回 true。</returns>
        private bool TryInterceptNetworkError(byte mainId, byte subId, ushort seqId, byte[] payload)
        {
            if (_globalErrorInterceptor == null || _messageTypeRegistry == null)
            {
                return false;
            }

            IResponse response;
            try
            {
                if (!_messageTypeRegistry.TryParseResponse(mainId, subId, payload, out response))
                {
                    return false;
                }
            }
            catch (Exception ex)
            {
                GameLog.Error($"[NetworkManager] 响应错误码解析失败: MainId={mainId} SubId={subId} SeqId={seqId}, 错误={ex.Message}");
                return false;
            }

            if (response == null || response.ResultCode <= 0)
            {
                return false;
            }

            if (!_globalErrorInterceptor(response.ResultCode))
            {
                return false;
            }

            if (seqId > 0)
            {
                _requestTracker?.TryMarkIntercepted(seqId);
            }

            GameLog.Warning($"[NetworkManager] 全局错误码已拦截: MainId={mainId} SubId={subId} SeqId={seqId} ResultCode={response.ResultCode}");
            return true;
        }

        /// <summary>
        /// 完成带 seqId 的请求响应。
        /// </summary>
        /// <param name="seqId">请求序列号。</param>
        /// <param name="payload">响应消息体字节数据。</param>
        private void CompleteSeqResponse(ushort seqId, byte[] payload)
        {
            _requestTracker?.TryComplete(seqId, payload);
        }

        private void HandleError(string error)
        {
            GameLog.Error($"[NetworkManager] 网络错误: {error}");
            OnError?.Invoke(error);
        }

        /// <summary>
        /// 判断指定协议是否应该打印收发日志。
        /// </summary>
        /// <param name="mainId">主消息 ID。</param>
        /// <param name="subId">子消息 ID。</param>
        /// <returns>需要打印时返回 true。</returns>
        private bool ShouldLogProtocol(byte mainId, byte subId)
        {
            if (!_enableProtocolLog)
            {
                return false;
            }

            if (!_enableHeartbeatProtocolLog && IsHeartbeatMessage(mainId, subId))
            {
                return false;
            }

            ushort messageId = MessagePacket.CombineMessageId(mainId, subId);
            return !_ignoredProtocolLogMessageIds.Contains(messageId);
        }

        private void SendHeartbeat()
        {
            if (_heartbeatMessageFactory == null)
            {
                if (!_heartbeatFactoryMissingWarned)
                {
                    GameLog.Warning("[NetworkManager] 未注入心跳消息工厂，跳过心跳发送（请在业务层调用 SetHeartbeatProvider）");
                    _heartbeatFactoryMissingWarned = true;
                }
                return;
            }

            try
            {
                if (_heartbeatSequenceId == int.MaxValue)
                {
                    _heartbeatSequenceId = 0;
                }

                long clientTime = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                INetMessage request = _heartbeatMessageFactory(clientTime, ++_heartbeatSequenceId);
                _lastHeartbeatSentLocalMs = clientTime; // 供响应到达时做服务器校时采样配对

                byte[] payload = ProtobufUtil.Serialize(request);
                byte[] packet = MessagePacket.Pack(request, payload);
                _client.Send(packet);

                if (ShouldLogProtocol(request.GetMainId(), request.GetSubId()))
                {
                    NetworkProtocolLogger.LogSend(request, packet.Length, 0);
                }
            }
            catch (Exception ex)
            {
                GameLog.Error($"[NetworkManager] 心跳发送失败: {ex.Message}");
            }
        }

        /// <summary>
        /// 指数退避重连循环。
        /// 每次尝试前触发 OnReconnecting 事件（含等待时长），供 UI 显示倒计时。
        /// </summary>
        private async UniTask TryReconnectAsync()
        {
            if (_isReconnecting) return;
            _isReconnecting = true;

            while (_currentReconnectAttempt < _maxReconnectAttempts)
            {
                _currentReconnectAttempt++;
                int   idx      = Math.Min(_currentReconnectAttempt - 1, _reconnectIntervals.Length - 1);
                float waitSecs = _reconnectIntervals[idx];

                GameLog.Log($"[NetworkManager] 重连中 ({_currentReconnectAttempt}/{_maxReconnectAttempts})，{waitSecs}s 后重试...");

                // 通知 UI：当前是第几次尝试、还有多久
                OnReconnecting?.Invoke(_currentReconnectAttempt, _maxReconnectAttempts, waitSecs);

                await UniTask.Delay(TimeSpan.FromSeconds(waitSecs));

                try
                {
                    await _client.ConnectAsync(_lastHost, _lastPort);

                    // 传输层已恢复，但服务端把新连接视为匿名会话；必须先重放登录握手恢复
                    // 鉴权身份（重新绑定会话、交还对局控制权），鉴权成功后才算真正"重连成功"。
                    // 鉴权失败时主动断开这条未鉴权连接，让下一轮退避重新建连 + 登录。
                    if (!await TryReauthenticateAsync())
                    {
                        GameLog.Warning($"[NetworkManager] 第 {_currentReconnectAttempt} 次重连传输已恢复但重新鉴权失败，断开后继续重试");
                        _client.Disconnect();
                        continue;
                    }

                    // 连接 + 鉴权均成功
                    GameLog.Log("[NetworkManager] 重连成功");
                    _isReconnecting = false;

                    // 会话已恢复：先补发断线期间排队的 opt-in 请求，再对外宣告重连成功
                    _offlineQueue.FlushAll();
                    OnReconnectSucceeded?.Invoke();
                    return;
                }
                catch (Exception ex)
                {
                    GameLog.Warning($"[NetworkManager] 第 {_currentReconnectAttempt} 次重连失败: {ex.Message}");
                }
            }

            // 全部次数用尽：连接不再恢复，排队请求全部按失败收尾
            _isReconnecting = false;
            _offlineQueue.FailAll();
            GameLog.Error($"[NetworkManager] 达到最大重连次数 ({_maxReconnectAttempts})，放弃重连");
            OnReconnectFailed?.Invoke();
            OnError?.Invoke("网络连接失败，请检查网络后手动重连");
        }

        /// <summary>
        /// 调用注入的重新鉴权钩子并吞掉其异常，统一转换为成功 / 失败布尔值，
        /// 避免鉴权实现抛出的异常中断重连退避循环。未注入钩子时视为无需鉴权，直接成功。
        /// </summary>
        /// <returns>会话是否已恢复（或无需恢复）。</returns>
        private async UniTask<bool> TryReauthenticateAsync()
        {
            if (_reauthenticator == null)
            {
                return true;
            }

            try
            {
                return await _reauthenticator();
            }
            catch (Exception ex)
            {
                GameLog.Warning($"[NetworkManager] 重连重新鉴权异常: {ex.Message}");
                return false;
            }
        }

        public override void OnShutdown()
        {
            Disconnect();
            _requestTracker?.CancelAll();
            _requestTracker = null;
            _dispatcher?.ClearAllHandlers();
            _messageTypeRegistry?.Clear();
            _client     = null;
            _dispatcher = null;
            _messageTypeRegistry = null;
            _protocolReceiveLogHandler = null;
            GameLog.Log("[NetworkManager] 已关闭");
        }
    }
}
