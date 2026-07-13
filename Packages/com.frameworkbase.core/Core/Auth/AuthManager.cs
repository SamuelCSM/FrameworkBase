using System;
using System.Threading;
using Cysharp.Threading.Tasks;

namespace Framework.Core.Auth
{
    /// <summary>
    /// 登录流程状态机（无服务端版先行）。
    /// 管理状态迁移、错误码映射、弹窗策略与重试入口。
    /// </summary>
    public class AuthManager : FrameworkComponent<AuthManager>
    {
        private IAuthBackend _backend;
        private IAuthPopupPresenter _popupPresenter;
        private CancellationTokenSource _loginCts;
        private LoginRequestContext? _lastContext;

        /// <summary>当前登录状态。</summary>
        public LoginFlowState State { get; private set; } = LoginFlowState.Idle;

        /// <summary>最近一次错误码。</summary>
        public string LastErrorCode { get; private set; } = string.Empty;

        /// <summary>状态变化事件。</summary>
        public event Action<LoginStateSnapshot> OnStateChanged;

        public override void OnInit()
        {
            _backend = CreateDefaultBackend();
            _popupPresenter = new LogOnlyAuthPopupPresenter();
            SetState(LoginFlowState.Idle, "init", string.Empty);
            GameLog.Log($"[AuthManager] 初始化完成 backend={_backend.GetType().Name}");
        }

        /// <summary>
        /// 创建默认登录后端：配置了 <c>AppConfig.AuthServerUrl</c> 且启用网络登录时用框架参考
        /// <see cref="HttpAuthBackend"/>（中性 HTTP 契约、无业务协议依赖），否则回退本地 <see cref="MockAuthBackend"/>。
        /// 业务若有自有登录协议，可经 <see cref="SetBackend"/> 注入替换，框架鉴权层不依赖任何具体实现。
        /// </summary>
        private static IAuthBackend CreateDefaultBackend()
        {
            AppConfigAsset config = AppConfig.Load();
            if (config != null && config.UseNetworkLogin && !string.IsNullOrEmpty(config.AuthServerUrl))
            {
                GameLog.Log($"[AuthManager] 使用 HTTP 登录后端: {config.AuthServerUrl}");
                return new HttpAuthBackend(config.AuthServerUrl);
            }

            GameLog.Log("[AuthManager] 使用 Mock 登录后端（未配置 AuthServerUrl 或已关闭网络登录）");
            return new MockAuthBackend();
        }

        public override void OnShutdown()
        {
            CancelLogin();
            _backend = null;
            _popupPresenter = null;
        }

        /// <summary>替换后端实现（后续接入真实服务端）。</summary>
        public void SetBackend(IAuthBackend backend)
        {
            if (backend == null)
            {
                GameLog.Warning("[AuthManager] SetBackend ignored: backend is null");
                return;
            }
            _backend = backend;
        }

        /// <summary>替换弹窗展示器（接入业务 UI 后使用）。</summary>
        public void SetPopupPresenter(IAuthPopupPresenter popupPresenter)
        {
            if (popupPresenter == null)
            {
                GameLog.Warning("[AuthManager] SetPopupPresenter ignored: presenter is null");
                return;
            }
            _popupPresenter = popupPresenter;
        }

        /// <summary>游客登录。</summary>
        public UniTask<LoginResult> LoginGuestAsync(int timeoutMs = 3000)
        {
            var context = new LoginRequestContext
            {
                Mode = LoginMode.Guest,
                Account = string.Empty,
                Password = string.Empty,
                TimeoutMs = timeoutMs
            };
            return ExecuteLoginAsync(context);
        }

        /// <summary>账号登录。</summary>
        public UniTask<LoginResult> LoginAccountAsync(string account, string password, int timeoutMs = 5000)
        {
            var context = new LoginRequestContext
            {
                Mode = LoginMode.Account,
                Account = account ?? string.Empty,
                Password = password ?? string.Empty,
                TimeoutMs = timeoutMs
            };
            return ExecuteLoginAsync(context);
        }

        /// <summary>取消当前登录。</summary>
        public void CancelLogin()
        {
            if (_loginCts == null) return;
            _loginCts.Cancel();
            _loginCts.Dispose();
            _loginCts = null;
            SetState(LoginFlowState.Cancelled, "cancel_requested", TelemetryErrorCodes.Auth.LoginCancelled);
        }

        /// <summary>基于最近一次上下文重试。</summary>
        public UniTask<LoginResult> RetryLastLoginAsync()
        {
            if (!_lastContext.HasValue)
            {
                GameLog.Warning("[AuthManager] RetryLastLoginAsync ignored: no last context");
                return UniTask.FromResult(LoginResult.Fail(TelemetryErrorCodes.Auth.Unknown, "no last context"));
            }

            return ExecuteLoginAsync(_lastContext.Value);
        }

        /// <summary>
        /// 断线重连后的静默重新鉴权：优先用会话令牌重放登录握手（服务端凭令牌重绑身份，
        /// 不再重放明文密码——密码在登录成功时已从内存丢弃），让服务端重新绑定会话身份并
        /// 交还对局控制权。与 <see cref="RetryLastLoginAsync"/> 不同，本方法刻意
        /// 不驱动登录状态机、不弹窗、不发布 PlayerLoginSuccess 登录成功事件，因此不会把
        /// 正在对局中的玩家重新拉回登录后主流程（EnterMainFlow），仅悄悄恢复网络会话。
        /// 供 <see cref="NetworkManager.SetReauthenticator"/> 注入，在传输层重连成功后调用。
        /// 令牌失效（服务器重启/过期）时返回 false，由上层走「引导重新登录」路径。
        /// </summary>
        /// <returns>会话是否已恢复；未登录或无历史凭据（如登录前掉线）时视为无需恢复，返回 true。</returns>
        public async UniTask<bool> ReauthenticateAsync()
        {
            // 从未登录或无历史登录上下文：传输恢复即可，无需重放登录握手。
            if (_backend == null || !_lastContext.HasValue || !AuthSession.IsLoggedIn)
            {
                return true;
            }

            LoginRequestContext context = _lastContext.Value;

            // 优先走令牌重绑：清空凭据字段，只携带令牌。
            // 无令牌（Mock 后端 / 旧服务端）时保持原上下文重放——游客模式靠设备 ID 仍可恢复；
            // 账号模式密码已被丢弃，会被服务端拒绝并走引导重新登录，不会静默使用明文密码。
            bool triedToken = !string.IsNullOrEmpty(AuthSession.SessionToken);
            LoginRequestContext attemptContext = context;
            if (triedToken)
            {
                attemptContext.Account = string.Empty;
                attemptContext.Password = string.Empty;
                attemptContext.SessionToken = AuthSession.SessionToken;
            }

            using (var timeoutController = new TimeoutController(context.TimeoutMs))
            {
                try
                {
                    LoginResult result = await _backend.LoginAsync(attemptContext, timeoutController.Token);
                    if (result.Success)
                    {
                        // 刷新会话令牌，保持与服务端最新签发一致，并同步刷新持久化会话。
                        AuthSession.Apply(result);
                        AuthSessionStore.Save(AuthSessionStore.BuildRecord(context.Mode, result, context.Account));
                        GameLog.Log("[AuthManager] 断线重连重新鉴权成功");
                        return true;
                    }

                    // 令牌被服务端拒绝（过期，或服务端重启导致内存令牌丢失——开发期高频）。
                    // 访客模式可靠稳定的 DeviceId 无缝恢复（服务端 GetOrCreateGuest 幂等、永远成功），
                    // 故降级重登一次，避免访客用户因令牌失效被误判断线/顶号而踢下线。
                    // 账号模式密码已从内存丢弃、无法静默重放，只能返回失败引导重新登录（安全边界不破）。
                    if (triedToken && context.Mode == LoginMode.Guest)
                    {
                        LoginRequestContext guestContext = context;
                        guestContext.SessionToken = string.Empty;
                        guestContext.Password = string.Empty;
                        LoginResult guestResult = await _backend.LoginAsync(guestContext, timeoutController.Token);
                        if (guestResult.Success)
                        {
                            AuthSession.Apply(guestResult);
                            AuthSessionStore.Save(AuthSessionStore.BuildRecord(context.Mode, guestResult, context.Account));
                            GameLog.Log("[AuthManager] 令牌失效，已回退访客身份重新鉴权成功");
                            return true;
                        }
                    }

                    GameLog.Warning($"[AuthManager] 断线重连重新鉴权被拒: code={result.ErrorCode}, msg={result.ErrorMessage}");
                    return false;
                }
                catch (OperationCanceledException)
                {
                    GameLog.Warning("[AuthManager] 断线重连重新鉴权超时/取消");
                    return false;
                }
                catch (Exception ex)
                {
                    GameLog.Warning($"[AuthManager] 断线重连重新鉴权异常: {ex.Message}");
                    return false;
                }
            }
        }

        /// <summary>
        /// 冷启动会话恢复：读取上次登录持久化的会话，静默重登以跳过登录界面。
        /// <para>
        /// 与 <see cref="ReauthenticateAsync"/>（对局中传输重连、不驱动状态机）不同，本方法是
        /// 冷启动首次进入登录阶段的「记住登录」：成功后驱动状态机到 <see cref="LoginFlowState.Success"/>
        /// 并发布 <see cref="GameMessage.PlayerLoginSuccess"/>，语义等价一次正常登录成功。
        /// </para>
        /// <para>
        /// 同时重建 <c>_lastContext</c>，修复「冷启动后从未走过登录、断线重连缺历史上下文无法重鉴权」的隐患。
        /// 账号模式若无令牌（明文密码从不持久化）不做静默恢复，返回失败引导重新登录（安全边界不破）。
        /// </para>
        /// </summary>
        /// <returns>恢复成功返回带 UserId/Token 的成功结果；无持久会话/令牌被拒/网络异常返回失败（应回登录界面）。</returns>
        public async UniTask<LoginResult> TryRestorePersistedSessionAsync()
        {
            if (_backend == null)
                return LoginResult.Fail(TelemetryErrorCodes.Auth.Unknown, "backend not configured");

            if (!AuthSessionStore.TryLoad(out AuthSessionStore.AuthSessionRecord record))
                return LoginResult.Fail(TelemetryErrorCodes.Auth.Unknown, "no persisted session");

            var mode = (LoginMode)record.Mode;
            bool hasToken = !string.IsNullOrEmpty(record.SessionToken);

            // 账号模式且无令牌：密码已丢弃、无法静默恢复，必须回登录界面重新输入。
            if (!hasToken && mode == LoginMode.Account)
                return LoginResult.Fail(TelemetryErrorCodes.Auth.TokenExpired, "account session requires re-login");

            var context = new LoginRequestContext
            {
                Mode = mode,
                Account = record.Account ?? string.Empty,
                Password = string.Empty,
                SessionToken = record.SessionToken ?? string.Empty,
                TimeoutMs = mode == LoginMode.Guest ? 3000 : 5000,
            };
            // 重建历史上下文：让冷启动后的断线重连(ReauthenticateAsync)同样有据可依。
            _lastContext = context;

            using (var timeoutController = new TimeoutController(context.TimeoutMs))
            {
                try
                {
                    LoginResult result = await _backend.LoginAsync(context, timeoutController.Token);
                    if (result.Success)
                    {
                        AuthSession.Apply(result);
                        AuthSessionStore.Save(AuthSessionStore.BuildRecord(mode, result, context.Account));
                        SetState(LoginFlowState.Success, "session_restored", string.Empty);
                        GameEntry.Event?.Publish(GameMessage.PlayerLoginSuccess);
                        GameLog.Log("[AuthManager] 冷启动会话恢复成功");
                        return result;
                    }

                    // 令牌被服务端拒绝：清持久化会话与内存态，回登录界面（不再静默）。
                    AuthSessionStore.Clear();
                    AuthSession.Clear();
                    GameLog.Warning($"[AuthManager] 冷启动会话恢复被拒: code={result.ErrorCode}, msg={result.ErrorMessage}");
                    return LoginResult.Fail(TelemetryErrorCodes.Auth.TokenExpired, result.ErrorMessage);
                }
                catch (Exception ex)
                {
                    // 网络异常/超时：不清持久化（可能只是暂时断网），本次回登录界面，下次冷启动可再试。
                    GameLog.Warning($"[AuthManager] 冷启动会话恢复异常: {ex.Message}");
                    return LoginResult.Fail(TelemetryErrorCodes.Auth.NetworkOffline, ex.Message);
                }
            }
        }

        private async UniTask<LoginResult> ExecuteLoginAsync(LoginRequestContext context)
        {
            if (_backend == null)
            {
                return LoginResult.Fail(TelemetryErrorCodes.Auth.Unknown, "backend not configured");
            }

            // 同一时刻只允许一个登录流程，避免并发 race。
            if (State == LoginFlowState.Preparing ||
                State == LoginFlowState.Connecting ||
                State == LoginFlowState.Authenticating)
            {
                return LoginResult.Fail(TelemetryErrorCodes.Auth.LoginInProgress, "login in progress");
            }

            _lastContext = context;
            LastErrorCode = string.Empty;
            RecreateLoginCts();

            try
            {
                SetState(LoginFlowState.Preparing, "prepare_request", string.Empty);
                await UniTask.Yield(PlayerLoopTiming.Update, _loginCts.Token);

                SetState(LoginFlowState.Connecting, "connect_phase", string.Empty);
                await UniTask.Delay(100, cancellationToken: _loginCts.Token);

                SetState(LoginFlowState.Authenticating, "auth_phase", string.Empty);
                var timeoutController = new TimeoutController(context.TimeoutMs);
                using (timeoutController)
                using (CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(_loginCts.Token, timeoutController.Token))
                {
                    try
                    {
                        LoginResult result = await _backend.LoginAsync(context, linked.Token);
                        if (result.Success)
                        {
                            AuthSession.Apply(result);
                            // 会话持久化：供跨重启静默重登（冷启动恢复）。明文密码从不持久化。
                            AuthSessionStore.Save(AuthSessionStore.BuildRecord(context.Mode, result, context.Account));

                            // 登录成功即从内存丢弃明文密码：后续断线重连改用会话令牌重绑
                            // （ReauthenticateAsync），不再重放密码。保留账号名与模式供遥测/展示。
                            // 失败路径不清——弹窗「重试」需要原始凭据重放。
                            context.Password = string.Empty;
                            _lastContext = context;

                            SetState(LoginFlowState.Success, "auth_success", string.Empty);
                            GameEntry.Event?.Publish(GameMessage.PlayerLoginSuccess);
                            return result;
                        }

                        return await HandleLoginFailureAsync(result.ErrorCode, result.ErrorMessage);
                    }
                    catch (Exception ex)
                    {
                        bool isTimeout = timeoutController.IsTimeoutReached && !_loginCts.IsCancellationRequested;
                        string errorCode = AuthErrorMapper.MapException(ex, isTimeout);
                        return await HandleLoginFailureAsync(errorCode, ex.Message);
                    }
                }
            }
            catch (Exception ex)
            {
                string errorCode = AuthErrorMapper.MapException(ex, isTimeout: false);
                return await HandleLoginFailureAsync(errorCode, ex.Message);
            }
            finally
            {
                DisposeLoginCts();
            }
        }

        private async UniTask<LoginResult> HandleLoginFailureAsync(string errorCode, string errorMessage)
        {
            LastErrorCode = string.IsNullOrEmpty(errorCode) ? TelemetryErrorCodes.Auth.Unknown : errorCode;
            SetState(LoginFlowState.Failed, "auth_failed", LastErrorCode);
            GameEntry.Event?.Publish(GameMessage.PlayerLoginFailed, LastErrorCode);

            var decision = AuthPopupPolicy.Resolve(LastErrorCode, errorMessage);
            if (_popupPresenter != null)
            {
                await _popupPresenter.PresentAsync(
                    decision,
                    retryHandler: async () => { await RetryLastLoginAsync(); },
                    exitHandler: () => GameEntry.Event?.Publish(GameMessage.PlayerLogout));
            }

            return LoginResult.Fail(LastErrorCode, errorMessage);
        }

        private void SetState(LoginFlowState state, string reason, string errorCode)
        {
            State = state;
            var snapshot = new LoginStateSnapshot
            {
                State = state,
                Reason = reason ?? string.Empty,
                ErrorCode = errorCode ?? string.Empty,
                AtUtc = DateTime.UtcNow
            };
            OnStateChanged?.Invoke(snapshot);

            // 登录状态迁移留面包屑：崩溃报告可回溯玩家当时处于登录哪一步、错在哪个码。
            Telemetry.CrashReporter.LeaveBreadcrumb($"auth:{state} {snapshot.Reason} {snapshot.ErrorCode}");
            GameLog.Log($"[AuthFlow] state={state}, reason={snapshot.Reason}, code={snapshot.ErrorCode}");
        }

        private void RecreateLoginCts()
        {
            DisposeLoginCts();
            _loginCts = new CancellationTokenSource();
        }

        private void DisposeLoginCts()
        {
            if (_loginCts == null) return;
            _loginCts.Dispose();
            _loginCts = null;
        }

        /// <summary>
        /// 小型超时控制器：避免把超时逻辑散落在主流程里。
        /// </summary>
        private sealed class TimeoutController : IDisposable
        {
            private readonly CancellationTokenSource _cts;
            public CancellationToken Token => _cts.Token;
            public bool IsTimeoutReached => _cts.IsCancellationRequested;

            public TimeoutController(int timeoutMs)
            {
                _cts = new CancellationTokenSource();
                if (timeoutMs > 0)
                    _cts.CancelAfter(timeoutMs);
            }

            public void Dispose()
            {
                _cts.Dispose();
            }
        }
    }
}
