using System;
using Cysharp.Threading.Tasks;
using UnityEngine;

namespace Framework.Sdk
{
    /// <summary>
    /// Mock 渠道实现：Editor / 开发期 / 未注册真实渠道时的兜底。
    /// 全能力即时成功——登录发设备号访客凭证，支付直接吐假收据（并打醒目日志防止误上线）。
    /// </summary>
    public class MockSdkProvider : ISdkProvider
    {
        private readonly MockAccount _account = new MockAccount();
        private readonly MockPurchase _purchase = new MockPurchase();
        private readonly MockPush _push = new MockPush();
        private readonly MockPrivacy _privacy = new MockPrivacy();

        public string ChannelName => "mock";

        public ISdkAccountService Account => _account;
        public ISdkPurchaseService Purchase => _purchase;
        public ISdkPushService Push => _push;
        public ISdkPrivacyService Privacy => _privacy;

        public UniTask<SdkResult> InitializeAsync()
        {
            GameLog.Log("[MockSdkProvider] 初始化（Mock 渠道，仅限开发期）");
            return UniTask.FromResult(SdkResult.Ok());
        }

        // ── 账号 ─────────────────────────────────────────────────────────────

        private sealed class MockAccount : ISdkAccountService
        {
            public event Action<string> OnSessionInvalidated
            {
                add { }
                remove { }
            }

            public UniTask<SdkResult<SdkLoginData>> LoginAsync()
            {
                var data = new SdkLoginData
                {
                    ChannelUserId = $"mock_{SystemInfo.deviceUniqueIdentifier}",
                    Token = $"mock_token_{DateTime.UtcNow.Ticks}",
                    DisplayName = "MockUser",
                    RawJson = string.Empty
                };
                GameLog.Log($"[MockSdkProvider] Mock 登录成功 userId={data.ChannelUserId}");
                return UniTask.FromResult(SdkResult<SdkLoginData>.Ok(data));
            }

            public UniTask<SdkResult> LogoutAsync()
            {
                GameLog.Log("[MockSdkProvider] Mock 登出");
                return UniTask.FromResult(SdkResult.Ok());
            }
        }

        // ── 支付 ─────────────────────────────────────────────────────────────

        private sealed class MockPurchase : ISdkPurchaseService
        {
            public UniTask<SdkResult<SdkProductInfo[]>> QueryProductsAsync(string[] productIds)
            {
                productIds = productIds ?? Array.Empty<string>();
                var products = new SdkProductInfo[productIds.Length];
                for (int i = 0; i < productIds.Length; i++)
                {
                    products[i] = new SdkProductInfo
                    {
                        ProductId = productIds[i],
                        LocalizedPrice = "￥0.00 (mock)",
                        LocalizedTitle = productIds[i],
                        CurrencyCode = "CNY"
                    };
                }
                return UniTask.FromResult(SdkResult<SdkProductInfo[]>.Ok(products));
            }

            public UniTask<SdkResult<SdkPurchaseData>> PurchaseAsync(string productId, string developerPayload)
            {
                // 醒目告警：Mock 支付永远成功，正式包出现此日志即渠道接入配置错误。
                GameLog.Warning($"[MockSdkProvider] Mock 支付直接成功 productId={productId}（假收据，禁止上线）");
                var data = new SdkPurchaseData
                {
                    ProductId = productId,
                    TransactionId = $"mock_txn_{Guid.NewGuid():N}",
                    Receipt = "mock_receipt",
                    DeveloperPayload = developerPayload
                };
                return UniTask.FromResult(SdkResult<SdkPurchaseData>.Ok(data));
            }

            public UniTask<SdkResult> ConfirmAsync(string transactionId)
            {
                GameLog.Log($"[MockSdkProvider] Mock 确认订单 {transactionId}");
                return UniTask.FromResult(SdkResult.Ok());
            }

            public UniTask<SdkResult<SdkPurchaseData[]>> RestorePendingAsync()
            {
                return UniTask.FromResult(SdkResult<SdkPurchaseData[]>.Ok(Array.Empty<SdkPurchaseData>()));
            }
        }

        // ── 推送 ─────────────────────────────────────────────────────────────

        private sealed class MockPush : ISdkPushService
        {
            public UniTask<SdkResult<bool>> RequestPermissionAsync()
                => UniTask.FromResult(SdkResult<bool>.Ok(true));

            public UniTask<SdkResult<string>> GetDeviceTokenAsync()
                => UniTask.FromResult(SdkResult<string>.Ok("mock_push_token"));
        }

        // ── 隐私 ─────────────────────────────────────────────────────────────

        private sealed class MockPrivacy : ISdkPrivacyService
        {
            public UniTask<SdkResult<bool>> RequestTrackingConsentAsync()
                => UniTask.FromResult(SdkResult<bool>.Ok(true));

            public UniTask<SdkResult> ShowPrivacyPolicyAsync()
            {
                GameLog.Log("[MockSdkProvider] Mock 展示隐私协议（no-op）");
                return UniTask.FromResult(SdkResult.Ok());
            }
        }
    }
}
