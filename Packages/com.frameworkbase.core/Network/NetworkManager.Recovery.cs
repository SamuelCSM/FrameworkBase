using System;
using System.Threading;
using Cysharp.Threading.Tasks;
using Framework.Core;
using Framework.Network;

namespace Framework
{
    /// <summary>
    /// <see cref="NetworkManager"/> 的「连接恢复」分部（连通性/生命周期/前台探活/指数退避重连/重鉴权）。
    /// <para>
    /// 与主分部同属一个类型：这簇状态（_isReconnecting/_lastHost/连接代际等）被生命周期与重连逻辑交叉读写，
    /// 是不可再分的一致性域，其「决策」已下沉到 <see cref="Framework.Network.NetworkLifecyclePolicy"/>；
    /// 剩下的是与共享 TcpClient / 连接事件编组 / 心跳 / 离线队列 / 事件强耦合的「执行」胶水。
    /// 故按分部类拆文件组织，而非抽独立对象（那需要一个宽 host 接口＝有间接无解耦）。
    /// 见 NetworkReconnectPlayModeTests 的 PlayMode 特征化回归网。
    /// </para>
    /// </summary>
    public partial class NetworkManager
    {
        // ── 应用生命周期与网络切换恢复 ──────────────────────────────────────
        private INetworkConnectivityProvider _connectivityProvider;
        private NetworkLifecyclePolicy _lifecyclePolicy;
        private NetworkConnectivitySnapshot _currentConnectivity;
        private bool _pendingReconnectRequest;
        private bool _foregroundProbePending;
        private int _foregroundProbeEpoch;
        private float _foregroundProbeElapsed;
        private float _foregroundProbeTimeoutSeconds = 5f;

        // ── 重连 ─────────────────────────────────────────────────────────────
        private bool    _enableAutoReconnect    = true;
        private int     _maxReconnectAttempts   = 5;
        private int     _currentReconnectAttempt = 0;
        private float[] _reconnectIntervals     = { 1f, 2f, 5f, 10f, 30f };
        private volatile bool _isReconnecting   = false;
        private CancellationTokenSource _reconnectCts;
        private CancellationTokenSource _connectLifecycleCts;
        private string  _lastHost;
        private int     _lastPort;

        /// <summary>
        /// 重连后的应用层重新鉴权钩子（由组合根注入，框架网络层不依赖具体鉴权实现）。
        /// 传输层重连成功后，新连接通常尚未绑定原会话身份，必须由上层重放鉴权或会话恢复握手；
        /// 在恢复成功前不得补发应用请求，否则可能因身份缺失被服务端拒绝或错误路由。
        /// 返回 true 表示会话已恢复，可对外宣告“重连成功”；
        /// 返回 false 表示鉴权失败，按本次重连失败处理并继续退避重试。未注入（null）时跳过。
        /// </summary>
        private Func<UniTask<NetworkReauthenticationResult>> _reauthenticator;

        /// <summary>
        /// Pause 是移动端后台/前台的权威信号：后台暂停心跳超时与重连退避；恢复时基于
        /// 单调时间和网络代际决定探活旧连接或直接废弃重连。
        /// </summary>
        public override void OnApplicationPause(bool isPaused)
        {
            EnsureLifecycleInitialized();
            if (isPaused)
            {
                EnterBackground();
                return;
            }

            ResumeForeground("pause-resume");
        }

        /// <summary>
        /// Focus 作为移动端漏发 Pause 回调时的兜底。它与 Pause 连续到达时，
        /// 生命周期策略会把重复的进入后台/恢复判定为幂等操作。
        /// <para>
        /// 失焦只在移动端代表进入后台（见 ADR-011）。桌面窗口与无头进程失焦时进程仍在跑，
        /// 若据此进入后台，<see cref="OnUpdate"/> 会在入站消息泵之前提前返回，入站派发、在途请求超时
        /// 与心跳一并停摆，表现为"连得上、发得出、永远收不到"，服务端随后按空闲回收该连接。
        /// </para>
        /// </summary>
        public override void OnApplicationFocus(bool hasFocus)
        {
            EnsureLifecycleInitialized();
            if (!hasFocus)
            {
                if (NetworkHostProfile.FocusLossMeansBackground(NetworkHostProfile.Current))
                    EnterBackground();
                return;
            }

            // 得焦一律走恢复判定：即使本形态不认失焦，Pause 也可能已把状态置为后台。
            if (_lifecyclePolicy.IsBackground)
                ResumeForeground("focus-resume");
            else if (!IsConnected && _enableAutoReconnect && !string.IsNullOrEmpty(_lastHost))
                RequestReconnect("focus-disconnected", resetBudget: true);
        }

        private void EnsureLifecycleInitialized()
        {
            _connectivityProvider ??= new UnityNetworkConnectivityProvider();
            _lifecyclePolicy ??= new NetworkLifecyclePolicy();
            _currentConnectivity = _connectivityProvider.Capture();
            _lifecyclePolicy.Initialize(_currentConnectivity);
        }

        private void EnterBackground()
        {
            _currentConnectivity = _connectivityProvider.Capture();
            if (!_lifecyclePolicy.EnterBackground(
                    NetworkMonotonicClock.NowMilliseconds(),
                    _currentConnectivity))
                return;

            CancelForegroundProbe();
            _pendingReconnectRequest = false;
            if (_isReconnecting)
            {
                // 后台不消耗重连预算；若处于“已连上传输、正在鉴权”阶段，该连接也不得悄悄晋级。
                CancelReconnectLoop();
                _client?.Disconnect();
            }
            CancelInitialConnectionAttempt();
            GameLog.Log("[NetworkManager] 进入后台：暂停心跳超时、请求计时与重连退避。");
        }

        private void ResumeForeground(string source)
        {
            _currentConnectivity = _connectivityProvider.Capture();
            NetworkRecoveryDecision decision = _lifecyclePolicy.Resume(
                NetworkMonotonicClock.NowMilliseconds(),
                _currentConnectivity,
                IsConnected,
                IsConnectionAttemptActive);

            if (decision.BackgroundElapsedMilliseconds > 0)
            {
                _offlineQueueClock += decision.BackgroundElapsedMilliseconds / 1000d;
                _offlineQueue.Update(_offlineQueueClock);
            }

            GameLog.Log(
                $"[NetworkManager] 前台恢复 source={source}, action={decision.Action}, " +
                $"backgroundMs={decision.BackgroundElapsedMilliseconds}, networkChanged={decision.NetworkChanged}。");
            ApplyLifecycleDecision(decision);
        }

        private void RefreshConnectivity()
        {
            if (_connectivityProvider == null || _lifecyclePolicy == null) return;
            NetworkConnectivitySnapshot snapshot = _connectivityProvider.Capture();
            NetworkRecoveryDecision decision = _lifecyclePolicy.ObserveForeground(
                snapshot,
                IsConnected,
                IsConnectionAttemptActive);
            _currentConnectivity = snapshot;
            ApplyLifecycleDecision(decision);
        }

        private void ApplyLifecycleDecision(NetworkRecoveryDecision decision)
        {
            switch (decision.Action)
            {
                case NetworkRecoveryAction.None:
                    return;
                case NetworkRecoveryAction.ProbeExistingConnection:
                    BeginForegroundProbe();
                    return;
                case NetworkRecoveryAction.Reconnect:
                    RequestReconnect(decision.Reason, resetBudget: true);
                    return;
                case NetworkRecoveryAction.InvalidateAndReconnect:
                    InvalidateTransport(decision.Reason);
                    RequestReconnect(decision.Reason, resetBudget: true);
                    return;
                case NetworkRecoveryAction.InvalidateAndWaitForNetwork:
                    _pendingReconnectRequest = false;
                    InvalidateTransport(decision.Reason);
                    return;
            }
        }

        private void BeginForegroundProbe()
        {
            if (!IsConnected)
            {
                RequestReconnect("probe-without-connection", resetBudget: true);
                return;
            }

            Interlocked.Exchange(ref _dataReceivedEpoch, 0);
            _foregroundProbeEpoch = _client.ConnectionEpoch;
            _foregroundProbeElapsed = 0f;
            _foregroundProbePending = true;
            _heartbeat.OnConnected(); // 探活前清零心跳/超时计时，避免探活期误判
            if (SendHeartbeat()) return;

            CancelForegroundProbe();
            ApplyLifecycleDecision(new NetworkRecoveryDecision(
                NetworkRecoveryAction.InvalidateAndReconnect,
                "foreground-probe-unavailable"));
        }

        private void CancelForegroundProbe()
        {
            _foregroundProbePending = false;
            _foregroundProbeEpoch = 0;
            _foregroundProbeElapsed = 0f;
        }

        private void InvalidateTransport(string reason)
        {
            GameLog.Warning($"[NetworkManager] 废弃当前连接：{reason}, epoch={_client?.ConnectionEpoch ?? 0}。");
            CancelForegroundProbe();
            CancelReconnectLoop();
            _requestTracker?.CancelAll();
            _client?.Disconnect();
        }

        private void RequestReconnect(string reason, bool resetBudget)
        {
            if (!_enableAutoReconnect || string.IsNullOrEmpty(_lastHost)) return;
            if (resetBudget) _currentReconnectAttempt = 0;
            _pendingReconnectRequest = true;
            GameLog.Log($"[NetworkManager] 已排队串行重连：{reason}。");
        }

        private void PumpReconnectRequest()
        {
            if (!_pendingReconnectRequest || IsConnectionAttemptActive ||
                _lifecyclePolicy?.IsBackground == true || !_currentConnectivity.IsReachable)
                return;
            if (!_enableAutoReconnect || string.IsNullOrEmpty(_lastHost))
            {
                _pendingReconnectRequest = false;
                return;
            }

            _pendingReconnectRequest = false;
            TryReconnectAsync().Forget();
        }

        // ── 连接 / 断开 ──────────────────────────────────────────────────────

        /// <summary>连接服务器</summary>
        public async UniTask ConnectAsync(
            string host,
            int port,
            CancellationToken cancellationToken = default)
        {
            if (IsConnected) return;
            _pendingReconnectRequest = false;
            CancelForegroundProbe();
            CancelReconnectLoop();
            _isReconnecting = false;
            _lastHost = host;
            _lastPort = port;
            _currentReconnectAttempt = 0;
            _enableAutoReconnect = true;
            CancelInitialConnectionAttempt();
            var lifecycleCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            _connectLifecycleCts = lifecycleCts;
            try
            {
                await ConnectInternalAsync(host, port, lifecycleCts.Token);
            }
            finally
            {
                if (ReferenceEquals(_connectLifecycleCts, lifecycleCts))
                    _connectLifecycleCts = null;
                lifecycleCts.Dispose();
            }
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
            if (_lifecyclePolicy?.IsBackground == true)
            {
                RequestReconnect("manual-request-while-background", resetBudget: true);
                return;
            }

            _currentReconnectAttempt = 0;
            _enableAutoReconnect     = true;
            await TryReconnectAsync();
        }

        /// <summary>主动断开（关闭自动重连）。断线待发队列一并按失败收尾——不会再有重连补发它们。</summary>
        public void Disconnect()
        {
            _enableAutoReconnect = false;
            _pendingReconnectRequest = false;
            CancelForegroundProbe();
            _isReconnecting = false;
            _currentReconnectAttempt = 0;
            CancelReconnectLoop();
            CancelInitialConnectionAttempt();
            _offlineQueue.FailAll();
            _client?.Disconnect();
        }

        private async UniTask ConnectInternalAsync(
            string host,
            int port,
            CancellationToken cancellationToken = default)
        {
            try
            {
                EnsureClient();
                await _client.ConnectAsync(host, port, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                GameLog.Error($"[NetworkManager] 初次连接失败：{ex.Message}");
                if (_enableAutoReconnect && !_isReconnecting)
                {
                    bool reconnected = await TryReconnectAsync();
                    if (reconnected) return;
                }
                throw new InvalidOperationException("连接与自动重连均失败。", ex);
            }
        }

        /// <summary>
        /// 指数退避重连循环。
        /// 每次尝试前触发 OnReconnecting 事件（含等待时长），供 UI 显示倒计时。
        /// </summary>
        private async UniTask<bool> TryReconnectAsync()
        {
            if (_isReconnecting || string.IsNullOrEmpty(_lastHost)) return IsConnected;
            if (_lifecyclePolicy?.IsBackground == true || !_currentConnectivity.IsReachable)
                return false;
            _isReconnecting = true;
            CancelReconnectLoop();
            var reconnectCts = new CancellationTokenSource();
            _reconnectCts = reconnectCts;
            CancellationToken token = reconnectCts.Token;

            try
            {
                EnsureClient();
                while (_currentReconnectAttempt < _maxReconnectAttempts)
                {
                    token.ThrowIfCancellationRequested();
                    _currentReconnectAttempt++;
                    int idx = Math.Min(_currentReconnectAttempt - 1, _reconnectIntervals.Length - 1);
                    float baseWait = _reconnectIntervals[idx];
                    // 共享随机源做 ±15% 退避抖动，避免多客户端同时重连造成服务端惊群。
                    float waitSeconds = baseWait * (float)RandomUtil.NextJitterFactor(0.15);
                    OnReconnecting?.Invoke(_currentReconnectAttempt, _maxReconnectAttempts, waitSeconds);
                    await UniTask.Delay(TimeSpan.FromSeconds(waitSeconds), cancellationToken: token);

                    try
                    {
                        await _client.ConnectAsync(_lastHost, _lastPort, token);
                        int reconnectEpoch = _client.ConnectionEpoch;
                        NetworkReauthenticationResult reauthentication =
                            await TryReauthenticateAsync(token);
                        if (reauthentication == NetworkReauthenticationResult.SessionExpired)
                        {
                            _client.Disconnect();
                            _offlineQueue.FailAll();
                            _enableAutoReconnect = false;
                            _pendingReconnectRequest = false;
                            OnSessionExpired?.Invoke();
                            OnReconnectFailed?.Invoke();
                            OnError?.Invoke("会话已过期，需要重新登录。");
                            return false;
                        }
                        if (NetworkReauthenticationPolicy.ShouldRetry(reauthentication) ||
                            !NetworkReauthenticationPolicy.CanFlushOfflineQueue(reauthentication) ||
                            !IsConnected || _client.ConnectionEpoch != reconnectEpoch)
                        {
                            _client.Disconnect();
                            continue;
                        }

                        _offlineQueue.FlushAll();
                        if (IsConnected && _client.ConnectionEpoch == reconnectEpoch)
                        {
                            _currentReconnectAttempt = 0;
                            _isReconnecting = false;
                            _pendingReconnectRequest = false;
                            OnReconnectSucceeded?.Invoke();
                            return true;
                        }
                        continue;
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception ex)
                    {
                        GameLog.Warning($"[NetworkManager] 第 {_currentReconnectAttempt} 次重连失败：{ex.Message}");
                    }
                }

                _offlineQueue.FailAll();
                OnReconnectFailed?.Invoke();
                OnError?.Invoke("网络重连预算已耗尽。");
                return false;
            }
            catch (OperationCanceledException)
            {
                GameLog.Log("[NetworkManager] 重连循环已取消。");
                return false;
            }
            finally
            {
                _isReconnecting = false;
                if (ReferenceEquals(_reconnectCts, reconnectCts))
                    _reconnectCts = null;
                reconnectCts.Dispose();
            }
        }

        private void CancelReconnectLoop()
        {
            CancellationTokenSource cts = _reconnectCts;
            _reconnectCts = null;
            if (cts == null) return;
            try { cts.Cancel(); } catch { }
            cts.Dispose();
        }

        private void CancelInitialConnectionAttempt()
        {
            CancellationTokenSource cts = _connectLifecycleCts;
            _connectLifecycleCts = null;
            if (cts == null) return;
            try { cts.Cancel(); } catch { }
            cts.Dispose();
        }

        /// <summary>
        /// 调用注入的重新鉴权钩子并吞掉普通异常，区分成功、可重试失败与会话永久过期。
        /// 未注入钩子时视为无需鉴权，直接成功；生命周期取消仍向上传播以立即停止本轮恢复。
        /// </summary>
        /// <returns>会话是否已恢复（或无需恢复）。</returns>
        private async UniTask<NetworkReauthenticationResult> TryReauthenticateAsync(
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_reauthenticator == null)
            {
                return NetworkReauthenticationResult.Succeeded;
            }

            try
            {
                NetworkReauthenticationResult result = await _reauthenticator();
                cancellationToken.ThrowIfCancellationRequested();
                return result;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                GameLog.Warning($"[NetworkManager] 重连重新鉴权异常: {ex.Message}");
                return NetworkReauthenticationResult.RetryableFailure;
            }
        }

    }
}
