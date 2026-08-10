namespace Framework.Network
{
    /// <summary>
    /// 请求-响应的终态分类（见 ADR-012）。
    /// <para>
    /// 这些原因的正确处置各不相同：超时与背压值得重试，取消绝不能重试，
    /// 非幂等被拒重试就是重复扣款，反序列化失败是本端 bug 而非网络问题。
    /// 全部塌缩成 null 时调用方只能猜，猜错的两个方向都有代价。
    /// </para>
    /// </summary>
    public enum NetworkResultStatus
    {
        /// <summary>拿到并成功反序列化了响应。</summary>
        Success = 0,

        /// <summary>未连接，且本次请求未开启离线排队。</summary>
        NotConnected = 1,

        /// <summary>离线排队被拒：请求未声明重放安全，或待发队列已满。重试前须先确认幂等性。</summary>
        QueueRejected = 2,

        /// <summary>调用方的取消令牌已触发。<b>不得重试</b>——调用方已经明确不要这个结果了。</summary>
        Canceled = 3,

        /// <summary>发送失败：发送队列背压或连接在发送瞬间断开。通常可在退避后重试。</summary>
        SendFailed = 4,

        /// <summary>等待响应超时。通常可重试，但对非幂等请求须由业务决定。</summary>
        Timeout = 5,

        /// <summary>被全局错误码拦截器消费（如登录失效已触发统一处置），调用方不应再自行处理。</summary>
        Intercepted = 6,

        /// <summary>收到了响应但反序列化失败。属本端协议不匹配，重试无用，应报错并排查版本。</summary>
        DeserializeFailed = 7,
    }

    /// <summary>
    /// 请求-响应结果（见 ADR-012）。<see cref="Value"/> 仅在 <see cref="NetworkResultStatus.Success"/> 时非空。
    /// <para>
    /// 判定是否重试由调用方按 <see cref="Status"/> 决定，框架不替它选：
    /// 重试策略与业务幂等性绑定，框架无从知晓某个请求重发一次是否安全。
    /// </para>
    /// <para>
    /// 新增失败类别时既有 <c>switch</c> 不会编译报错，故调用方应按 <see cref="IsSuccess"/> 判成功、
    /// 按具体 <see cref="Status"/> 判特例，不要写带兜底分支的穷举 <c>switch</c>。
    /// </para>
    /// </summary>
    /// <typeparam name="T">响应消息类型。</typeparam>
    public readonly struct NetworkResult<T> where T : class
    {
        /// <summary>本次请求的终态。</summary>
        public readonly NetworkResultStatus Status;

        /// <summary>响应消息；非成功终态时为 null。</summary>
        public readonly T Value;

        /// <summary>构造一个结果。一般由框架内部构造，调用方使用 <see cref="Ok"/> / <see cref="Fail"/>。</summary>
        /// <param name="status">终态。</param>
        /// <param name="value">响应消息，仅成功时给出。</param>
        public NetworkResult(NetworkResultStatus status, T value)
        {
            Status = status;
            Value = value;
        }

        /// <summary>
        /// 是否拿到了可用响应。同时要求 <see cref="Value"/> 非空——
        /// 避免"状态成功但响应为 null"的半成功状态被当作成功使用。
        /// </summary>
        public bool IsSuccess => Status == NetworkResultStatus.Success && Value != null;

        /// <summary>构造成功结果。</summary>
        /// <param name="value">响应消息。</param>
        /// <returns>成功结果；<paramref name="value"/> 为 null 时降级为反序列化失败。</returns>
        public static NetworkResult<T> Ok(T value)
            => value != null
                ? new NetworkResult<T>(NetworkResultStatus.Success, value)
                : new NetworkResult<T>(NetworkResultStatus.DeserializeFailed, null);

        /// <summary>构造失败结果。</summary>
        /// <param name="status">失败终态；传 Success 无意义，由调用点保证。</param>
        /// <returns>不带响应的失败结果。</returns>
        public static NetworkResult<T> Fail(NetworkResultStatus status)
            => new NetworkResult<T>(status, null);

        /// <summary>日志友好的文本，形如 <c>Timeout</c> 或 <c>Success(EchoResponse)</c>。</summary>
        /// <returns>终态描述。</returns>
        public override string ToString()
            => IsSuccess ? $"{Status}({typeof(T).Name})" : Status.ToString();
    }
}
