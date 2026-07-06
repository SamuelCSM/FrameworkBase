using System;
using Cysharp.Threading.Tasks;

namespace Framework.Sdk
{
    /// <summary>
    /// 平台 SDK 总入口契约。
    ///
    /// 定位：框架主干只定义能力接口与注册机制，<b>不含任何渠道厂商代码</b>；
    /// 具体渠道（官方包 / 各安卓渠道 / iOS）的实现放在各自的扩展包中，
    /// 由业务组合根在启动早期经 <see cref="SdkManager.RegisterProvider"/> 注册。
    ///
    /// 能力划分为四个子服务，渠道不支持的能力返回 null，业务侧用前判空
    /// （或经 <see cref="SdkManager"/> 的判定属性检查）。
    /// </summary>
    public interface ISdkProvider
    {
        /// <summary>渠道标识（如 "mock" / "taptap" / "appstore"），用于日志与埋点维度。</summary>
        string ChannelName { get; }

        /// <summary>
        /// 初始化 SDK（拉起渠道 SDK 自身的 init 流程）。
        /// 必须可安全重入：已初始化时直接返回成功。
        /// </summary>
        UniTask<SdkResult> InitializeAsync();

        /// <summary>账号能力；渠道不支持时为 null。</summary>
        ISdkAccountService Account { get; }

        /// <summary>支付能力；渠道不支持时为 null。</summary>
        ISdkPurchaseService Purchase { get; }

        /// <summary>推送能力；渠道不支持时为 null。</summary>
        ISdkPushService Push { get; }

        /// <summary>隐私合规能力；渠道不支持时为 null。</summary>
        ISdkPrivacyService Privacy { get; }
    }

    /// <summary>
    /// 渠道账号能力：登录 / 登出 / 会话失效通知。
    /// 注意与 <c>Framework.Core.Auth</c> 的分工——本接口产出<b>渠道凭证</b>
    /// （channelUserId + token），游戏侧会话由 AuthManager 拿凭证向游戏服换取。
    /// </summary>
    public interface ISdkAccountService
    {
        /// <summary>拉起渠道登录（渠道自己的 UI / 授权页）。</summary>
        UniTask<SdkResult<SdkLoginData>> LoginAsync();

        /// <summary>登出渠道账号。</summary>
        UniTask<SdkResult> LogoutAsync();

        /// <summary>
        /// 渠道会话失效（被顶号 / token 过期 / 家长控制强制下线）。
        /// 业务应订阅并回到登录流程。
        /// </summary>
        event Action<string> OnSessionInvalidated;
    }

    /// <summary>
    /// 渠道支付能力（IAP）。
    /// 约定流程：QueryProductsAsync → PurchaseAsync →（服务端验证发货后）ConfirmAsync。
    /// Confirm 前的订单渠道会在 RestoreAsync / 下次启动补单回调中重新给到。
    /// </summary>
    public interface ISdkPurchaseService
    {
        /// <summary>查询商品定价信息（本地化价格串来自渠道）。</summary>
        UniTask<SdkResult<SdkProductInfo[]>> QueryProductsAsync(string[] productIds);

        /// <summary>
        /// 拉起购买。
        /// </summary>
        /// <param name="productId">商品 ID（渠道后台配置）。</param>
        /// <param name="developerPayload">透传字段（如订单号），随收据回带用于服务端对账。</param>
        UniTask<SdkResult<SdkPurchaseData>> PurchaseAsync(string productId, string developerPayload);

        /// <summary>
        /// 确认订单（服务端验证收据并发货成功后调用；对应 Google consume / Apple finishTransaction）。
        /// 不调用则渠道会持续补单。
        /// </summary>
        UniTask<SdkResult> ConfirmAsync(string transactionId);

        /// <summary>拉取未确认订单（启动补单 / 恢复购买）。</summary>
        UniTask<SdkResult<SdkPurchaseData[]>> RestorePendingAsync();
    }

    /// <summary>渠道推送能力。</summary>
    public interface ISdkPushService
    {
        /// <summary>请求系统推送权限（iOS 弹窗 / Android 13+ 运行时权限）。</summary>
        UniTask<SdkResult<bool>> RequestPermissionAsync();

        /// <summary>获取设备推送 token（APNs / FCM / 厂商通道），供服务端定向推送。</summary>
        UniTask<SdkResult<string>> GetDeviceTokenAsync();
    }

    /// <summary>隐私合规能力（上架审核硬性要求，按地区法规差异由渠道实现兜住）。</summary>
    public interface ISdkPrivacyService
    {
        /// <summary>
        /// 请求跟踪授权（iOS ATT / 各法规区的个性化广告同意）。
        /// 返回 true 表示用户同意跟踪。
        /// </summary>
        UniTask<SdkResult<bool>> RequestTrackingConsentAsync();

        /// <summary>展示隐私协议 / 用户协议页（渠道内置或跳转 URL）。</summary>
        UniTask<SdkResult> ShowPrivacyPolicyAsync();
    }
}
