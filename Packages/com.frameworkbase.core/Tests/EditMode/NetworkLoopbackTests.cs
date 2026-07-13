using System;
using System.Collections.Concurrent;
using System.Security.Cryptography.X509Certificates;
using System.Threading;
using Cysharp.Threading.Tasks;
using Framework.Network;
using NUnit.Framework;
using TcpClient = Framework.Network.TcpClient;

namespace Framework.Tests
{
    /// <summary>
    /// 环回 TCP/TLS 集成测试：用真实 Socket + SslStream 覆盖 Framework.Network.TcpClient 的
    /// 连接/断开/重连/世代隔离/半包/粘包/非法帧/超大帧/背压 与 TLS 信任/Pin/轮换/超时 生命周期。
    /// <para>
    /// 说明：重连预算耗尽、重连后重鉴权退避（NetworkManager 层）、主线程回调编组（需 PlayerLoop）、
    /// 离线队列 opt-in（OfflineRequestQueueTests）等属更高层或已有专项测试，见文末注释与对应测试；
    /// NAT64-only / IPv6-only / 前后台切换 / 弱网 / 真实证书轮换 等外部环境场景见
    /// Docs/NetworkDeviceAcceptance.md，不以单测通过替代真机结论。
    /// </para>
    /// </summary>
    public class NetworkLoopbackTests
    {
        // 两张自签名测试证书（SAN=localhost,127.0.0.1），仅用于本地/CI 环回 TLS，不含任何生产密钥。
        private const string CertAPfxBase64 =
            "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";
        private const string CertAPin = "E8D45871D2DC9C26221CC079FA72F49B6084C418CEA0E975A161867891E8A7B1";

        private const string CertBPfxBase64 =
            "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";
        private const string CertBPin = "E27F838D5A3EFCA92B6234D40CB224DB092F3038E3EDE762C473EAF383740449";

        private const string Host = "127.0.0.1";
        private const string SniHost = "localhost";
        private const int WaitMs = 5000;

        private static X509Certificate2 LoadCert(string base64) =>
            new X509Certificate2(Convert.FromBase64String(base64), "test", X509KeyStorageFlags.Exportable);

        /// <summary>
        /// 在 EditMode 下驱动 UniTask 连接完成。
        /// <para>
        /// EditMode 测试运行在 Unity 主线程且主线程持有 SynchronizationContext；若直接在主线程
        /// 阻塞等待 ConnectAsync，其内部 await 的续体会被投递回被阻塞的主线程 → 死锁。
        /// 因此在线程池线程上驱动连接，使续体不捕获主线程上下文；失败时原样重抛底层异常。
        /// </para>
        /// </summary>
        private static void Connect(ClientProbe probe, string host, int port, CancellationToken ct = default) =>
            System.Threading.Tasks.Task.Run(async () => await probe.Client.ConnectAsync(host, port, ct))
                .GetAwaiter().GetResult();

        /// <summary>把 TcpClient 事件收敛为可等待句柄与计数，供测试线程同步。</summary>
        private sealed class ClientProbe : IDisposable
        {
            public readonly TcpClient Client = new TcpClient();
            public readonly ManualResetEventSlim Connected = new ManualResetEventSlim(false);
            public readonly ManualResetEventSlim Disconnected = new ManualResetEventSlim(false);
            public readonly ManualResetEventSlim Errored = new ManualResetEventSlim(false);
            public readonly ConcurrentQueue<byte[]> Frames = new ConcurrentQueue<byte[]>();
            public volatile string LastError;

            public ClientProbe()
            {
                Client.OnConnected += () => Connected.Set();
                Client.OnDisconnected += () => Disconnected.Set();
                Client.OnError += msg => { LastError = msg; Errored.Set(); };
                Client.OnReceiveWithEpoch += (epoch, buffer, len) =>
                {
                    var copy = new byte[len];
                    Buffer.BlockCopy(buffer, 0, copy, 0, len);
                    Frames.Enqueue(copy);
                };
            }

            public void Dispose()
            {
                try { Client.Disconnect(); } catch { }
                Connected.Dispose();
                Disconnected.Dispose();
                Errored.Dispose();
            }
        }

        private static byte[] Frame(byte main, byte sub, byte[] payload, ushort seq = 0) =>
            MessagePacket.Pack(main, sub, payload, seq);

        // ── 明文 TCP 生命周期 ─────────────────────────────────────────────────

        [Test]
        public void TCP连接成功_触发已连接事件()
        {
            using var server = new LoopbackTcpServer();
            using var probe = new ClientProbe();

            Connect(probe, Host, server.Port);

            Assert.IsTrue(probe.Client.IsConnected);
            Assert.IsTrue(probe.Connected.Wait(WaitMs), "OnConnected 必须触发");
            Assert.IsTrue(server.ClientReady.Wait(WaitMs), "服务端应观察到连接");
        }

        [Test]
        public void 服务端主动断开_客户端检测到断线()
        {
            using var server = new LoopbackTcpServer();
            using var probe = new ClientProbe();
            Connect(probe, Host, server.Port);
            Assert.IsTrue(server.ClientReady.Wait(WaitMs));

            server.CloseClient();

            Assert.IsTrue(probe.Disconnected.Wait(WaitMs), "服务端断开后客户端必须触发 OnDisconnected");
            Assert.IsFalse(probe.Client.IsConnected);
        }

        [Test]
        public void 客户端检测断开并重连_世代递增()
        {
            using var server = new LoopbackTcpServer();
            using var probe = new ClientProbe();
            Connect(probe, Host, server.Port);
            Assert.IsTrue(server.ClientReady.Wait(WaitMs));
            int epoch1 = probe.Client.ConnectionEpoch;

            server.CloseClient();
            Assert.IsTrue(probe.Disconnected.Wait(WaitMs));

            probe.Disconnected.Reset();
            Connect(probe, Host, server.Port); // 重连
            Assert.IsTrue(server.ClientReady.Wait(WaitMs));
            int epoch2 = probe.Client.ConnectionEpoch;

            Assert.IsTrue(probe.Client.IsConnected);
            Assert.Greater(epoch2, epoch1, "重连后 ConnectionEpoch 必须递增，用于隔离旧连接数据");
        }

        [Test]
        public void 旧Epoch响应无法完成新Epoch请求()
        {
            // TcpClient 侧世代递增保证在 NetworkRequestTracker 侧转化为"必须同时匹配 Epoch+SeqId"。
            // 此处直接对追踪器断言旧世代响应被拒（与重连世代隔离形成端到端闭环）。
            var tracker = new NetworkRequestTracker();
            bool completed = false;
            ushort seq = tracker.Register(connectionEpoch: 1,
                onResponse: _ => completed = true, onTimeout: null, onCancelled: null,
                config: NetworkRequestConfig.Silent);

            bool matchedOldEpoch = tracker.TryComplete(connectionEpoch: 2, seqId: seq, payload: new byte[] { 1 });

            Assert.IsFalse(matchedOldEpoch, "新世代不能完成旧世代登记的请求");
            Assert.IsFalse(completed);
        }

        [Test]
        public void 服务端发送半包_不足一帧时不回调()
        {
            using var server = new LoopbackTcpServer();
            using var probe = new ClientProbe();
            Connect(probe, Host, server.Port);
            Assert.IsTrue(server.ClientReady.Wait(WaitMs));

            byte[] full = Frame(1, 2, new byte[] { 0xAB, 0xCD }, 7); // 长度 10
            server.SendRaw(Slice(full, 0, 6)); // 只发前 6 字节
            Thread.Sleep(200);
            Assert.AreEqual(0, probe.Frames.Count, "不足一帧不得回调");

            server.SendRaw(Slice(full, 6, full.Length - 6)); // 补齐
            Assert.IsTrue(WaitForFrames(probe, 1, WaitMs), "补齐后应恰好回调一帧");
            probe.Frames.TryDequeue(out byte[] frame);
            CollectionAssert.AreEqual(full, frame);
        }

        [Test]
        public void 服务端粘包_一次写入多帧_逐帧回调()
        {
            using var server = new LoopbackTcpServer();
            using var probe = new ClientProbe();
            Connect(probe, Host, server.Port);
            Assert.IsTrue(server.ClientReady.Wait(WaitMs));

            byte[] f1 = Frame(1, 1, new byte[] { 1 }, 1);
            byte[] f2 = Frame(2, 2, new byte[] { 2, 2 }, 2);
            byte[] combined = new byte[f1.Length + f2.Length];
            Buffer.BlockCopy(f1, 0, combined, 0, f1.Length);
            Buffer.BlockCopy(f2, 0, combined, f1.Length, f2.Length);
            server.SendRaw(combined);

            Assert.IsTrue(WaitForFrames(probe, 2, WaitMs), "粘包应拆成两帧");
        }

        [Test]
        public void 非法帧长度_触发连接关闭()
        {
            using var server = new LoopbackTcpServer();
            using var probe = new ClientProbe();
            Connect(probe, Host, server.Port);
            Assert.IsTrue(server.ClientReady.Wait(WaitMs));

            // 长度字段 = 3，小于 HeaderSize(8) → 非法帧。
            byte[] illegal = BitConverter.GetBytes(3);
            byte[] padded = new byte[8];
            Buffer.BlockCopy(illegal, 0, padded, 0, 4);
            server.SendRaw(padded);

            Assert.IsTrue(probe.Errored.Wait(WaitMs), "非法帧长度必须上报错误");
            Assert.IsTrue(probe.Disconnected.Wait(WaitMs), "非法帧必须关闭连接");
        }

        [Test]
        public void 超大协议帧_被拒绝并关闭连接()
        {
            using var server = new LoopbackTcpServer();
            using var probe = new ClientProbe();
            probe.Client.MaxMessageSize = 64;
            Connect(probe, Host, server.Port);
            Assert.IsTrue(server.ClientReady.Wait(WaitMs));

            byte[] header = new byte[8];
            Buffer.BlockCopy(BitConverter.GetBytes(65), 0, header, 0, 4); // 65 > MaxMessageSize(64)
            server.SendRaw(header);

            Assert.IsTrue(probe.Errored.Wait(WaitMs), "超大帧必须上报错误");
            Assert.IsTrue(probe.Disconnected.Wait(WaitMs), "超大帧必须关闭连接");
        }

        [Test]
        public void 发送队列满_返回背压失败()
        {
            using var server = new LoopbackTcpServer(); // 服务端不读取，OS 发送缓冲很快填满
            using var probe = new ClientProbe();
            probe.Client.MaxSendQueuePackets = 1;
            Connect(probe, Host, server.Port);
            Assert.IsTrue(server.ClientReady.Wait(WaitMs));

            var big = new byte[256 * 1024];
            bool anyBackpressure = false;
            for (int i = 0; i < 128 && !anyBackpressure; i++)
            {
                if (!probe.Client.Send(big))
                    anyBackpressure = true;
            }

            Assert.IsTrue(anyBackpressure, "发送队列达到上限时 Send 必须返回 false（背压），绝不无界堆积");
        }

        // ── 连接取消 / 超时 ───────────────────────────────────────────────────

        [Test]
        public void 连接取消_抛出取消异常()
        {
            using var server = new LoopbackTcpServer();
            using var probe = new ClientProbe();
            using var cts = new CancellationTokenSource();
            cts.Cancel();

            Assert.Catch<OperationCanceledException>(() =>
                Connect(probe, Host, server.Port, cts.Token));
            Assert.IsFalse(probe.Client.IsConnected);
        }

        [Test]
        public void 连接超时_快速失败不挂起()
        {
            using var probe = new ClientProbe();
            probe.Client.ConnectTimeoutSeconds = 2;

            // 192.0.2.0/24 是 RFC5737 TEST-NET-1，保证不可路由，连接必然失败（超时或不可达）。
            var sw = System.Diagnostics.Stopwatch.StartNew();
            Assert.Catch(() => Connect(probe, "192.0.2.1", 9));
            sw.Stop();

            Assert.IsFalse(probe.Client.IsConnected);
            Assert.Less(sw.Elapsed.TotalSeconds, 15, "必须在超时上限内快速失败，不得无限挂起");
        }

        // ── TLS 信任与 Pin ────────────────────────────────────────────────────

        [Test]
        public void TLS系统信任失败_自签名无Pin_拒绝连接()
        {
            using var cert = LoadCert(CertAPfxBase64);
            using var server = new LoopbackTcpServer(cert);
            using var probe = new ClientProbe();
            probe.Client.Tls = new TlsClientOptions
            {
                Enabled = true,
                TargetHost = SniHost,
                AllowPinnedCertificateWithoutSystemTrust = false, // 不放行 → 自签名系统信任失败即拒绝
            };

            Assert.Catch(() => Connect(probe, Host, server.Port));
            Assert.IsFalse(probe.Client.IsConnected, "自签名且未配置 Pin/未放行时必须拒绝");
        }

        [Test]
        public void TLS_Pin匹配且显式放行系统信任_连接成功()
        {
            using var cert = LoadCert(CertAPfxBase64);
            using var server = new LoopbackTcpServer(cert);
            using var probe = new ClientProbe();
            probe.Client.Tls = new TlsClientOptions
            {
                Enabled = true,
                TargetHost = SniHost,
                CertSha256 = CertAPin,
                AllowPinnedCertificateWithoutSystemTrust = true, // 开发自签名场景显式放行
            };

            Connect(probe, Host, server.Port);
            Assert.IsTrue(probe.Client.IsConnected);
            Assert.IsTrue(probe.Connected.Wait(WaitMs));
        }

        [Test]
        public void TLS_Pin匹配但不放行系统信任_仍拒绝()
        {
            // 安全关键断言：Pin 命中不等于放行系统信任。AllowPinned=false 时自签名仍必须被拒，
            // 证明 Pin 不会静默绕过 CA/有效期/主机名校验（真实 CA 主机名不匹配属外部环境验收）。
            using var cert = LoadCert(CertAPfxBase64);
            using var server = new LoopbackTcpServer(cert);
            using var probe = new ClientProbe();
            probe.Client.Tls = new TlsClientOptions
            {
                Enabled = true,
                TargetHost = SniHost,
                CertSha256 = CertAPin,
                AllowPinnedCertificateWithoutSystemTrust = false,
            };

            Assert.Catch(() => Connect(probe, Host, server.Port));
            Assert.IsFalse(probe.Client.IsConnected);
        }

        [Test]
        public void TLS_Pin不匹配_拒绝连接()
        {
            using var cert = LoadCert(CertAPfxBase64);
            using var server = new LoopbackTcpServer(cert);
            using var probe = new ClientProbe();
            probe.Client.Tls = new TlsClientOptions
            {
                Enabled = true,
                TargetHost = SniHost,
                CertSha256 = CertBPin, // 服务端是 A，配置 B 的 Pin → 不匹配
                AllowPinnedCertificateWithoutSystemTrust = true,
            };

            Assert.Catch(() => Connect(probe, Host, server.Port));
            Assert.IsFalse(probe.Client.IsConnected, "Pin 不匹配必须拒绝");
        }

        [Test]
        public void TLS_双Pin任一命中即通过()
        {
            using var cert = LoadCert(CertAPfxBase64);
            using var server = new LoopbackTcpServer(cert);
            using var probe = new ClientProbe();
            probe.Client.Tls = new TlsClientOptions
            {
                Enabled = true,
                TargetHost = SniHost,
                CertSha256Pins = new[] { CertBPin, CertAPin }, // 含一个错误 Pin 和正确 Pin
                AllowPinnedCertificateWithoutSystemTrust = true,
            };

            Connect(probe, Host, server.Port);
            Assert.IsTrue(probe.Client.IsConnected, "双 Pin 中任一命中即应通过");
        }

        [Test]
        public void TLS证书轮换_轮换窗口内双Pin通过_移除旧Pin后新证书需新Pin()
        {
            // 服务端已切到新证书 B。轮换窗口内客户端同时配置 [A,B] → 通过。
            using (var certB = LoadCert(CertBPfxBase64))
            using (var server = new LoopbackTcpServer(certB))
            using (var probe = new ClientProbe())
            {
                probe.Client.Tls = new TlsClientOptions
                {
                    Enabled = true,
                    TargetHost = SniHost,
                    CertSha256Pins = new[] { CertAPin, CertBPin },
                    AllowPinnedCertificateWithoutSystemTrust = true,
                };
                Connect(probe, Host, server.Port);
                Assert.IsTrue(probe.Client.IsConnected, "轮换窗口内新旧 Pin 并存应通过");
            }

            // 轮换完成、移除旧 Pin A 后，仍只信任 A 的客户端连不上新证书 B（迫使及时更新 Pin）。
            using (var certB = LoadCert(CertBPfxBase64))
            using (var server = new LoopbackTcpServer(certB))
            using (var probe = new ClientProbe())
            {
                probe.Client.Tls = new TlsClientOptions
                {
                    Enabled = true,
                    TargetHost = SniHost,
                    CertSha256Pins = new[] { CertAPin }, // 只剩旧 Pin
                    AllowPinnedCertificateWithoutSystemTrust = true,
                };
                Assert.Catch(() => Connect(probe, Host, server.Port));
                Assert.IsFalse(probe.Client.IsConnected);
            }
        }

        [Test]
        public void TLS握手超时_服务端不握手时快速失败()
        {
            using var cert = LoadCert(CertAPfxBase64);
            using var server = new LoopbackTcpServer(cert, skipTlsHandshake: true); // 接受 TCP 但不握手
            using var probe = new ClientProbe();
            probe.Client.TlsHandshakeTimeoutSeconds = 2;
            probe.Client.Tls = new TlsClientOptions
            {
                Enabled = true,
                TargetHost = SniHost,
                CertSha256 = CertAPin,
                AllowPinnedCertificateWithoutSystemTrust = true,
            };

            var sw = System.Diagnostics.Stopwatch.StartNew();
            Assert.Catch(() => Connect(probe, Host, server.Port));
            sw.Stop();

            Assert.IsFalse(probe.Client.IsConnected);
            Assert.Less(sw.Elapsed.TotalSeconds, 10, "TLS 握手必须在握手超时上限内失败，不得无限挂起");
        }

        private static byte[] Slice(byte[] source, int offset, int count)
        {
            var slice = new byte[count];
            Buffer.BlockCopy(source, offset, slice, 0, count);
            return slice;
        }

        private static bool WaitForFrames(ClientProbe probe, int expected, int timeoutMs)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < timeoutMs)
            {
                if (probe.Frames.Count >= expected) return true;
                Thread.Sleep(20);
            }
            return probe.Frames.Count >= expected;
        }
    }
}
