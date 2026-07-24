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

        /// <summary>广告未加载就绪（需先 Preload 再 Show）。</summary>
        AdNotReady = 7,

        /// <summary>广告无填充（广告平台当前无可展示广告，非错误，通常静默或走保底奖励）。</summary>
        AdNoFill = 8,

        /// <summary>激励视频未达发奖条件（用户提前关闭/跳过），不发奖。</summary>
        RewardNotEarned = 9,

        /// <summary>需先完成实名认证（未实名被拦）。</summary>
        RealNameRequired = 10,

        /// <summary>防沉迷时长限制中（宵禁时段 / 时长用尽），当前禁玩。</summary>
        PlaytimeRestricted = 11,

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

    /// <summary>广告类型。</summary>
    public enum SdkAdType
    {
        /// <summary>激励视频：看满时长发奖，展示结果 <see cref="SdkAdShowResult.Rewarded"/> 表示是否达成发奖条件。</summary>
        Rewarded = 0,

        /// <summary>插屏广告：全屏展示、无奖励（关卡间/结算页常用）。</summary>
        Interstitial = 1,
    }

    /// <summary>广告展示结果。</summary>
    public class SdkAdShowResult
    {
        /// <summary>广告位 ID。</summary>
        public string PlacementId;

        /// <summary>
        /// （激励视频）是否达成发奖条件（看满时长）。插屏恒 false。
        /// <b>仅供即时反馈</b>——真正发奖必须由服务端校验广告平台回调后到账，不得以本字段为发奖依据。
        /// </summary>
        public bool Rewarded;

        /// <summary>用户是否提前跳过/关闭。</summary>
        public bool Skipped;
    }

    /// <summary>实名认证状态。</summary>
    public enum SdkRealNameState
    {
        /// <summary>未知（未查询 / 渠道未返回）。</summary>
        Unknown = 0,

        /// <summary>未实名。</summary>
        NotAuthenticated = 1,

        /// <summary>已实名成年人。</summary>
        Adult = 2,

        /// <summary>已实名未成年人（受防沉迷时长/时段管控）。</summary>
        Minor = 3,
    }

    /// <summary>实名认证结果。</summary>
    public class SdkRealNameStatus
    {
        /// <summary>实名状态。</summary>
        public SdkRealNameState State;

        /// <summary>年龄（岁，0=未知）；未成年分档管控（如 &lt;8 / 8-16 / 16-18）可据此。</summary>
        public int Age;
    }

    /// <summary>防沉迷时长裁决状态。</summary>
    public enum SdkPlaytimeState
    {
        /// <summary>可正常游玩（成年人 / 未触发限制）。</summary>
        Allowed = 0,

        /// <summary>限时内可玩：<see cref="SdkPlaytimeVerdict.RemainingSeconds"/> 为本时段剩余可玩秒数。</summary>
        Restricted = 1,

        /// <summary>当前禁玩（宵禁时段 / 已用尽时长 / 未实名被拦）。</summary>
        Blocked = 2,
    }

    /// <summary>
    /// 防沉迷时长裁决。由渠道 / 游戏服依实名信息与法规计算——<b>框架不硬编码任何规则</b>
    /// （宵禁时段、时长上限随政策变），只原样透传裁决与合规文案。
    /// </summary>
    public class SdkPlaytimeVerdict
    {
        /// <summary>裁决状态。</summary>
        public SdkPlaytimeState State;

        /// <summary>本时段剩余可玩秒数；<see cref="SdkPlaytimeState.Allowed"/> 时为 -1（不限），Blocked 时为 0。</summary>
        public int RemainingSeconds = -1;

        /// <summary>需向玩家展示的合规文案（渠道 / 法规原文，框架原样透传，不自拟）。</summary>
        public string NoticeMessage;
    }
}
