using System;
using System.IO;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using System.Threading;

namespace Framework.Tests
{
    /// <summary>
    /// 环回 TCP/TLS 测试服务器夹具：用真实 <see cref="TcpListener"/> + 可选 <see cref="SslStream"/>
    /// 驱动 <c>Framework.Network.TcpClient</c> 的真实 Socket/TLS 生命周期。
    /// <para>
    /// 只服务本机 127.0.0.1 环回，端口取 0 由系统分配，避免与真实网络或端口占用冲突。
    /// 服务器不主动读取客户端数据（默认行为），使背压测试可复现；需要主动断开时调用 <see cref="CloseClient"/>。
    /// </para>
    /// <para>线程模型：接受循环在后台线程；测试线程通过 <see cref="ClientReady"/> 等事件同步。</para>
    /// </summary>
    public sealed class LoopbackTcpServer : IDisposable
    {
        private readonly TcpListener _listener;
        private readonly X509Certificate2 _serverCert;
        private readonly bool _skipTlsHandshake;
        private readonly Thread _acceptThread;
        private volatile bool _running = true;

        private readonly object _clientLock = new object();
        private Socket _clientSocket;
        private Stream _clientStream;

        /// <summary>系统分配的监听端口。</summary>
        public int Port { get; }

        /// <summary>有客户端完成连接（含 TLS 握手，若启用）后置位。</summary>
        public readonly ManualResetEventSlim ClientReady = new ManualResetEventSlim(false);

        /// <summary>服务端 TLS 握手失败时记录的异常（用于诊断，不影响客户端断言）。</summary>
        public volatile Exception LastServerError;

        /// <param name="serverCert">非空则启用 TLS（服务端出示该证书）。</param>
        /// <param name="skipTlsHandshake">为 true 时接受 TCP 后不进行 TLS 握手（用于握手超时测试）。</param>
        public LoopbackTcpServer(X509Certificate2 serverCert = null, bool skipTlsHandshake = false)
        {
            _serverCert = serverCert;
            _skipTlsHandshake = skipTlsHandshake;
            _listener = new TcpListener(IPAddress.Loopback, 0);
            _listener.Start();
            Port = ((IPEndPoint)_listener.LocalEndpoint).Port;

            _acceptThread = new Thread(AcceptLoop) { IsBackground = true, Name = "LoopbackTcpServer_Accept" };
            _acceptThread.Start();
        }

        private void AcceptLoop()
        {
            while (_running)
            {
                Socket socket;
                try
                {
                    socket = _listener.AcceptSocket();
                }
                catch
                {
                    return; // 监听器已关闭
                }

                try
                {
                    Stream stream = new NetworkStream(socket, ownsSocket: false);
                    if (_serverCert != null && !_skipTlsHandshake)
                    {
                        var ssl = new SslStream(stream, false);
                        // 服务端握手：自签名证书，客户端侧决定是否信任（系统信任/Pin/放行）。
                        ssl.AuthenticateAsServer(_serverCert, clientCertificateRequired: false,
                            checkCertificateRevocation: false);
                        stream = ssl;
                    }
                    else if (_serverCert != null && _skipTlsHandshake)
                    {
                        // 故意不握手：持有 socket 不动，客户端 AuthenticateAsClient 会握手超时。
                        lock (_clientLock) { _clientSocket = socket; _clientStream = stream; }
                        continue;
                    }

                    lock (_clientLock)
                    {
                        _clientSocket = socket;
                        _clientStream = stream;
                    }
                    ClientReady.Set();
                }
                catch (Exception ex)
                {
                    // 握手失败（如客户端 Pin 不匹配主动中断）属预期，记录即可。
                    LastServerError = ex;
                    try { socket.Close(); } catch { }
                }
            }
        }

        /// <summary>向当前客户端连接写入任意原始字节（仅明文模式使用，用于半包/粘包/非法帧构造）。</summary>
        public void SendRaw(byte[] bytes)
        {
            Stream stream;
            lock (_clientLock) stream = _clientStream;
            if (stream == null) throw new InvalidOperationException("尚无客户端连接。");
            stream.Write(bytes, 0, bytes.Length);
            stream.Flush();
        }

        /// <summary>服务端主动关闭当前客户端连接（触发客户端断线检测）。</summary>
        public void CloseClient()
        {
            Socket socket;
            Stream stream;
            lock (_clientLock)
            {
                socket = _clientSocket;
                stream = _clientStream;
                _clientSocket = null;
                _clientStream = null;
            }
            ClientReady.Reset();
            try { stream?.Dispose(); } catch { }
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
            ClientReady.Dispose();
        }
    }
}
