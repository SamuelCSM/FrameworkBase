using System;
using System.Collections.Concurrent;
using System.IO;
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
    /// TCP客户端
    /// 负责底层TCP连接和数据收发。
    /// 收发各跑一个后台线程：接收线程负责拆帧并向上派发，发送线程消费发送队列，
    /// 因此公开的 <see cref="Send"/> 不会阻塞调用方（主线程），避免对端窗口打满时卡帧。
    /// </summary>
    public class TcpClient
    {
        // 接收线程单次 socket 读取的临时缓冲。
        private const int ReceiveChunkSize = 8192;

        // 消息组装缓冲的初始容量（按需翻倍增长，上限受 MaxMessageSize 约束）。
        private const int InitialMessageBufferSize = 16384;

        private Socket _socket;

        // 传输流：明文为 NetworkStream，TLS 为已完成握手的 SslStream。收发线程只面向流，不关心加密细节。
        private Stream _stream;

        private Thread _receiveThread;
        private Thread _sendThread;

        // 跨线程读写的连接标志，必须 volatile 保证弱内存模型（ARM 真机）下的可见性。
        private volatile bool _isConnected;

        // 发送队列：调用方仅入队，由发送线程消费，调用方不阻塞。
        private BlockingCollection<byte[]> _sendQueue;

        // Disconnect 幂等保护，避免收/发线程并发触发重复清理。
        private readonly object _disconnectLock = new object();

        private readonly byte[] _receiveChunk = new byte[ReceiveChunkSize];

        // 消息组装缓冲（可增长），用于把 socket 分片拼成完整帧。
        private byte[] _messageBuffer = new byte[InitialMessageBufferSize];
        private int _messageBufferOffset;

        /// <summary>
        /// 连接超时秒数（默认 10s）。
        /// 超时后抛出 TimeoutException，触发 NetworkManager 的重连逻辑。
        /// </summary>
        public int ConnectTimeoutSeconds = 10;

        /// <summary>
        /// 单条消息最大字节数（含 4 字节长度头）。默认 1MB。
        /// 收到声明长度超过此值的帧视为协议异常，主动断开以触发上层重连/重建快照，
        /// 而不是静默清空缓冲区导致后续帧错位。可按战斗快照等最大包体调整。
        /// </summary>
        public int MaxMessageSize = 1 << 20;

        /// <summary>
        /// TLS 配置；null 或 <see cref="TlsClientOptions.Enabled"/>=false 时走明文 TCP。
        /// 由组合根在连接前设置（从 AppConfig 装配），连接期间不可变更。
        /// </summary>
        public TlsClientOptions Tls;

        /// <summary>
        /// 是否已连接
        /// </summary>
        public bool IsConnected => _isConnected && _socket != null && _socket.Connected;

        /// <summary>
        /// 连接成功事件
        /// </summary>
        public event Action OnConnected;

        /// <summary>
        /// 断开连接事件
        /// </summary>
        public event Action OnDisconnected;

        /// <summary>
        /// 接收到数据事件（在接收线程触发，订阅方需自行切回主线程）
        /// </summary>
        public event Action<byte[]> OnReceive;

        /// <summary>
        /// 错误事件
        /// </summary>
        public event Action<string> OnError;

        /// <summary>
        /// 异步连接到服务器
        /// </summary>
        /// <param name="host">服务器地址</param>
        /// <param name="port">服务器端口</param>
        public async UniTask ConnectAsync(string host, int port)
        {
            if (IsConnected)
            {
                Logger.Warning("已经连接到服务器，无需重复连接");
                return;
            }

            try
            {
                // 创建Socket
                _socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
                _socket.NoDelay = true; // 禁用Nagle算法，减少延迟

                // 带超时的异步连接
                var connectTask = _socket.ConnectAsync(host, port);
                var timeoutTask = Task.Delay(TimeSpan.FromSeconds(ConnectTimeoutSeconds));
                var completed   = await Task.WhenAny(connectTask, timeoutTask);

                if (completed == timeoutTask)
                {
                    _socket.Close();
                    _socket = null;
                    throw new TimeoutException($"连接超时 ({ConnectTimeoutSeconds}s)");
                }

                // 如果 connectTask 内部有异常，在此重新抛出
                await connectTask;

                OnConnectedInternal(host, port);
            }
            catch (Exception ex)
            {
                _isConnected = false;
                Logger.Error($"连接服务器失败: {ex.Message}");
                OnError?.Invoke($"连接失败: {ex.Message}");
                throw;
            }
        }

        /// <summary>
        /// 同步连接到服务器（阻塞）
        /// </summary>
        /// <param name="host">服务器地址</param>
        /// <param name="port">服务器端口</param>
        public void Connect(string host, int port)
        {
            if (IsConnected)
            {
                Logger.Warning("已经连接到服务器，无需重复连接");
                return;
            }

            try
            {
                // 创建Socket
                _socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
                _socket.NoDelay = true;

                // 同步连接
                _socket.Connect(host, port);

                OnConnectedInternal(host, port);
            }
            catch (Exception ex)
            {
                _isConnected = false;
                Logger.Error($"连接服务器失败: {ex.Message}");
                OnError?.Invoke($"连接失败: {ex.Message}");
                throw;
            }
        }

        /// <summary>
        /// 连接建立成功后的统一收尾：建立传输流（TLS 模式先握手）、重置缓冲、启动收发线程、触发事件。
        /// 握手失败时关闭 socket 并向上抛出，由调用方按连接失败处理。
        /// </summary>
        private void OnConnectedInternal(string host, int port)
        {
            try
            {
                _stream = CreateTransportStream();
            }
            catch
            {
                // 握手失败：socket 已连上但传输不可用，必须就地关闭，避免半开连接泄漏。
                _socket?.Close();
                _socket = null;
                throw;
            }

            _isConnected = true;
            _messageBufferOffset = 0;
            Logger.Log($"成功连接到服务器 {host}:{port}" + (Tls is { Enabled: true } ? "（TLS 已启用）" : string.Empty));

            // 启动收发线程
            _sendQueue = new BlockingCollection<byte[]>();
            StartReceiveThread();
            StartSendThread();

            // 触发连接成功事件
            OnConnected?.Invoke();
        }

        /// <summary>
        /// 建立传输流：明文直接包装网络流；TLS 模式执行客户端握手并校验服务器证书。
        /// </summary>
        /// <returns>可直接收发协议字节的传输流。</returns>
        private Stream CreateTransportStream()
        {
            // ownsSocket=false：socket 生命周期仍由本类的 Disconnect 统一管理。
            var networkStream = new NetworkStream(_socket, ownsSocket: false);
            if (Tls == null || !Tls.Enabled)
            {
                return networkStream;
            }

            // leaveInnerStreamOpen=false：释放 SslStream 时连带释放内层网络流。
            var sslStream = new SslStream(networkStream, false, ValidateServerCertificate);
            try
            {
                // 同步握手（约 1 RTT + 加解密开销）：仅发生在连接/重连时，不在帧循环内。
                sslStream.AuthenticateAsClient(Tls.TargetHost);
                return sslStream;
            }
            catch
            {
                sslStream.Dispose();
                throw;
            }
        }

        /// <summary>
        /// 服务器证书校验：标准链校验通过 或 SHA-256 指纹匹配（自签名证书走指纹固定）二者其一放行。
        /// 指纹未配置且链校验失败时拒绝——TLS 开启后绝不静默接受不可信证书。
        /// </summary>
        private bool ValidateServerCertificate(
            object sender, X509Certificate certificate, X509Chain chain, SslPolicyErrors sslPolicyErrors)
        {
            // CA 签发证书的正常路径：链校验通过直接放行。
            if (sslPolicyErrors == SslPolicyErrors.None)
            {
                return true;
            }

            if (certificate == null || Tls == null || string.IsNullOrEmpty(Tls.CertSha256))
            {
                Logger.Error($"服务器证书校验失败（{sslPolicyErrors}）且未配置指纹固定，拒绝连接");
                return false;
            }

            string actual;
            using (var sha256 = SHA256.Create())
            {
                actual = BitConverter.ToString(sha256.ComputeHash(certificate.GetRawCertData()))
                    .Replace("-", string.Empty);
            }

            // 期望指纹允许冒号/空格分隔与任意大小写，规整后逐字比较。
            string expected = Tls.CertSha256.Replace(":", string.Empty).Replace(" ", string.Empty);
            bool matched = string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase);
            if (!matched)
            {
                Logger.Error($"服务器证书指纹不匹配，疑似中间人或证书未同步：实际={actual}");
            }

            return matched;
        }

        /// <summary>
        /// 断开连接（幂等，可由主线程或收/发线程任意一方触发）
        /// </summary>
        public void Disconnect()
        {
            lock (_disconnectLock)
            {
                if (!_isConnected && _socket == null)
                {
                    return;
                }

                _isConnected = false;

                // 停止发送线程：完成队列写入，使发送线程退出消费循环
                try
                {
                    _sendQueue?.CompleteAdding();
                }
                catch (ObjectDisposedException)
                {
                    // 队列已释放，忽略
                }

                // 先释放传输流：TLS 模式下 SslStream 须显式 Dispose 释放握手缓冲；
                // 同时会解除接收线程在 Read 上的阻塞（_isConnected 已置 false，线程按正常退出处理）。
                if (_stream != null)
                {
                    try
                    {
                        _stream.Dispose();
                    }
                    catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException)
                    {
                        // 释放失败只影响本连接清理，忽略。
                    }

                    _stream = null;
                }

                // 关闭Socket
                if (_socket != null)
                {
                    try
                    {
                        _socket.Shutdown(SocketShutdown.Both);
                    }
                    catch (Exception ex)
                    {
                        // Shutdown 在对端已断开时可能抛 SocketException，记录后继续 Close
                        Logger.Debug($"Socket Shutdown 异常（通常可忽略）: {ex.Message}");
                    }

                    _socket.Close();
                    _socket = null;
                }

                // 等待收发线程结束（不 Join 自己所在线程，避免死锁）
                JoinThread(ref _receiveThread);
                JoinThread(ref _sendThread);

                // 释放发送队列
                if (_sendQueue != null)
                {
                    _sendQueue.Dispose();
                    _sendQueue = null;
                }

                // 清空消息缓冲
                _messageBufferOffset = 0;

                Logger.Log("已断开与服务器的连接");
            }

            // 事件回调放在锁外触发，避免订阅方回调里再次进入网络逻辑造成死锁
            OnDisconnected?.Invoke();
        }

        /// <summary>
        /// 等待并清理线程引用；若当前正运行在该线程上则跳过 Join，避免自我等待死锁。
        /// </summary>
        private static void JoinThread(ref Thread thread)
        {
            Thread t = thread;
            if (t == null)
            {
                return;
            }

            if (t != Thread.CurrentThread && t.IsAlive)
            {
                t.Join(1000); // 最多等待1秒
            }

            thread = null;
        }

        /// <summary>
        /// 发送数据（线程安全，非阻塞）。
        /// 仅将数据入队，由发送线程异步写出，避免阻塞调用方（主线程）。
        /// </summary>
        /// <param name="data">要发送的数据</param>
        public void Send(byte[] data)
        {
            if (!IsConnected)
            {
                Logger.Error("未连接到服务器，无法发送数据");
                OnError?.Invoke("未连接到服务器");
                return;
            }

            if (data == null || data.Length == 0)
            {
                Logger.Warning("发送数据为空");
                return;
            }

            try
            {
                _sendQueue?.Add(data);
            }
            catch (Exception ex)
            {
                // 队列已 CompleteAdding/Dispose（断开中），属正常竞态，记录后忽略
                Logger.Debug($"入队发送数据失败（连接可能正在断开）: {ex.Message}");
            }
        }

        /// <summary>
        /// 启动接收线程
        /// </summary>
        private void StartReceiveThread()
        {
            _receiveThread = new Thread(ReceiveThreadFunc)
            {
                IsBackground = true,
                Name = "TcpClient_Receive"
            };
            _receiveThread.Start();
        }

        /// <summary>
        /// 启动发送线程
        /// </summary>
        private void StartSendThread()
        {
            _sendThread = new Thread(SendThreadFunc)
            {
                IsBackground = true,
                Name = "TcpClient_Send"
            };
            _sendThread.Start();
        }

        /// <summary>
        /// 发送线程函数：阻塞消费发送队列并写出，失败则触发断开。
        /// </summary>
        private void SendThreadFunc()
        {
            BlockingCollection<byte[]> queue = _sendQueue;
            try
            {
                // GetConsumingEnumerable 在 CompleteAdding 且队列清空后自然结束
                foreach (byte[] data in queue.GetConsumingEnumerable())
                {
                    Stream stream = _stream;
                    if (!_isConnected || stream == null)
                    {
                        break;
                    }

                    // Stream.Write 保证整包写出（内部处理部分写），TLS 模式由 SslStream 加密成帧。
                    stream.Write(data, 0, data.Length);
                }
            }
            catch (OperationCanceledException)
            {
                // 队列释放时的正常退出
            }
            catch (ObjectDisposedException)
            {
                // 队列/socket 释放时的正常退出
            }
            catch (Exception ex)
            {
                if (_isConnected)
                {
                    Logger.Error($"发送数据失败: {ex.Message}");
                    OnError?.Invoke($"发送失败: {ex.Message}");
                }
            }
            finally
            {
                // 发送异常意味着连接已不可用，触发断开（Disconnect 幂等且不会 Join 自己）
                if (_isConnected)
                {
                    Disconnect();
                }
            }
        }

        /// <summary>
        /// 接收线程函数
        /// </summary>
        private void ReceiveThreadFunc()
        {
            try
            {
                while (_isConnected && _stream != null)
                {
                    // 接收数据（面向传输流：明文/TLS 统一路径）
                    Stream stream = _stream;
                    if (stream == null)
                    {
                        break;
                    }

                    int bytesRead = stream.Read(_receiveChunk, 0, _receiveChunk.Length);

                    if (bytesRead <= 0)
                    {
                        // 连接已断开
                        Logger.Warning("服务器断开连接");
                        break;
                    }

                    // 确保组装缓冲容量足够，再把本次数据拼接进去
                    EnsureMessageBufferCapacity(_messageBufferOffset + bytesRead);
                    Array.Copy(_receiveChunk, 0, _messageBuffer, _messageBufferOffset, bytesRead);
                    _messageBufferOffset += bytesRead;

                    // 处理消息缓冲区中的完整消息；返回 false 表示遇到致命协议错误需断开
                    if (!ProcessMessageBuffer())
                    {
                        break;
                    }
                }
            }
            catch (SocketException ex)
            {
                if (_isConnected)
                {
                    Logger.Error($"接收数据时发生Socket异常: {ex.Message}");
                    OnError?.Invoke($"接收失败: {ex.Message}");
                }
            }
            catch (ObjectDisposedException)
            {
                // socket 在断开流程中被释放，属正常退出
            }
            catch (Exception ex)
            {
                if (_isConnected)
                {
                    Logger.Error($"接收数据时发生异常: {ex.Message}");
                    OnError?.Invoke($"接收失败: {ex.Message}");
                }
            }
            finally
            {
                // 接收线程结束，断开连接（Disconnect 幂等且不会 Join 自己）
                if (_isConnected)
                {
                    Disconnect();
                }
            }
        }

        /// <summary>
        /// 确保消息组装缓冲至少有 required 字节容量，不足则按需翻倍增长。
        /// 增长前的真正帧大小由 <see cref="ProcessMessageBuffer"/> 中的 MaxMessageSize 校验兜底，
        /// 因此缓冲不会无界增长。
        /// </summary>
        private void EnsureMessageBufferCapacity(int required)
        {
            if (required <= _messageBuffer.Length)
            {
                return;
            }

            int newSize = _messageBuffer.Length;
            while (newSize < required)
            {
                newSize <<= 1;
            }

            Array.Resize(ref _messageBuffer, newSize);
        }

        /// <summary>
        /// 处理消息缓冲区。
        /// 消息格式：Length(4字节, 含头) + MsgId(2字节) + Reserved(2字节) + Payload(N字节)，
        /// 长度头与服务端 ClientSession 保持一致（BitConverter 小端，长度含 4 字节头）。
        /// </summary>
        /// <returns>true 表示正常处理；false 表示遇到致命协议错误，调用方应断开连接。</returns>
        private bool ProcessMessageBuffer()
        {
            while (_messageBufferOffset >= 8) // 至少需要8字节的消息头
            {
                // 读取消息长度（前4字节）
                int messageLength = BitConverter.ToInt32(_messageBuffer, 0);

                // 验证消息长度：下限为消息头，上限为可配置的 MaxMessageSize
                if (messageLength < 8 || messageLength > MaxMessageSize)
                {
                    Logger.Error($"无效的消息长度: {messageLength}（上限 {MaxMessageSize}），断开连接以重建会话");
                    OnError?.Invoke($"协议错误：无效消息长度 {messageLength}");
                    // 无法安全地从错位的字节流中恢复帧边界，交由上层重连/重建快照
                    return false;
                }

                // 检查是否接收到完整消息
                if (_messageBufferOffset < messageLength)
                {
                    // 消息不完整，等待更多数据
                    break;
                }

                // 提取完整消息
                byte[] message = new byte[messageLength];
                Array.Copy(_messageBuffer, 0, message, 0, messageLength);

                // 移除已处理的消息（剩余字节前移）
                int remainingBytes = _messageBufferOffset - messageLength;
                if (remainingBytes > 0)
                {
                    Array.Copy(_messageBuffer, messageLength, _messageBuffer, 0, remainingBytes);
                }
                _messageBufferOffset = remainingBytes;

                // 触发接收事件
                try
                {
                    OnReceive?.Invoke(message);
                }
                catch (Exception ex)
                {
                    Logger.Error($"处理接收消息时发生异常: {ex.Message}");
                }
            }

            return true;
        }
    }
}
