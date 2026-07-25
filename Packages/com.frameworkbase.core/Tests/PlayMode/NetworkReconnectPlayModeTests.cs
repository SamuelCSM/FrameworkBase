using System;
using System.Collections;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using Cysharp.Threading.Tasks;
using Framework;
using Framework.Network;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace Framework.Tests.PlayMode
{
    /// <summary>
    /// NetworkManager 层重连 / 重鉴权的 PlayMode 特征化测试（拆分计划「切片0」安全网）。
    /// <para>
    /// 这些行为靠字段时序维系隐式不变量（重连中不误播 OnConnected、重鉴权往返再断线不启第二轮循环等），
    /// 只有在真实 PlayerLoop 下驱动 <see cref="NetworkManager.OnUpdate"/> + UniTask 调度才能覆盖，
    /// EditMode 测不到。它们是后续切片3(lifecycle/probe)+切片4(reconnect) 重构的回归标尺——
    /// 抽类前后这些断言必须逐条保持绿。
    /// </para>
    /// <para>
    /// 用真实环回 TCP（127.0.0.1，系统分配端口）驱动，注入恒定可达的连通性与可控的重鉴权钩子，
    /// 用极短退避间隔保证用例快速收敛；不依赖场景 / GameEntry / Addressables。
    /// </para>
    /// </summary>
    public class NetworkReconnectPlayModeTests
    {
        private const string Host = "127.0.0.1";

        // 本类用例故意制造网络错误（服务器下线 / 连接被重置 / 会话过期），NetworkManager 会 GameLog.Error。
        // PlayMode Runner 默认把任何 Debug.LogError 判为用例失败；文本/次数随时序变化无法用 LogAssert.Expect 精确匹配。
        // [UnityTest] 下 [SetUp] 里设 ignoreFailingMessages 不生效（协程体重置日志作用域），故每个用例体首行显式放行——
        // 用例以"事件是否如期触发"为断言口径，而非日志静默。

        /// <summary>恒定"可达 + 局域网"的连通性桩，去除真机 <c>Application.internetReachability</c> 的不确定性。</summary>
        private sealed class AlwaysReachable : INetworkConnectivityProvider
        {
            public NetworkConnectivitySnapshot Capture()
                => new NetworkConnectivitySnapshot(true, NetworkTransportKind.LocalArea, "test");
        }

        /// <summary>把 NetworkManager 接上真实 Update 循环——OnUpdate 驱动连接事件排空 / 心跳 / 重连泵。</summary>
        private sealed class Driver : MonoBehaviour
        {
            public NetworkManager Nm;
            private void Update() => Nm?.OnUpdate(Time.deltaTime);
        }

        /// <summary>
        /// 极简明文环回服务器：持续 accept（支持重连再接入），可主动断开当前客户端，Dispose 后端口不可达。
        /// 仅用于本机 127.0.0.1，端口取 0 由系统分配。
        /// </summary>
        private sealed class MiniServer : IDisposable
        {
            private readonly TcpListener _listener;
            private readonly Thread _acceptThread;
            private volatile bool _running = true;
            private readonly object _lock = new object();
            private Socket _client;

            public int Port { get; }

            public MiniServer()
            {
                _listener = new TcpListener(IPAddress.Loopback, 0);
                _listener.Start();
                Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
                _acceptThread = new Thread(AcceptLoop) { IsBackground = true, Name = "MiniServer_Accept" };
                _acceptThread.Start();
            }

            private void AcceptLoop()
            {
                while (_running)
                {
                    Socket socket;
                    try { socket = _listener.AcceptSocket(); }
                    catch { return; } // 监听器已关闭
                    lock (_lock) { _client = socket; }
                }
            }

            /// <summary>主动关闭当前客户端连接，触发客户端断线检测；监听器继续接受后续重连。</summary>
            public void CloseClient()
            {
                Socket socket;
                lock (_lock) { socket = _client; _client = null; }
                try { socket?.Shutdown(SocketShutdown.Both); } catch { }
                try { socket?.Close(); } catch { }
            }

            public void Dispose()
            {
                _running = false;
                CloseClient();
                try { _listener.Stop(); } catch { }
                if (_acceptThread != null && _acceptThread.IsAlive)
                    _acceptThread.Join(1000);
            }
        }

        private static (NetworkManager nm, Driver driver) NewManager(
            int maxAttempts,
            Func<UniTask<NetworkReauthenticationResult>> reauth = null)
        {
            var nm = new NetworkManager();
            nm.OnInit();
            nm.SetConnectivityProvider(new AlwaysReachable());
            nm.SetReconnectIntervals(new[] { 0.05f }); // 极短退避，用例快速收敛
            nm.SetMaxReconnectAttempts(maxAttempts);
            if (reauth != null) nm.SetReauthenticationProvider(reauth);

            var driver = new GameObject("nm-driver").AddComponent<Driver>();
            driver.Nm = nm;
            return (nm, driver);
        }

        private static void Teardown(NetworkManager nm, Driver driver, IDisposable server)
        {
            try { nm.OnShutdown(); } catch { }
            if (driver != null) UnityEngine.Object.Destroy(driver.gameObject);
            server?.Dispose();
        }

        /// <summary>在真实帧循环下等待条件成立或超时；返回条件最终是否成立。</summary>
        private static async UniTask<bool> WaitUntil(Func<bool> condition, float timeoutSeconds)
        {
            float deadline = Time.realtimeSinceStartup + timeoutSeconds;
            while (!condition() && Time.realtimeSinceStartup < deadline)
                await UniTask.Yield();
            return condition();
        }

        [UnityTest]
        public IEnumerator 服务器不可达_重连预算耗尽_触发OnReconnectFailed() => UniTask.ToCoroutine(async () =>
        {
            LogAssert.ignoreFailingMessages = true; // 放行预期内的网络错误日志噪声（见类注释）
            var server = new MiniServer();
            var (nm, driver) = NewManager(maxAttempts: 2);
            bool failed = false;
            nm.OnReconnectFailed += () => failed = true;

            try
            {
                await nm.ConnectAsync(Host, server.Port);
                Assert.IsTrue(nm.IsConnected, "初次连接应成功");

                server.Dispose(); // 端口彻底不可达：后续所有重连尝试必然失败

                Assert.IsTrue(await WaitUntil(() => failed, 12f),
                    "所有重连尝试失败后必须触发 OnReconnectFailed（预算耗尽）");
                Assert.IsFalse(nm.IsConnected);
            }
            finally { Teardown(nm, driver, null); }
        });

        [UnityTest]
        public IEnumerator 断线自动重连成功_触发OnReconnectSucceeded_且重连中不误播OnConnected()
            => UniTask.ToCoroutine(async () =>
        {
            LogAssert.ignoreFailingMessages = true; // 放行预期内的网络错误日志噪声（见类注释）
            var server = new MiniServer();
            var (nm, driver) = NewManager(maxAttempts: 5); // 无重鉴权钩子 = 视为已恢复
            int onConnected = 0;
            bool reconnected = false;
            nm.OnConnected += () => onConnected++;
            nm.OnReconnectSucceeded += () => reconnected = true;

            try
            {
                await nm.ConnectAsync(Host, server.Port);
                Assert.IsTrue(await WaitUntil(() => onConnected >= 1, 5f), "初次连接应广播一次 OnConnected");

                server.CloseClient(); // 掉线但服务器继续监听 → 自动重连应成功

                Assert.IsTrue(await WaitUntil(() => reconnected, 12f), "重连成功必须触发 OnReconnectSucceeded");
                Assert.IsTrue(nm.IsConnected);
                Assert.AreEqual(1, onConnected,
                    "重连中传输层连上 ≠ 会话恢复，不得再次广播 OnConnected（防业务在匿名连接上抢跑）");
            }
            finally { Teardown(nm, driver, server); }
        });

        [UnityTest]
        public IEnumerator 重鉴权返回会话过期_触发OnSessionExpired并停止自动重连()
            => UniTask.ToCoroutine(async () =>
        {
            LogAssert.ignoreFailingMessages = true; // 放行预期内的网络错误日志噪声（见类注释）
            var server = new MiniServer();
            bool sessionExpired = false;
            var (nm, driver) = NewManager(
                maxAttempts: 5,
                reauth: () => UniTask.FromResult(NetworkReauthenticationResult.SessionExpired));
            nm.OnSessionExpired += () => sessionExpired = true;

            try
            {
                await nm.ConnectAsync(Host, server.Port);
                Assert.IsTrue(nm.IsConnected);

                server.CloseClient(); // 触发重连 → 传输连上 → 重鉴权判会话过期

                Assert.IsTrue(await WaitUntil(() => sessionExpired, 12f),
                    "重鉴权返回 SessionExpired 必须触发 OnSessionExpired");
                Assert.IsFalse(nm.IsConnected, "会话过期后应断开");

                // 会话过期后自动重连被关闭：再断开也不应重新连上
                bool reconnected = false;
                nm.OnReconnectSucceeded += () => reconnected = true;
                server.CloseClient();
                Assert.IsFalse(await WaitUntil(() => reconnected, 2f),
                    "会话过期后自动重连必须已关闭，不得再自动重连");
            }
            finally { Teardown(nm, driver, server); }
        });

        [UnityTest]
        public IEnumerator 重鉴权可重试失败_耗尽预算后触发OnReconnectFailed()
            => UniTask.ToCoroutine(async () =>
        {
            LogAssert.ignoreFailingMessages = true; // 放行预期内的网络错误日志噪声（见类注释）
            var server = new MiniServer();
            int reauthCalls = 0;
            bool failed = false;
            var (nm, driver) = NewManager(
                maxAttempts: 2,
                reauth: () =>
                {
                    reauthCalls++;
                    return UniTask.FromResult(NetworkReauthenticationResult.RetryableFailure);
                });
            nm.OnReconnectFailed += () => failed = true;

            try
            {
                await nm.ConnectAsync(Host, server.Port);
                server.CloseClient(); // 每轮：传输连上但重鉴权可重试失败 → 断开重试 → 耗尽预算

                Assert.IsTrue(await WaitUntil(() => failed, 12f),
                    "重鉴权始终可重试失败、预算耗尽后必须触发 OnReconnectFailed");
                Assert.GreaterOrEqual(reauthCalls, 1, "至少应尝试过一次重鉴权");
                Assert.IsFalse(nm.IsConnected);
            }
            finally { Teardown(nm, driver, server); }
        });

        [UnityTest]
        public IEnumerator 重鉴权往返期间二次断线_不并发启动第二轮重连循环()
            => UniTask.ToCoroutine(async () =>
        {
            LogAssert.ignoreFailingMessages = true; // 放行预期内的网络错误日志噪声（见类注释）
            var server = new MiniServer();
            var gate = new UniTaskCompletionSource<NetworkReauthenticationResult>();
            int reauthCalls = 0;
            var (nm, driver) = NewManager(
                maxAttempts: 5,
                reauth: () =>
                {
                    reauthCalls++;
                    return gate.Task; // 卡在重鉴权中，模拟往返未决
                });

            try
            {
                await nm.ConnectAsync(Host, server.Port);

                server.CloseClient(); // 第一轮重连：传输连上后卡在重鉴权 gate
                Assert.IsTrue(await WaitUntil(() => reauthCalls == 1, 12f),
                    "第一轮重连应连上并进入重鉴权（reauthCalls==1）");

                server.CloseClient(); // 重鉴权往返期间二次断线
                // 给帧循环足够机会去（错误地）启动第二轮：若无保护，reauthCalls 会变成 2
                await UniTask.Delay(400);

                Assert.AreEqual(1, reauthCalls,
                    "重鉴权往返期间的二次断线不得并发启动第二轮重连循环（_isReconnecting 守卫）");

                // 放行第一轮并等其在 _client 仍有效时干净收尾（SessionExpired 分支），
                // 避免 Teardown 先置空 _client 后续体再触达导致后台任务 NRE 污染用例。
                gate.TrySetResult(NetworkReauthenticationResult.SessionExpired);
                await WaitUntil(() => !nm.IsReconnecting, 3f);
            }
            finally
            {
                gate.TrySetResult(NetworkReauthenticationResult.SessionExpired); // 兜底（幂等）
                Teardown(nm, driver, server);
            }
        });
    }
}
