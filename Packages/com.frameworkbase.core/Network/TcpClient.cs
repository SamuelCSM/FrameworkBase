using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Threading;
using System.Threading.Tasks;
using Cysharp.Threading.Tasks;

namespace Framework.Network
{
    /// <summary>
    /// 移动端 TCP 长连接传输客户端，负责 DNS 解析、IPv6/IPv4 地址回退、可选 TLS、证书校验、收发线程和有界背压。
    /// <para>
    /// 发送队列具有固定容量，<see cref="Send"/> 在队列已满时立即返回 false，绝不允许无界堆积导致托管内存持续增长。
    /// 上层必须把 false 视为真实发送失败，并结束对应请求或执行限流，不能静默丢包。
    /// </para>
    /// <para>
    /// 每次成功建立传输都会递增 ConnectionEpoch。收包事件携带捕获的 Epoch，使 NetworkManager 能与 ushort SeqId 组合匹配，
    /// 拒绝旧连接线程、延迟响应或重连竞态产生的陈旧数据。
    /// </para>
    /// <para>
    /// 收发回调发生在后台线程，不得直接访问 Unity API。NetworkManager 必须先投递到有界主线程队列，再触发业务或 UI 回调。
    /// </para>
    /// </summary>
    public sealed class TcpClient
    {
        private const int ReceiveChunkSize = 8192;
        private const int InitialMessageBufferSize = 16384;

        private readonly object _stateLock = new object();
        private readonly SemaphoreSlim _connectGate = new SemaphoreSlim(1, 1);
        private Socket _socket;
        private Stream _stream;
        private Thread _receiveThread;
        private Thread _sendThread;
        private BlockingCollection<byte[]> _sendQueue;
        private volatile bool _isConnected;
        private int _connectionEpoch;

        /// <summary>DNS 解析与单个 TCP 地址连接的超时上限（秒）。</summary>
        public int ConnectTimeoutSeconds = 10;
        /// <summary>TLS 握手超时上限（秒），与 TCP 连接超时独立计算。</summary>
        public int TlsHandshakeTimeoutSeconds = 10;
        /// <summary>单个协议帧允许的最大字节数；默认 1 MiB，用于阻止恶意或损坏长度头触发超大分配。</summary>
        public int MaxMessageSize = 1 << 20;
        /// <summary>发送队列最大包数量。达到上限时 Send 立即失败，由上层决定限流、断线或重试。</summary>
        public int MaxSendQueuePackets = 256;
        /// <summary>TLS 客户端选项；为 null 或 Enabled=false 时使用明文 TCP，仅应在受控开发环境启用。</summary>
        public TlsClientOptions Tls;

        /// <summary>当前是否存在已激活且尚未关闭的 TCP/TLS 传输。</summary>
        public bool IsConnected => _isConnected;
        /// <summary>当前连接世代；每次成功激活新传输后原子递增。</summary>
        public int ConnectionEpoch => Volatile.Read(ref _connectionEpoch);

        /// <summary>TCP/TLS 传输已建立事件；可能从连接调用的延续线程触发，上层不得假定是 Unity 主线程。</summary>
        public event Action OnConnected;
        /// <summary>携带新连接 Epoch 的建立事件，新代码应优先订阅该事件。</summary>
        public event Action<int> OnConnectedWithEpoch;
        /// <summary>当前传输关闭事件。主动断开与底层故障均可能触发一次。</summary>
        public event Action OnDisconnected;
        /// <summary>携带关闭连接 Epoch 的断开事件，用于主线程忽略排队期间已经过期的旧连接状态。</summary>
        public event Action<int> OnDisconnectedWithEpoch;
        /// <summary>旧版无 Epoch 收包事件，仅用于兼容；每帧产生一次精确长度拷贝，订阅方获得数组所有权。</summary>
        public event Action<byte[]> OnReceive;
        /// <summary>
        /// 携带 ConnectionEpoch 的完整协议帧事件（epoch, buffer, frameLength）。
        /// <para>
        /// buffer 租自 ArrayPool，实际长度可能大于 frameLength，且<b>仅在回调执行期间有效</b>：
        /// 回调返回后缓冲区立即归还池并被复用，订阅方严禁持有引用，跨线程或延迟消费必须先按 frameLength 拷贝。
        /// </para>
        /// </summary>
        public event Action<int, byte[], int> OnReceiveWithEpoch;
        /// <summary>传输错误事件，可能由后台线程触发；订阅方必须切回主线程后再操作 Unity UI。</summary>
        public event Action<string> OnError;
        /// <summary>携带故障连接 Epoch 的错误事件，用于重连后过滤旧线程延迟上报。</summary>
        public event Action<int, string> OnErrorWithEpoch;

        /// <summary>
        /// 异步建立到目标主机的传输连接。域名会解析全部可用地址，优先尝试 IPv6，再回退 IPv4；
        /// 每个 TCP 尝试和 TLS 握手均有独立超时，并在调用方 CancellationToken 取消时终止连接流程。
        /// </summary>
        /// <param name="host">服务器域名、IPv6 字面量或 IPv4 字面量。</param>
        /// <param name="port">1～65535 范围内的 TCP 端口。</param>
        /// <param name="cancellationToken">场景退出、应用暂停或新连接覆盖旧连接时的取消令牌。</param>
        public async UniTask ConnectAsync(string host, int port, CancellationToken cancellationToken = default)
        {
            await _connectGate.WaitAsync(cancellationToken);
            try
            {
                if (IsConnected) return;
                if (string.IsNullOrWhiteSpace(host)) throw new ArgumentException("连接主机不能为空。", nameof(host));
                if (port <= 0 || port > 65535) throw new ArgumentOutOfRangeException(nameof(port));

                IPAddress[] addresses = await ResolveAddressesAsync(host, cancellationToken);
                Exception lastError = null;
                foreach (IPAddress address in OrderAddresses(addresses))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    Socket socket = null;
                    try
                    {
                        socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp)
                        {
                            NoDelay = true,
                        };
                        await AwaitWithTimeout(
                            socket.ConnectAsync(new IPEndPoint(address, port)),
                            ConnectTimeoutSeconds,
                            cancellationToken,
                            $"TCP 连接超时：{address}:{port}");

                        Stream stream = await CreateTransportStreamAsync(socket, host, cancellationToken);
                        ActivateConnection(socket, stream, host, port);
                        socket = null;
                        return;
                    }
                    catch (Exception ex)
                    {
                        lastError = ex;
                        try { socket?.Dispose(); } catch { }
                    }
                }

                throw new SocketExceptionWithInner(
                    $"所有解析地址均连接失败：{host}:{port}",
                    lastError);
            }
            catch (Exception ex)
            {
                string error = $"连接失败：{ex.Message}";
                OnErrorWithEpoch?.Invoke(ConnectionEpoch, error);
                OnError?.Invoke(error);
                throw;
            }
            finally
            {
                _connectGate.Release();
            }
        }

        /// <summary>
        /// 同步连接兼容入口。Unity 主线程新代码应优先使用 ConnectAsync，避免同步等待造成卡顿或死锁。
        /// </summary>
        public void Connect(string host, int port)
        {
            ConnectAsync(host, port).GetAwaiter().GetResult();
        }

        /// <summary>
        /// 幂等关闭当前连接：先在锁内撤销共享状态，再完成发送队列、关闭 Stream/Socket，使阻塞 I/O 退出，
        /// 最后有限等待后台线程结束。不得在持有状态锁时 Join，避免线程退出路径反向获取锁导致死锁。
        /// </summary>
        public void Disconnect()
        {
            Socket socket;
            Stream stream;
            BlockingCollection<byte[]> queue;
            Thread receiveThread;
            Thread sendThread;
            bool notify;
            int epoch = ConnectionEpoch;

            lock (_stateLock)
            {
                notify = _isConnected || _socket != null || _stream != null;
                if (!notify) return;

                _isConnected = false;
                socket = _socket;
                stream = _stream;
                queue = _sendQueue;
                receiveThread = _receiveThread;
                sendThread = _sendThread;
                _socket = null;
                _stream = null;
                _sendQueue = null;
                _receiveThread = null;
                _sendThread = null;
            }

            try { queue?.CompleteAdding(); } catch { }
            try { stream?.Dispose(); } catch { }
            try { socket?.Shutdown(SocketShutdown.Both); } catch { }
            try { socket?.Dispose(); } catch { }

            JoinThread(receiveThread);
            JoinThread(sendThread);
            try { queue?.Dispose(); } catch { }

            if (notify)
            {
                OnDisconnectedWithEpoch?.Invoke(epoch);
                OnDisconnected?.Invoke();
            }
        }

        /// <summary>
        /// 尝试把一个已完成编码的协议包加入有界发送队列。未连接、队列关闭或队列达到容量时立即返回 false。
        /// 调用方必须处理 false 并结束对应请求，禁止把背压失败误认为消息已经送达网络。
        /// </summary>
        /// <param name="data">不可为空的完整协议包。入队后调用方不得再修改其内容。</param>
        public bool Send(byte[] data)
        {
            if (data == null || data.Length == 0) return false;
            BlockingCollection<byte[]> queue = _sendQueue;
            if (!_isConnected || queue == null || queue.IsAddingCompleted) return false;

            try
            {
                if (queue.TryAdd(data, millisecondsTimeout: 0))
                    return true;

                string error = $"发送队列已满，触发背压：capacity={Math.Max(1, MaxSendQueuePackets)}";
                OnErrorWithEpoch?.Invoke(ConnectionEpoch, error);
                OnError?.Invoke(error);
                return false;
            }
            catch (ObjectDisposedException) { return false; }
            catch (InvalidOperationException) { return false; }
        }

        /// <summary>解析主机地址；字面量 IP 直接返回，域名解析受连接超时与取消令牌约束。</summary>
        private async Task<IPAddress[]> ResolveAddressesAsync(string host, CancellationToken cancellationToken)
        {
            if (IPAddress.TryParse(host, out IPAddress literal))
                return new[] { literal };

            Task<IPAddress[]> resolveTask = Dns.GetHostAddressesAsync(host);
            return await AwaitWithTimeout(
                resolveTask,
                ConnectTimeoutSeconds,
                cancellationToken,
                $"DNS 解析超时：{host}");
        }

        /// <summary>对解析结果去重并优先 IPv6、随后 IPv4，以适配 IPv6-only、NAT64 与双栈移动网络。</summary>
        internal static IEnumerable<IPAddress> OrderAddresses(IEnumerable<IPAddress> addresses)
        {
            // 保留 DNS 返回的全部可连接地址；优先 IPv6，但任何单地址失败后继续尝试 IPv4 或其他地址。
            return addresses
                .Where(address => address.AddressFamily == AddressFamily.InterNetworkV6 ||
                                  address.AddressFamily == AddressFamily.InterNetwork)
                .OrderBy(address => address.AddressFamily == AddressFamily.InterNetworkV6 ? 0 : 1)
                .Distinct();
        }

        /// <summary>为已连接 Socket 创建 NetworkStream；启用 TLS 时完成握手和证书验证后才返回可用传输流。</summary>
        private async Task<Stream> CreateTransportStreamAsync(
            Socket socket,
            string connectedHost,
            CancellationToken cancellationToken)
        {
            var networkStream = new NetworkStream(socket, ownsSocket: false);
            if (Tls == null || !Tls.Enabled) return networkStream;

            var sslStream = new SslStream(networkStream, false, ValidateServerCertificate);
            string targetHost = string.IsNullOrWhiteSpace(Tls.TargetHost) ? connectedHost : Tls.TargetHost;
            try
            {
                await AwaitWithTimeout(
                    sslStream.AuthenticateAsClientAsync(targetHost),
                    TlsHandshakeTimeoutSeconds,
                    cancellationToken,
                    $"TLS 握手超时：{targetHost}");
                return sslStream;
            }
            catch
            {
                sslStream.Dispose();
                throw;
            }
        }

        /// <summary>
        /// 验证服务端证书。配置 SHA-256 Pin 时对证书原始 DER 做固定时间摘要比较；未配置 Pin 时要求系统证书链和主机名校验全部通过。
        /// 正式项目应在配置层支持新旧 Pin 并行轮换，避免证书更换造成全量客户端断连。
        /// </summary>
        private bool ValidateServerCertificate(
            object sender,
            X509Certificate certificate,
            X509Chain chain,
            SslPolicyErrors sslPolicyErrors)
        {
            bool systemTrustValid = sslPolicyErrors == SslPolicyErrors.None;
            var configuredPins = new List<string>();
            AddNormalizedPin(configuredPins, Tls?.CertSha256);
            if (Tls?.CertSha256Pins != null)
            {
                foreach (string pin in Tls.CertSha256Pins)
                    AddNormalizedPin(configuredPins, pin);
            }

            if (configuredPins.Count == 0)
                return systemTrustValid;
            if (certificate == null)
                return false;

            string actual;
            using (SHA256 sha256 = SHA256.Create())
            {
                actual = BitConverter.ToString(sha256.ComputeHash(certificate.GetRawCertData()))
                    .Replace("-", string.Empty)
                    .ToUpperInvariant();
            }

            bool pinMatched = configuredPins.Any(pin => FixedTimeEquals(actual, pin));
            if (!pinMatched)
                return false;

            return systemTrustValid || (Tls?.AllowPinnedCertificateWithoutSystemTrust ?? false);
        }

        /// <summary>
        /// 规范化并收集单个 SHA-256 Pin；任何非 64 位十六进制配置都会加入不可匹配哨兵，使握手失败关闭。
        /// </summary>
        private static void AddNormalizedPin(List<string> pins, string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return;
            string normalized = NormalizeFingerprint(value);
            bool valid = normalized.Length == 64 && normalized.All(c =>
                c >= '0' && c <= '9' || c >= 'A' && c <= 'F');
            pins.Add(valid ? normalized : "INVALID_PIN_CONFIGURATION");
        }

        /// <summary>原子激活已完成连接和握手的传输，创建有界发送队列、收发线程并递增 ConnectionEpoch。</summary>
        private void ActivateConnection(Socket socket, Stream stream, string host, int port)
        {
            int epoch = Interlocked.Increment(ref _connectionEpoch);
            var queue = new BlockingCollection<byte[]>(
                new ConcurrentQueue<byte[]>(),
                Math.Max(1, MaxSendQueuePackets));

            lock (_stateLock)
            {
                _socket = socket;
                _stream = stream;
                _sendQueue = queue;
                _isConnected = true;
                _receiveThread = new Thread(() => ReceiveThreadFunc(epoch, stream))
                {
                    IsBackground = true,
                    Name = $"TcpClient_Receive_{epoch}",
                };
                _sendThread = new Thread(() => SendThreadFunc(epoch, stream, queue))
                {
                    IsBackground = true,
                    Name = $"TcpClient_Send_{epoch}",
                };
                _receiveThread.Start();
                _sendThread.Start();
            }

            GameLog.Log($"已连接 {host}:{port}，epoch={epoch}，addressFamily={socket.AddressFamily}");
            OnConnectedWithEpoch?.Invoke(epoch);
            OnConnected?.Invoke();
        }

        /// <summary>后台发送线程：按队列顺序写入 TCP/TLS Stream；任何真实写入异常都会报告并关闭当前 Epoch 连接。</summary>
        private void SendThreadFunc(int epoch, Stream stream, BlockingCollection<byte[]> queue)
        {
            try
            {
                foreach (byte[] data in queue.GetConsumingEnumerable())
                {
                    if (!_isConnected || epoch != ConnectionEpoch) break;
                    stream.Write(data, 0, data.Length);
                }
            }
            catch (ObjectDisposedException) { }
            catch (InvalidOperationException) { }
            catch (Exception ex)
            {
                HandleTransportFailure(epoch, $"发送失败：{ex.Message}");
            }
            finally
            {
                // 发送线程一旦因真实 I/O 异常或传输流意外结束退出，当前连接已不再可靠，必须统一走断线清理与重连语义。
                if (_isConnected && epoch == ConnectionEpoch)
                    Disconnect();
            }
        }

        /// <summary>后台接收线程：持续读取字节流、累积并拆分完整协议帧；线程只处理创建时捕获的 Epoch。</summary>
        private void ReceiveThreadFunc(int epoch, Stream stream)
        {
            byte[] chunk = new byte[ReceiveChunkSize];
            byte[] buffer = new byte[InitialMessageBufferSize];
            int offset = 0;
            try
            {
                while (_isConnected && epoch == ConnectionEpoch)
                {
                    int bytesRead = stream.Read(chunk, 0, chunk.Length);
                    if (bytesRead <= 0) break;
                    EnsureCapacity(ref buffer, offset + bytesRead);
                    Buffer.BlockCopy(chunk, 0, buffer, offset, bytesRead);
                    offset += bytesRead;
                    if (!ProcessFrames(epoch, buffer, ref offset)) break;
                }
            }
            catch (ObjectDisposedException) { }
            catch (IOException ex)
            {
                HandleTransportFailure(epoch, $"接收失败：{ex.Message}");
            }
            catch (SocketException ex)
            {
                HandleTransportFailure(epoch, $"接收失败：{ex.Message}");
            }
            catch (Exception ex)
            {
                HandleTransportFailure(epoch, $"接收失败：{ex.Message}");
            }
            finally
            {
                if (_isConnected && epoch == ConnectionEpoch)
                    Disconnect();
            }
        }

        /// <summary>从累计缓冲区解析完整长度前缀帧，保留不足一帧的尾部字节供下次读取继续拼接。</summary>
        private bool ProcessFrames(int epoch, byte[] buffer, ref int offset)
        {
            int readOffset = 0;
            while (offset - readOffset >= MessagePacket.HeaderSize)
            {
                int messageLength = BitConverter.ToInt32(buffer, readOffset);
                if (messageLength < MessagePacket.HeaderSize || messageLength > MaxMessageSize)
                {
                    HandleTransportFailure(epoch, $"协议帧长度非法：{messageLength}");
                    return false;
                }
                if (offset - readOffset < messageLength) break;

                // 帧缓冲租自 ArrayPool：高频收包不再逐帧产生托管垃圾。所有权收在本方法内，
                // 回调同步执行完毕即归还；订阅方契约见 OnReceiveWithEpoch 文档。
                byte[] message = ArrayPool<byte>.Shared.Rent(messageLength);
                Buffer.BlockCopy(buffer, readOffset, message, 0, messageLength);
                readOffset += messageLength;
                try
                {
                    OnReceiveWithEpoch?.Invoke(epoch, message, messageLength);
                    if (OnReceive != null)
                    {
                        // 兼容事件按精确长度拷贝并转移所有权，只有存在订阅者时才付出这次分配。
                        byte[] legacyCopy = new byte[messageLength];
                        Buffer.BlockCopy(message, 0, legacyCopy, 0, messageLength);
                        OnReceive.Invoke(legacyCopy);
                    }
                }
                catch (Exception ex)
                {
                    GameLog.Error($"收包事件回调异常：{ex}");
                }
                finally
                {
                    ArrayPool<byte>.Shared.Return(message);
                }
            }

            if (readOffset > 0)
            {
                int remaining = offset - readOffset;
                if (remaining > 0) Buffer.BlockCopy(buffer, readOffset, buffer, 0, remaining);
                offset = remaining;
            }
            return true;
        }

        /// <summary>仅向上报告仍属于当前 Epoch 的传输故障，旧线程错误不得污染重连后的新连接。</summary>
        private void HandleTransportFailure(int epoch, string message)
        {
            if (epoch != ConnectionEpoch) return;
            OnErrorWithEpoch?.Invoke(epoch, message);
            OnError?.Invoke(message);
        }

        private static void EnsureCapacity(ref byte[] buffer, int required)
        {
            if (required <= buffer.Length) return;
            int size = buffer.Length;
            while (size < required) size <<= 1;
            Array.Resize(ref buffer, size);
        }

        private static void JoinThread(Thread thread)
        {
            if (thread != null && thread != Thread.CurrentThread && thread.IsAlive)
                thread.Join(1000);
        }

        /// <summary>等待 Task 完成，并同时施加超时和 CancellationToken；超时只改变等待结果，调用方负责关闭底层资源。</summary>
        private static async Task AwaitWithTimeout(
            Task task,
            int timeoutSeconds,
            CancellationToken cancellationToken,
            string timeoutMessage)
        {
            Task delay = Task.Delay(TimeSpan.FromSeconds(Math.Max(1, timeoutSeconds)), cancellationToken);
            Task completed = await Task.WhenAny(task, delay);
            if (completed != task)
            {
                cancellationToken.ThrowIfCancellationRequested();
                throw new TimeoutException(timeoutMessage);
            }
            await task;
        }

        /// <summary>带返回值 Task 的超时等待重载，完成后返回原任务结果。</summary>
        private static async Task<T> AwaitWithTimeout<T>(
            Task<T> task,
            int timeoutSeconds,
            CancellationToken cancellationToken,
            string timeoutMessage)
        {
            await AwaitWithTimeout((Task)task, timeoutSeconds, cancellationToken, timeoutMessage);
            return await task;
        }

        private static string NormalizeFingerprint(string value) =>
            string.IsNullOrWhiteSpace(value)
                ? string.Empty
                : value.Replace(":", string.Empty).Replace(" ", string.Empty).ToUpperInvariant();

        private static bool FixedTimeEquals(string left, string right)
        {
            if (left == null || right == null) return false;
            int diff = left.Length ^ right.Length;
            int count = Math.Min(left.Length, right.Length);
            for (int i = 0; i < count; i++) diff |= left[i] ^ right[i];
            return diff == 0;
        }

        private sealed class SocketExceptionWithInner : IOException
        {
            public SocketExceptionWithInner(string message, Exception inner) : base(message, inner) { }
        }
    }
}
