using System;
using System.Diagnostics;
using UnityEngine;

namespace Framework.Network
{
    public enum NetworkTransportKind
    {
        None,
        LocalArea,
        Carrier,
        Unknown,
    }

    /// <summary>重连后的会话恢复结果；SessionExpired 是永久失败，禁止继续拿同一过期令牌空转重试。</summary>
    public enum NetworkReauthenticationResult
    {
        Succeeded,
        RetryableFailure,
        SessionExpired,
    }

    internal static class NetworkReauthenticationPolicy
    {
        public static bool ShouldRetry(NetworkReauthenticationResult result) =>
            result == NetworkReauthenticationResult.RetryableFailure;

        public static bool CanFlushOfflineQueue(NetworkReauthenticationResult result) =>
            result == NetworkReauthenticationResult.Succeeded;
    }

    /// <summary>可观测的网络连通性快照。Fingerprint 由平台适配层提供，不应包含 SSID 等敏感信息。</summary>
    public readonly struct NetworkConnectivitySnapshot
    {
        public NetworkConnectivitySnapshot(bool isReachable, NetworkTransportKind transport, string fingerprint)
        {
            IsReachable = isReachable;
            Transport = transport;
            Fingerprint = fingerprint ?? string.Empty;
        }

        public bool IsReachable { get; }
        public NetworkTransportKind Transport { get; }
        public string Fingerprint { get; }

        public bool IsSameNetwork(NetworkConnectivitySnapshot other) =>
            IsReachable == other.IsReachable &&
            Transport == other.Transport &&
            string.Equals(Fingerprint, other.Fingerprint, StringComparison.Ordinal);
    }

    public interface INetworkConnectivityProvider
    {
        NetworkConnectivitySnapshot Capture();
    }

    /// <summary>
    /// Unity 默认连通性适配。只能区分无网 / 局域网 / 蜂窝；项目可注入原生网络监控，
    /// 用不含个人信息的接口/路由代际作为 Fingerprint，以识别同类型网络之间的切换。
    /// </summary>
    internal sealed class UnityNetworkConnectivityProvider : INetworkConnectivityProvider
    {
        public NetworkConnectivitySnapshot Capture()
        {
            switch (Application.internetReachability)
            {
                case NetworkReachability.NotReachable:
                    return new NetworkConnectivitySnapshot(false, NetworkTransportKind.None, "offline");
                case NetworkReachability.ReachableViaLocalAreaNetwork:
                    return new NetworkConnectivitySnapshot(true, NetworkTransportKind.LocalArea, "local-area");
                case NetworkReachability.ReachableViaCarrierDataNetwork:
                    return new NetworkConnectivitySnapshot(true, NetworkTransportKind.Carrier, "carrier");
                default:
                    return new NetworkConnectivitySnapshot(true, NetworkTransportKind.Unknown, "unknown");
            }
        }
    }

    internal enum NetworkRecoveryAction
    {
        None,
        ProbeExistingConnection,
        Reconnect,
        InvalidateAndReconnect,
        InvalidateAndWaitForNetwork,
    }

    internal readonly struct NetworkRecoveryDecision
    {
        public NetworkRecoveryDecision(
            NetworkRecoveryAction action,
            string reason,
            long backgroundElapsedMilliseconds = 0,
            bool networkChanged = false)
        {
            Action = action;
            Reason = reason ?? string.Empty;
            BackgroundElapsedMilliseconds = Math.Max(0, backgroundElapsedMilliseconds);
            NetworkChanged = networkChanged;
        }

        public NetworkRecoveryAction Action { get; }
        public string Reason { get; }
        public long BackgroundElapsedMilliseconds { get; }
        public bool NetworkChanged { get; }
    }

    /// <summary>
    /// 纯逻辑生命周期策略：基于单调时间和网络快照决定“探活 / 废弃旧连接 / 重连 / 等网”。
    /// 不直接持有 Socket 或 Unity 生命周期，因此可确定性覆盖短后台、长后台和网络切换矩阵。
    /// </summary>
    internal sealed class NetworkLifecyclePolicy
    {
        private NetworkConnectivitySnapshot _backgroundNetwork;
        private NetworkConnectivitySnapshot _lastObservedNetwork;
        private long _backgroundStartedAtMilliseconds;
        private bool _hasObservedNetwork;

        public NetworkLifecyclePolicy(long connectedGraceMilliseconds = 10_000)
        {
            ConnectedGraceMilliseconds = Math.Max(0, connectedGraceMilliseconds);
        }

        public long ConnectedGraceMilliseconds { get; }
        public bool IsBackground { get; private set; }

        public void Initialize(NetworkConnectivitySnapshot initial)
        {
            _lastObservedNetwork = initial;
            _hasObservedNetwork = true;
        }

        public bool EnterBackground(long nowMilliseconds, NetworkConnectivitySnapshot network)
        {
            if (IsBackground) return false;
            IsBackground = true;
            _backgroundStartedAtMilliseconds = Math.Max(0, nowMilliseconds);
            _backgroundNetwork = network;
            _lastObservedNetwork = network;
            _hasObservedNetwork = true;
            return true;
        }

        public NetworkRecoveryDecision Resume(
            long nowMilliseconds,
            NetworkConnectivitySnapshot network,
            bool isConnected,
            bool isReconnecting)
        {
            if (!IsBackground)
                return new NetworkRecoveryDecision(NetworkRecoveryAction.None, "duplicate-resume");

            IsBackground = false;
            long elapsed = Math.Max(0, nowMilliseconds - _backgroundStartedAtMilliseconds);
            bool changed = !_backgroundNetwork.IsSameNetwork(network);
            _lastObservedNetwork = network;
            _hasObservedNetwork = true;

            if (!network.IsReachable)
            {
                NetworkRecoveryAction action = isConnected || isReconnecting
                    ? NetworkRecoveryAction.InvalidateAndWaitForNetwork
                    : NetworkRecoveryAction.None;
                return new NetworkRecoveryDecision(action, "resume-offline", elapsed, changed);
            }

            if (isConnected && !isReconnecting && !changed && elapsed <= ConnectedGraceMilliseconds)
            {
                return new NetworkRecoveryDecision(
                    NetworkRecoveryAction.ProbeExistingConnection,
                    "short-background-probe",
                    elapsed,
                    false);
            }

            NetworkRecoveryAction recovery = isConnected || isReconnecting
                ? NetworkRecoveryAction.InvalidateAndReconnect
                : NetworkRecoveryAction.Reconnect;
            string reason = changed ? "network-changed-in-background" : "background-grace-exceeded";
            return new NetworkRecoveryDecision(recovery, reason, elapsed, changed);
        }

        public NetworkRecoveryDecision ObserveForeground(
            NetworkConnectivitySnapshot network,
            bool isConnected,
            bool isReconnecting)
        {
            if (IsBackground)
                return new NetworkRecoveryDecision(NetworkRecoveryAction.None, "background");
            if (!_hasObservedNetwork)
            {
                Initialize(network);
                return new NetworkRecoveryDecision(NetworkRecoveryAction.None, "initial-snapshot");
            }
            if (_lastObservedNetwork.IsSameNetwork(network))
                return new NetworkRecoveryDecision(NetworkRecoveryAction.None, "unchanged");

            NetworkConnectivitySnapshot previous = _lastObservedNetwork;
            _lastObservedNetwork = network;
            if (!network.IsReachable)
            {
                NetworkRecoveryAction offlineAction = isConnected || isReconnecting
                    ? NetworkRecoveryAction.InvalidateAndWaitForNetwork
                    : NetworkRecoveryAction.None;
                return new NetworkRecoveryDecision(offlineAction, "connectivity-lost", networkChanged: true);
            }

            // 从无网恢复，或 Wi-Fi/蜂窝/原生网络代际发生变化：旧 TCP 即使仍显示 Connected 也可能半开。
            bool changed = !previous.IsSameNetwork(network);
            NetworkRecoveryAction action = isConnected || isReconnecting
                ? NetworkRecoveryAction.InvalidateAndReconnect
                : NetworkRecoveryAction.Reconnect;
            return new NetworkRecoveryDecision(action, "connectivity-restored-or-switched", networkChanged: changed);
        }
    }

    internal static class NetworkMonotonicClock
    {
        public static long NowMilliseconds() =>
            (long)(Stopwatch.GetTimestamp() * 1000d / Stopwatch.Frequency);
    }
}
