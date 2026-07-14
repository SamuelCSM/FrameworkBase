using Framework.Network;
using NUnit.Framework;

namespace Framework.Tests
{
    public class NetworkLifecycleTests
    {
        private static NetworkConnectivitySnapshot Wifi(string generation = "wifi-1") =>
            new NetworkConnectivitySnapshot(true, NetworkTransportKind.LocalArea, generation);

        private static NetworkConnectivitySnapshot Carrier(string generation = "carrier-1") =>
            new NetworkConnectivitySnapshot(true, NetworkTransportKind.Carrier, generation);

        private static NetworkConnectivitySnapshot Offline() =>
            new NetworkConnectivitySnapshot(false, NetworkTransportKind.None, "offline");

        [Test]
        public void 短后台且网络未变_先主动探活而非盲信Connected标志()
        {
            var policy = new NetworkLifecyclePolicy(connectedGraceMilliseconds: 10_000);
            policy.Initialize(Wifi());
            Assert.IsTrue(policy.EnterBackground(1000, Wifi()));

            NetworkRecoveryDecision decision = policy.Resume(
                5000, Wifi(), isConnected: true, isReconnecting: false);

            Assert.AreEqual(NetworkRecoveryAction.ProbeExistingConnection, decision.Action);
            Assert.AreEqual(4000, decision.BackgroundElapsedMilliseconds);
        }

        [Test]
        public void 长后台_即使本地仍显示Connected也废弃旧Epoch并重连()
        {
            var policy = new NetworkLifecyclePolicy(connectedGraceMilliseconds: 10_000);
            policy.Initialize(Wifi());
            policy.EnterBackground(1000, Wifi());

            NetworkRecoveryDecision decision = policy.Resume(
                20_000, Wifi(), isConnected: true, isReconnecting: false);

            Assert.AreEqual(NetworkRecoveryAction.InvalidateAndReconnect, decision.Action);
            Assert.IsFalse(decision.NetworkChanged);
        }

        [Test]
        public void 后台WiFi切蜂窝_不探活旧TCP直接重连并重鉴权()
        {
            var policy = new NetworkLifecyclePolicy();
            policy.Initialize(Wifi());
            policy.EnterBackground(1000, Wifi());

            NetworkRecoveryDecision decision = policy.Resume(
                2000, Carrier(), isConnected: true, isReconnecting: false);

            Assert.AreEqual(NetworkRecoveryAction.InvalidateAndReconnect, decision.Action);
            Assert.IsTrue(decision.NetworkChanged);
        }

        [Test]
        public void 同为WiFi但原生网络代际变化_仍视为网络切换()
        {
            var policy = new NetworkLifecyclePolicy();
            policy.Initialize(Wifi("wifi-a"));

            NetworkRecoveryDecision decision = policy.ObserveForeground(
                Wifi("wifi-b"), isConnected: true, isReconnecting: false);

            Assert.AreEqual(NetworkRecoveryAction.InvalidateAndReconnect, decision.Action);
            Assert.IsTrue(decision.NetworkChanged);
        }

        [Test]
        public void 无网恢复_只在网络可达后请求串行重连()
        {
            var policy = new NetworkLifecyclePolicy();
            policy.Initialize(Wifi());

            NetworkRecoveryDecision lost = policy.ObserveForeground(
                Offline(), isConnected: true, isReconnecting: false);
            Assert.AreEqual(NetworkRecoveryAction.InvalidateAndWaitForNetwork, lost.Action);

            NetworkRecoveryDecision restored = policy.ObserveForeground(
                Carrier(), isConnected: false, isReconnecting: false);
            Assert.AreEqual(NetworkRecoveryAction.Reconnect, restored.Action);
        }

        [Test]
        public void 重连中再次断网_取消当前退避并等待网络恢复()
        {
            var policy = new NetworkLifecyclePolicy();
            policy.Initialize(Wifi());

            NetworkRecoveryDecision decision = policy.ObserveForeground(
                Offline(), isConnected: false, isReconnecting: true);

            Assert.AreEqual(NetworkRecoveryAction.InvalidateAndWaitForNetwork, decision.Action);
        }

        [Test]
        public void 重复前台回调_幂等不启动第二轮恢复()
        {
            var policy = new NetworkLifecyclePolicy();
            policy.Initialize(Wifi());
            policy.EnterBackground(1000, Wifi());
            NetworkRecoveryDecision first = policy.Resume(
                2000, Wifi(), isConnected: false, isReconnecting: false);
            NetworkRecoveryDecision duplicate = policy.Resume(
                2100, Wifi(), isConnected: false, isReconnecting: false);

            Assert.AreEqual(NetworkRecoveryAction.Reconnect, first.Action);
            Assert.AreEqual(NetworkRecoveryAction.None, duplicate.Action);
            Assert.AreEqual("duplicate-resume", duplicate.Reason);
        }

        [Test]
        public void 后台恢复时仍无网_旧连接必须作废但不立即空转重试()
        {
            var policy = new NetworkLifecyclePolicy();
            policy.Initialize(Wifi());
            policy.EnterBackground(1000, Wifi());

            NetworkRecoveryDecision decision = policy.Resume(
                3000, Offline(), isConnected: true, isReconnecting: false);

            Assert.AreEqual(NetworkRecoveryAction.InvalidateAndWaitForNetwork, decision.Action);
        }

        [Test]
        public void Token过期是永久失败_禁止重复重试和离线队列补发()
        {
            Assert.IsFalse(NetworkReauthenticationPolicy.ShouldRetry(
                NetworkReauthenticationResult.SessionExpired));
            Assert.IsFalse(NetworkReauthenticationPolicy.CanFlushOfflineQueue(
                NetworkReauthenticationResult.SessionExpired));

            Assert.IsTrue(NetworkReauthenticationPolicy.ShouldRetry(
                NetworkReauthenticationResult.RetryableFailure));
            Assert.IsTrue(NetworkReauthenticationPolicy.CanFlushOfflineQueue(
                NetworkReauthenticationResult.Succeeded));
        }
    }
}
