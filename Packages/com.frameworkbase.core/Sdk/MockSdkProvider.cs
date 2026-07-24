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
        private readonly MockAd _ad = new MockAd();
        private readonly MockCompliance _compliance = new MockCompliance();
        private readonly MockShare _share = new MockShare();

        public string ChannelName => "mock";

        public ISdkAccountService Account => _account;
        public ISdkPurchaseService Purchase => _purchase;
        public ISdkPushService Push => _push;
        public ISdkPrivacyService Privacy => _privacy;
        public ISdkAdService Ad => _ad;
        public ISdkComplianceService Compliance => _compliance;
        public ISdkShareService Share => _share;

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

        // ── 广告 ─────────────────────────────────────────────────────────────

        private sealed class MockAd : ISdkAdService
        {
            public UniTask<SdkResult> PreloadAsync(SdkAdType type, string placementId)
                => UniTask.FromResult(SdkResult.Ok());

            public bool IsReady(SdkAdType type, string placementId) => true;

            public UniTask<SdkResult<SdkAdShowResult>> ShowAsync(SdkAdType type, string placementId)
            {
                // 醒目告警：Mock 广告直接"看满"，正式包出现此日志即广告渠道未接入，激励发奖会被伪造。
                GameLog.Warning($"[MockSdkProvider] Mock 广告直接完成 type={type} placement={placementId}（假发奖，禁止上线）");
                var data = new SdkAdShowResult
                {
                    PlacementId = placementId,
                    Rewarded = type == SdkAdType.Rewarded, // 激励视频 mock 恒发奖
                    Skipped = false,
                };
                return UniTask.FromResult(SdkResult<SdkAdShowResult>.Ok(data));
            }
        }

        // ── 合规（实名 + 防沉迷）─────────────────────────────────────────────

        private sealed class MockCompliance : ISdkComplianceService
        {
            public event Action<SdkPlaytimeVerdict> OnPlaytimeVerdictChanged
            {
                add { }
                remove { }
            }

            // Mock 恒为已实名成年人、不限时——开发期不被防沉迷挡住；正式包接真实渠道后由其裁决。
            public UniTask<SdkResult<SdkRealNameStatus>> QueryRealNameAsync()
                => UniTask.FromResult(SdkResult<SdkRealNameStatus>.Ok(
                    new SdkRealNameStatus { State = SdkRealNameState.Adult, Age = 30 }));

            public UniTask<SdkResult<SdkRealNameStatus>> ShowRealNameAuthAsync()
            {
                GameLog.Log("[MockSdkProvider] Mock 实名认证（直接视为已实名成年人）");
                return UniTask.FromResult(SdkResult<SdkRealNameStatus>.Ok(
                    new SdkRealNameStatus { State = SdkRealNameState.Adult, Age = 30 }));
            }

            public UniTask<SdkResult<SdkPlaytimeVerdict>> QueryPlaytimeAsync()
                => UniTask.FromResult(SdkResult<SdkPlaytimeVerdict>.Ok(
                    new SdkPlaytimeVerdict { State = SdkPlaytimeState.Allowed, RemainingSeconds = -1 }));

            public UniTask<SdkResult> ReportPlaytimeHeartbeatAsync(int elapsedSeconds)
                => UniTask.FromResult(SdkResult.Ok());
        }

        // ── 分享 ─────────────────────────────────────────────────────────────

        private sealed class MockShare : ISdkShareService
        {
            public bool IsChannelAvailable(SdkShareChannel channel) => true;

            public UniTask<SdkResult> ShareAsync(SdkShareChannel channel, SdkShareContent content)
            {
                GameLog.Log($"[MockSdkProvider] Mock 分享 channel={channel} type={content?.Type}（no-op）");
                return UniTask.FromResult(SdkResult.Ok());
            }
        }
    }
}
