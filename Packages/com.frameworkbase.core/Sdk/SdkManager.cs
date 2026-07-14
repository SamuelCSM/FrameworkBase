using System;
using Cysharp.Threading.Tasks;
using Framework.Core;

namespace Framework.Sdk
{
    /// <summary>
    /// 平台 SDK 管理器：渠道实现的注册点与业务访问入口。
    ///
    /// 组合根流程：
    /// <code>
    /// // 业务启动早期（LaunchFlow 之前 / HotfixEntry 里），注册当前渠道实现：
    /// GameEntry.Sdk.RegisterProvider(new TapTapSdkProvider());   // 来自渠道扩展包
    /// await GameEntry.Sdk.InitializeAsync();
    ///
    /// // 之后业务侧统一经能力接口访问，不感知渠道：
    /// var login = await GameEntry.Sdk.Account.LoginAsync();
    /// </code>
    ///
    /// 未注册任何渠道时兜底 <see cref="MockSdkProvider"/>（开发期即插即用；
    /// 正式包走到 Mock 会有醒目告警日志）。框架主干不含任何渠道厂商代码。
    /// </summary>
    public class SdkManager : FrameworkComponent<SdkManager>
    {
        private ISdkProvider _provider;
        private bool _initialized;
        private bool _sessionInvalidatedHooked;

        /// <summary>
        /// 渠道会话失效（被顶号 / token 过期 / 家长控制强制下线）转发事件，参数为渠道原因串。
        /// 由 <see cref="InitializeAsync"/> 完成后从当前渠道实现转发；组合根订阅它统一执行登出清凭据。
        /// </summary>
        public event Action<string> OnSessionInvalidated;

        /// <summary>当前渠道名（未注册时为 mock）。</summary>
        public string ChannelName => ProviderOrMock().ChannelName;

        /// <summary>SDK 是否已完成初始化。</summary>
        public bool IsInitialized => _initialized;

        /// <summary>账号能力；当前渠道不支持时为 null（用前判空或查 <see cref="SupportsAccount"/>）。</summary>
        public ISdkAccountService Account => ProviderOrMock().Account;

        /// <summary>支付能力；当前渠道不支持时为 null。</summary>
        public ISdkPurchaseService Purchase => ProviderOrMock().Purchase;

        /// <summary>推送能力；当前渠道不支持时为 null。</summary>
        public ISdkPushService Push => ProviderOrMock().Push;

        /// <summary>隐私合规能力；当前渠道不支持时为 null。</summary>
        public ISdkPrivacyService Privacy => ProviderOrMock().Privacy;

        public bool SupportsAccount  => ProviderOrMock().Account  != null;
        public bool SupportsPurchase => ProviderOrMock().Purchase != null;
        public bool SupportsPush     => ProviderOrMock().Push     != null;
        public bool SupportsPrivacy  => ProviderOrMock().Privacy  != null;

        /// <summary>
        /// 注册渠道实现。必须在 <see cref="InitializeAsync"/> 之前调用；
        /// 重复注册以最后一次为准（记 Warning，便于发现组合根写重）。
        /// </summary>
        public void RegisterProvider(ISdkProvider provider)
        {
            if (provider == null)
            {
                GameLog.Error("[SdkManager] RegisterProvider 传入 null，忽略");
                return;
            }

            if (_initialized)
            {
                GameLog.Error($"[SdkManager] SDK 已初始化，拒绝切换渠道实现（当前 {_provider?.ChannelName} → 试图注册 {provider.ChannelName}）");
                return;
            }

            if (_provider != null)
                GameLog.Warning($"[SdkManager] 渠道实现被覆盖注册：{_provider.ChannelName} → {provider.ChannelName}");

            _provider = provider;
            GameLog.Log($"[SdkManager] 已注册渠道实现: {provider.ChannelName}");
        }

        /// <summary>
        /// 初始化当前渠道 SDK。可安全重入（已初始化直接返回成功）。
        /// 未注册渠道时落到 Mock 并按构建类型告警。
        /// </summary>
        public async UniTask<SdkResult> InitializeAsync()
        {
            if (_initialized)
                return SdkResult.Ok();

            ISdkProvider provider = ProviderOrMock();
            SdkResult result = await provider.InitializeAsync();
            _initialized = result.Success;

            if (result.Success)
            {
                // 渠道确定（初始化后不再切换）：挂接会话失效转发，供组合根统一登出清凭据。
                // 会话失效属账号能力（ISdkAccountService.OnSessionInvalidated）；渠道不支持账号能力时
                // Account 为 null，跳过挂接（此时也不会有会话失效可转发）。
                if (!_sessionInvalidatedHooked && provider.Account != null)
                {
                    provider.Account.OnSessionInvalidated += RaiseSessionInvalidated;
                    _sessionInvalidatedHooked = true;
                }
                GameLog.Log($"[SdkManager] SDK 初始化完成 channel={provider.ChannelName}");
                // 渠道名作为崩溃归因维度：分渠道排查崩溃（不同渠道 SDK / 机型分布差异大）。
                Framework.Core.Telemetry.CrashReporter.SetCustomKey("channel", provider.ChannelName);
            }
            else
                GameLog.Error($"[SdkManager] SDK 初始化失败 channel={provider.ChannelName} code={result.Code} msg={result.Message}");

            return result;
        }

        public override void OnShutdown()
        {
            // 仅当当初挂接过（_sessionInvalidatedHooked，蕴含挂接时 Account 非 null）才解绑；
            // 渠道初始化后不切换、Account 实例稳定，故此处解绑的是同一账号服务实例。
            if (_sessionInvalidatedHooked && _provider?.Account != null)
                _provider.Account.OnSessionInvalidated -= RaiseSessionInvalidated;
            _sessionInvalidatedHooked = false;
            _provider = null;
            _initialized = false;
        }

        private void RaiseSessionInvalidated(string reason)
        {
            GameLog.Warning($"[SdkManager] 渠道会话失效: {reason}");
            OnSessionInvalidated?.Invoke(reason ?? string.Empty);
        }

        /// <summary>取当前渠道实现；未注册时惰性创建 Mock 兜底（正式包告警）。</summary>
        private ISdkProvider ProviderOrMock()
        {
            if (_provider != null)
                return _provider;

#if !UNITY_EDITOR && !DEVELOPMENT_BUILD
            // 正式包走到 Mock 说明组合根漏注册渠道 —— 支付/登录都会是假实现，必须醒目暴露。
            GameLog.Error("[SdkManager] 正式构建未注册任何渠道实现，已兜底 Mock（登录/支付均为假实现，禁止上线）");
#endif
            _provider = new MockSdkProvider();
            return _provider;
        }
    }
}
