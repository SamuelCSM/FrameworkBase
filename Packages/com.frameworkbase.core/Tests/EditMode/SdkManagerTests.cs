using Cysharp.Threading.Tasks;
using Framework.Sdk;
using NUnit.Framework;

namespace Framework.Tests
{
    /// <summary>
    /// SdkManager 单元测试：注册/兜底/初始化重入规则与 Mock 能力往返。
    /// Mock 的 UniTask 全部同步完成，可安全在 EditMode 直接取结果。
    /// </summary>
    public class SdkManagerTests
    {
        private SdkManager _sdk;

        [SetUp]
        public void SetUp()
        {
            _sdk = new SdkManager();
            _sdk.OnInit();
        }

        [TearDown]
        public void TearDown()
        {
            _sdk.OnShutdown();
        }

        /// <summary>同步取 UniTask 结果（Mock 实现全部同步完成）。</summary>
        private static T Wait<T>(UniTask<T> task) => task.GetAwaiter().GetResult();

        // ── 注册与兜底 ───────────────────────────────────────────────────────

        [Test]
        public void 未注册渠道_自动兜底Mock()
        {
            Assert.AreEqual("mock", _sdk.ChannelName);
            Assert.IsTrue(_sdk.SupportsAccount);
            Assert.IsTrue(_sdk.SupportsPurchase);
        }

        [Test]
        public void 注册渠道后_以注册实现为准()
        {
            _sdk.RegisterProvider(new FakeChannelProvider());
            Assert.AreEqual("fake_channel", _sdk.ChannelName);
            Assert.IsFalse(_sdk.SupportsPurchase, "该假渠道不支持支付，能力应为 null");
        }

        [Test]
        public void 初始化后_拒绝切换渠道实现()
        {
            _sdk.RegisterProvider(new FakeChannelProvider());
            Assert.IsTrue(Wait(_sdk.InitializeAsync()).Success);

            UnityEngine.TestTools.LogAssert.Expect(
                UnityEngine.LogType.Error,
                new System.Text.RegularExpressions.Regex(@"\[SdkManager\] SDK 已初始化，拒绝切换渠道实现"));
            _sdk.RegisterProvider(new MockSdkProvider());

            Assert.AreEqual("fake_channel", _sdk.ChannelName, "初始化后注册应被拒绝");
        }

        [Test]
        public void 初始化可重入_第二次直接成功()
        {
            Assert.IsTrue(Wait(_sdk.InitializeAsync()).Success);
            Assert.IsTrue(Wait(_sdk.InitializeAsync()).Success);
            Assert.IsTrue(_sdk.IsInitialized);
        }

        // ── Mock 能力往返 ────────────────────────────────────────────────────

        [Test]
        public void Mock登录_产出渠道凭证()
        {
            Wait(_sdk.InitializeAsync());
            var result = Wait(_sdk.Account.LoginAsync());

            Assert.IsTrue(result.Success);
            Assert.IsNotEmpty(result.Data.ChannelUserId);
            Assert.IsNotEmpty(result.Data.Token);
        }

        [Test]
        public void Mock支付_全流程往返()
        {
            Wait(_sdk.InitializeAsync());

            var products = Wait(_sdk.Purchase.QueryProductsAsync(new[] { "gem_60", "gem_300" }));
            Assert.IsTrue(products.Success);
            Assert.AreEqual(2, products.Data.Length);

            var buy = Wait(_sdk.Purchase.PurchaseAsync("gem_60", "order_123"));
            Assert.IsTrue(buy.Success);
            Assert.AreEqual("gem_60", buy.Data.ProductId);
            Assert.AreEqual("order_123", buy.Data.DeveloperPayload, "developerPayload 必须原样回带");
            Assert.IsNotEmpty(buy.Data.TransactionId);

            Assert.IsTrue(Wait(_sdk.Purchase.ConfirmAsync(buy.Data.TransactionId)).Success);

            var pending = Wait(_sdk.Purchase.RestorePendingAsync());
            Assert.IsTrue(pending.Success);
            Assert.AreEqual(0, pending.Data.Length);
        }

        [Test]
        public void SdkResult_错误构造与判定()
        {
            var fail = SdkResult<SdkLoginData>.Fail(SdkErrorCode.UserCancelled, "user closed", "10001");
            Assert.IsFalse(fail.Success);
            Assert.AreEqual(SdkErrorCode.UserCancelled, fail.Code);
            Assert.AreEqual("10001", fail.ChannelCode);
            Assert.IsNull(fail.Data);

            Assert.IsTrue(SdkResult.Ok().Success);
        }

        // ── 测试用假渠道（只支持账号，不支持支付/推送/隐私）─────────────────

        private sealed class FakeChannelProvider : ISdkProvider
        {
            public string ChannelName => "fake_channel";
            public ISdkAccountService Account { get; } = new FakeAccount();
            public ISdkPurchaseService Purchase => null;
            public ISdkPushService Push => null;
            public ISdkPrivacyService Privacy => null;
            public ISdkAdService Ad => null;
            public ISdkComplianceService Compliance => null;

            public UniTask<SdkResult> InitializeAsync() => UniTask.FromResult(SdkResult.Ok());

            private sealed class FakeAccount : ISdkAccountService
            {
                public event System.Action<string> OnSessionInvalidated { add { } remove { } }

                public UniTask<SdkResult<SdkLoginData>> LoginAsync()
                    => UniTask.FromResult(SdkResult<SdkLoginData>.Ok(new SdkLoginData
                    {
                        ChannelUserId = "fake_user",
                        Token = "fake_token"
                    }));

                public UniTask<SdkResult> LogoutAsync() => UniTask.FromResult(SdkResult.Ok());
            }
        }
    }
}
