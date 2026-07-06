namespace Framework.Sdk
{
    /// <summary>
    /// SDK 调用统一错误码。渠道原生错误码/消息放 <see cref="SdkResult.ChannelCode"/> 与
    /// <see cref="SdkResult.Message"/>，业务逻辑只对本枚举分支，不感知渠道差异。
    /// </summary>
    public enum SdkErrorCode
    {
        /// <summary>成功。</summary>
        Ok = 0,

        /// <summary>用户主动取消（登录取消 / 支付取消）——通常静默处理，不弹错误。</summary>
        UserCancelled = 1,

        /// <summary>网络错误——可提示重试。</summary>
        NetworkError = 2,

        /// <summary>SDK 未初始化或初始化失败。</summary>
        NotInitialized = 3,

        /// <summary>当前渠道不支持该能力。</summary>
        NotSupported = 4,

        /// <summary>支付被拒（余额不足 / 支付渠道拒绝 / 风控）。</summary>
        PaymentDeclined = 5,

        /// <summary>商品不存在或不可购买（后台未配置 / 已下架）。</summary>
        ProductUnavailable = 6,

        /// <summary>其余未归类错误——详情看 ChannelCode/Message。</summary>
        Unknown = 100
    }

    /// <summary>无载荷的 SDK 调用结果。</summary>
    public class SdkResult
    {
        /// <summary>是否成功（Code == Ok）。</summary>
        public bool Success => Code == SdkErrorCode.Ok;

        /// <summary>统一错误码。</summary>
        public SdkErrorCode Code { get; set; } = SdkErrorCode.Ok;

        /// <summary>渠道原生错误码（透传，用于排查与埋点，业务逻辑不要分支它）。</summary>
        public string ChannelCode { get; set; } = string.Empty;

        /// <summary>人读消息（渠道原文或框架描述）。</summary>
        public string Message { get; set; } = string.Empty;

        public static SdkResult Ok() => new SdkResult();

        public static SdkResult Fail(SdkErrorCode code, string message = "", string channelCode = "")
            => new SdkResult { Code = code, Message = message, ChannelCode = channelCode };
    }

    /// <summary>带载荷的 SDK 调用结果。失败时 <see cref="Data"/> 为 default。</summary>
    public class SdkResult<T> : SdkResult
    {
        /// <summary>成功时的载荷。</summary>
        public T Data { get; set; }

        public static SdkResult<T> Ok(T data) => new SdkResult<T> { Data = data };

        public new static SdkResult<T> Fail(SdkErrorCode code, string message = "", string channelCode = "")
            => new SdkResult<T> { Code = code, Message = message, ChannelCode = channelCode };
    }

    /// <summary>渠道登录产出的凭证（交给 AuthManager 向游戏服换游戏会话）。</summary>
    public class SdkLoginData
    {
        /// <summary>渠道用户唯一 ID。</summary>
        public string ChannelUserId;

        /// <summary>渠道会话凭证（token / code），服务端拿它向渠道验真。</summary>
        public string Token;

        /// <summary>展示名（可空）。</summary>
        public string DisplayName;

        /// <summary>渠道返回的原始 JSON（可空；服务端验真可能需要额外字段）。</summary>
        public string RawJson;
    }

    /// <summary>渠道商品信息（本地化定价来自渠道后台）。</summary>
    public class SdkProductInfo
    {
        /// <summary>商品 ID。</summary>
        public string ProductId;

        /// <summary>本地化价格串（含货币符号，直接显示）。</summary>
        public string LocalizedPrice;

        /// <summary>本地化标题。</summary>
        public string LocalizedTitle;

        /// <summary>ISO 货币码（如 CNY / USD）。</summary>
        public string CurrencyCode;
    }

    /// <summary>渠道支付产出（收据交服务端验证，验证发货后须 Confirm）。</summary>
    public class SdkPurchaseData
    {
        /// <summary>商品 ID。</summary>
        public string ProductId;

        /// <summary>渠道交易 ID（Confirm / 对账用）。</summary>
        public string TransactionId;

        /// <summary>收据 / 票据（服务端向渠道验真的凭证）。</summary>
        public string Receipt;

        /// <summary>购买时透传的 developerPayload（回带对账）。</summary>
        public string DeveloperPayload;
    }
}
