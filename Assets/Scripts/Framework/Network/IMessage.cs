namespace Framework.Network
{
    /// <summary>
    /// 网络消息接口。
    /// 所有 Protobuf 消息都应该实现此接口。
    /// </summary>
    public interface IMessage
    {
        /// <summary>
        /// 获取主消息ID（模块ID）。
        /// </summary>
        /// <returns>主消息ID。</returns>
        byte GetMainId();

        /// <summary>
        /// 获取子消息ID（消息类型ID）。
        /// </summary>
        /// <returns>子消息ID。</returns>
        byte GetSubId();
    }

    /// <summary>
    /// 响应消息接口，提供统一的结果码访问。
    /// 用于全局错误码拦截器提取 ResultCode。
    /// </summary>
    public interface IResponse : IMessage
    {
        /// <summary>结果码。0 = 成功，非 0 = 错误。</summary>
        int ResultCode { get; }
    }

    /// <summary>
    /// 请求消息接口，声明该请求对应的响应类型。
    /// 用于 <see cref="NetworkManager.RequestAsync{TResp}(IRequest{TResp}, NetworkRequestConfig)"/>
    /// 的单泛型参数推断。
    /// </summary>
    /// <typeparam name="TResp">该请求对应的响应消息类型。</typeparam>
    public interface IRequest<TResp> : IMessage
        where TResp : class, IMessage, new()
    {
    }
}
