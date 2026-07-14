using System;
using System.Collections.Generic;
using System.Threading;
using Cysharp.Threading.Tasks;
using Framework.Core.Auth;
using Framework.Foundation;

namespace Framework.Core
{
    /// <summary>应用级粗粒度状态。启动引导（LaunchFlow：热更/资源）只跑一次，属状态机之前的引导段，不进状态机。</summary>
    public enum AppFlowState
    {
        /// <summary>未登录：登录界面活动中（含静默会话恢复 / 自动访客登录）。</summary>
        Login,
        /// <summary>已登录：身份已贯通、业务已接管。</summary>
        InGame,
    }

    /// <summary>应用级状态转换触发器。</summary>
    public enum AppFlowTrigger
    {
        /// <summary>登录成功且身份已贯通，进入业务。</summary>
        LoginSucceeded,
        /// <summary>收到登出请求（玩家主动 / 服务端互踢 / 渠道会话失效），执行拆卸回登录。</summary>
        LogoutRequested,
    }

    /// <summary>
    /// AppFlow 的注入点集合。除 <see cref="RunLoginAsync"/> 必填外均可空；
    /// 所有钩子的异常都会被 AppFlow 隔离并送 <see cref="Error"/>，不会中断主循环、不会使状态机 Faulted。
    /// </summary>
    public sealed class AppFlowHooks
    {
        /// <summary>必填。交互式登录活动：失败应在内部重试，仅在成功（或取消）时返回。</summary>
        public Func<CancellationToken, UniTask<LoginResult>> RunLoginAsync { get; set; }

        /// <summary>登录成功后、进入业务前，把玩家身份贯通到各子系统（存档/埋点/远配/崩溃归因）。</summary>
        public Action<LoginResult> BindIdentity { get; set; }

        /// <summary>业务入口：进主场景/开主界面。作为 InGame 的 Enter 执行——入口未完成时收到的登出会
        /// 在转换门上排队，待入口完成后立即拆卸（登出请求后置合并，不打断半初始化的入口）。</summary>
        public Func<LoginResult, CancellationToken, UniTask> EnterBusinessAsync { get; set; }

        /// <summary>业务退出：保存账号数据、取消账号级定时器、关业务 UI。参数为登出原因。</summary>
        public Action<string> ExitBusiness { get; set; }

        /// <summary>清空鉴权凭据（内存 + 持久化）。参数为登出原因。</summary>
        public Action<string> LogoutAuth { get; set; }

        /// <summary>复位跨模块玩家身份（存档回 guest / 埋点 / 远配 / 崩溃归因清空）。</summary>
        public Action ClearIdentity { get; set; }

        /// <summary>关键节点信息日志出口。</summary>
        public Action<string> Info { get; set; }

        /// <summary>钩子异常与内部故障的诊断出口；异常参数可能为 null（纯告警）。</summary>
        public Action<string, Exception> Error { get; set; }
    }

    /// <summary>
    /// 应用主流程状态机：Login ⇄ InGame，骑在通用 <see cref="AsyncStateMachine{TState,TTrigger}"/> 上。
    /// 纯逻辑、全靠注入（不触 Unity API），可在 EditMode 脱离 Play 单测。
    /// <para>
    /// <b>职责边界</b>：登录（活动）→ 身份贯通 → 进业务（InGame.Enter）→ 挂起等登出 → 拆卸
    /// （InGame.Exit：业务退出 → 鉴权登出 → 清身份）→ 回登录。三个登出源统一改调
    /// <see cref="RequestLogout"/>——它只记原因 + 唤醒主循环，不在调用栈里执行拆卸；
    /// 真正的拆卸只在 InGame→Login 转移中按序发生，且经一次 Yield 与信号发布者的调用栈解耦。
    /// </para>
    /// <para>
    /// <b>信号语义</b>：同一会话内多次登出请求天然合并（首个原因生效）；登录态收到登出为 no-op
    /// （返回 false）；业务入口 await 期间收到的登出在转换门上后置排队，入口完成后立即拆卸。
    /// 断线重连不产生登出请求，与本状态机无关。
    /// </para>
    /// <para>
    /// <b>鲁棒性</b>：全部钩子异常被隔离上报，状态转换不会因业务异常失败，故各状态无需配置补偿
    /// （OnRollback）；状态机 Faulted 只可能来自框架自身缺陷，主循环含防御性 Recover 兜底。
    /// </para>
    /// </summary>
    public sealed class AppFlow : IDisposable
    {
        private readonly AppFlowHooks _hooks;
        private readonly AsyncStateMachine<AppFlowState, AppFlowTrigger> _machine;
        private readonly object _signalSync = new object();

        private UniTaskCompletionSource<string> _logoutSignal;   // 会话内武装；_signalSync 保护
        private LoginResult _currentLogin;
        private string _logoutReason;
        private int _started;

        public AppFlow(AppFlowHooks hooks)
        {
            if (hooks == null) throw new ArgumentNullException(nameof(hooks));
            if (hooks.RunLoginAsync == null)
                throw new ArgumentException("必须注入 RunLoginAsync 登录活动。", nameof(hooks));
            _hooks = hooks;

            _machine = AsyncStateMachine<AppFlowState, AppFlowTrigger>.Build(AppFlowState.Login, b =>
            {
                b.ObserverErrorSink = ex => ReportError("状态变化观察者异常，已隔离", ex);
                b.State(AppFlowState.Login)
                    .Permit(AppFlowTrigger.LoginSucceeded, AppFlowState.InGame);
                b.State(AppFlowState.InGame)
                    .OnEnterAsync((context, token) => RunEnterBusinessIsolatedAsync(token))
                    .OnExit(_ => RunTeardownIsolated())
                    .Permit(AppFlowTrigger.LogoutRequested, AppFlowState.Login);
            });
            _machine.StateChanged += (source, target) => StateChanged?.Invoke(source, target);
        }

        /// <summary>当前应用状态；InGame.Enter（业务入口）执行期间仍读到 Login，提交后才是 InGame。</summary>
        public AppFlowState CurrentState => _machine.CurrentState;

        /// <summary>状态提交后触发，参数为 (source, target)。订阅者异常被隔离并送 Error 出口。</summary>
        public event Action<AppFlowState, AppFlowState> StateChanged;

        /// <summary>状态转换审计历史快照（时间升序），用于诊断登录/登出链路。</summary>
        public IReadOnlyList<StateTransitionRecord<AppFlowState, AppFlowTrigger>> GetHistorySnapshot()
            => _machine.GetHistorySnapshot();

        /// <summary>
        /// 主循环：登录活动 → 身份贯通 → 转 InGame（业务入口）→ 等登出 → 转 Login（拆卸）→ 循环。
        /// 只允许驱动一次；<paramref name="cancellationToken"/>（应用退出）取消时静默收束，
        /// application_quit 的业务退出由宿主在退出路径直接调用，不经本循环。
        /// </summary>
        public async UniTask RunAsync(CancellationToken cancellationToken)
        {
            if (Interlocked.Exchange(ref _started, 1) != 0)
                throw new InvalidOperationException("AppFlow.RunAsync 只允许驱动一次。");

            try
            {
                while (!cancellationToken.IsCancellationRequested)
                {
                    // 防御兜底：钩子异常全被隔离，Faulted 只可能来自框架缺陷；恢复回 Login 保持应用可用。
                    if (_machine.IsFaulted)
                    {
                        ReportError("应用状态机 Faulted，防御性恢复回 Login", _machine.LastFailure);
                        await _machine.RecoverAsync(AppFlowState.Login, cancellationToken);
                        continue;
                    }

                    // ── Login 态活动：交互式登录（失败由登录流程内部重试，仅成功返回）──
                    LoginResult login = await _hooks.RunLoginAsync(cancellationToken);
                    if (!login.Success)
                    {
                        ReportError("登录活动返回失败结果，停留登录态重试", null);
                        continue;
                    }
                    _currentLogin = login;
                    try { _hooks.BindIdentity?.Invoke(login); }
                    catch (Exception ex) { ReportError("身份贯通钩子异常，已隔离", ex); }

                    // 从此刻起登出请求有效：业务入口 await 期间到达的登出会被记住并后置处理。
                    ArmLogoutSignal();
                    _hooks.Info?.Invoke($"登录成功，进入 InGame userId={login.UserId}");
                    await _machine.FireAsync(AppFlowTrigger.LoginSucceeded, cancellationToken);

                    // ── InGame 态活动：挂起直到登出请求 ──
                    string reason = await WaitForLogoutAsync(cancellationToken);
                    // 先解除武装：拆卸期间到达的登出请求为 no-op（本来就在登出）。
                    lock (_signalSync) _logoutSignal = null;
                    // 与登出信号发布者的调用栈解耦：拆卸不在事件派发/回调栈深处执行。
                    await UniTask.Yield();

                    _logoutReason = reason;
                    _hooks.Info?.Invoke($"执行登出拆卸 reason={reason}");
                    await _machine.FireAsync(AppFlowTrigger.LogoutRequested, cancellationToken);
                    _logoutReason = null;
                    _currentLogin = default;
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // 应用退出：正常收束。
            }
        }

        /// <summary>
        /// 请求登出（服务端互踢 / 玩家主动 / 渠道会话失效统一入口）。只记录原因并唤醒主循环，
        /// 不在调用栈里执行拆卸。返回 false 表示当前不在登录会话中（登录态 / 拆卸中），请求被忽略；
        /// 同一会话内的重复请求合并为首个原因并返回 true。线程安全。
        /// </summary>
        public bool RequestLogout(string reason)
        {
            if (string.IsNullOrEmpty(reason)) reason = "unspecified";
            UniTaskCompletionSource<string> signal;
            lock (_signalSync) signal = _logoutSignal;
            if (signal == null) return false;
            signal.TrySetResult(reason);
            return true;
        }

        public void Dispose()
        {
            _machine.Dispose();
        }

        private void ArmLogoutSignal()
        {
            lock (_signalSync) _logoutSignal = new UniTaskCompletionSource<string>();
        }

        private async UniTask<string> WaitForLogoutAsync(CancellationToken cancellationToken)
        {
            UniTaskCompletionSource<string> signal;
            lock (_signalSync) signal = _logoutSignal;
            return await signal.Task.AttachExternalCancellation(cancellationToken);
        }

        /// <summary>InGame.Enter：业务入口。异常（含入口自身的取消）一律隔离，保证转换提交、主循环不死。</summary>
        private async UniTask RunEnterBusinessIsolatedAsync(CancellationToken cancellationToken)
        {
            if (_hooks.EnterBusinessAsync == null) return;
            try
            {
                await _hooks.EnterBusinessAsync(_currentLogin, cancellationToken);
            }
            catch (Exception ex)
            {
                ReportError("业务入口钩子异常，已隔离不影响框架生命周期", ex);
            }
        }

        /// <summary>InGame.Exit：拆卸按序执行「业务退出 → 鉴权登出 → 清身份」，逐段隔离异常保证清理走完。</summary>
        private void RunTeardownIsolated()
        {
            string reason = _logoutReason ?? "unspecified";
            try { _hooks.ExitBusiness?.Invoke(reason); }
            catch (Exception ex) { ReportError($"业务退出钩子异常，继续框架清理 reason={reason}", ex); }
            try { _hooks.LogoutAuth?.Invoke(reason); }
            catch (Exception ex) { ReportError($"鉴权登出异常，继续身份清理 reason={reason}", ex); }
            try { _hooks.ClearIdentity?.Invoke(); }
            catch (Exception ex) { ReportError("玩家身份复位异常", ex); }
        }

        private void ReportError(string message, Exception error)
        {
            try { _hooks.Error?.Invoke(message, error); }
            catch { /* 诊断出口自身的异常没有更下游的去处，只能吞。 */ }
        }
    }
}
