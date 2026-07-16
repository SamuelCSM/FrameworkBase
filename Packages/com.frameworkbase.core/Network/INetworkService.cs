using System;
using System.Threading;
using Cysharp.Threading.Tasks;
using Framework.Network;

namespace Framework
{
    /// <summary>
    /// 网络服务瘦接口：业务消费端最常用的连接控制与消息收发子集，
    /// 用作测试替身 / 解耦注入的缝（构造函数收本接口而非具体 <see cref="NetworkManager"/>）。
    /// <para>
    /// 刻意不求全：重连策略、心跳配置、协议日志开关等运维面只在具体类上
    /// （组合根经 <c>GameEntry.Network</c> 访问）——接口面越小，替身越好写、演进越自由。
    /// </para>
    /// </summary>
    public interface INetworkService
    {
        /// <summary>当前是否已连接。</summary>
        bool IsConnected { get; }

        /// <summary>连接建立（含重连成功）后触发。</summary>
        event Action OnConnected;

        /// <summary>连接断开后触发。</summary>
        event Action OnDisconnected;

        /// <summary>连接服务器。</summary>
        UniTask ConnectAsync(string host, int port, CancellationToken cancellationToken = default);

        /// <summary>手动触发重连（UI 失败按钮回调用）。</summary>
        UniTask ReconnectAsync();

        /// <summary>主动断开（关闭自动重连）。</summary>
        void Disconnect();

        /// <summary>发送不需要匹配响应的单向消息（seqId = 0）。返回值必须用于处理发送背压。</summary>
        bool Notify<T>(T message) where T : class, INetMessage;

        /// <summary>发送请求并等待对应类型的响应（SeqId 配对）。超时 / 取消 / 未连接返回 null。</summary>
        UniTask<TResp> RequestAsync<TReq, TResp>(
            TReq request,
            NetworkRequestConfig config = null,
            CancellationToken cancellationToken = default)
            where TReq : class, INetMessage
            where TResp : class, INetMessage, new();

        /// <summary>发送请求并等待响应（单泛型版本，请求实现 <see cref="IRequest{TResp}"/> 时自动推断）。</summary>
        UniTask<TResp> RequestAsync<TResp>(
            IRequest<TResp> request,
            NetworkRequestConfig config = null,
            CancellationToken cancellationToken = default)
            where TResp : class, INetMessage, new();

        /// <summary>订阅类型化推送消息，协议号从消息类型自身读取。返回句柄用于释放订阅。</summary>
        MessageSubscription Subscribe<T>(Action<T> handler, int priority = 0)
            where T : class, INetMessage, new();
    }
}
