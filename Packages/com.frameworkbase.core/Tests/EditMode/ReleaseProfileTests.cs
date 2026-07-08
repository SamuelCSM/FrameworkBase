using Framework.Editor.Release;
using NUnit.Framework;

namespace Framework.Tests
{
    /// <summary>
    /// 发布环境（ReleaseProfile）解析与发布前校验（ReleaseProfileGate）单元测试。
    /// 校验逻辑是纯函数（不读文件、不碰 EditorPrefs），私钥是否可用由参数注入。
    /// </summary>
    public class ReleaseProfileTests
    {
        // ── 解析 ─────────────────────────────────────────────────────────────

        [Test]
        public void FromJson_合法_字段回填()
        {
            const string json = "{\"Name\":\"prod\",\"BaseUrl\":\"https://cdn.example.com/updates\"," +
                                "\"RequireHttps\":true,\"RequireManifestSignature\":true," +
                                "\"SigningKeyRef\":\"prod_key\",\"AllowPlayerPrefsOverride\":false}";

            var profile = ReleaseProfile.FromJson(json, out string error);

            Assert.IsNull(error);
            Assert.IsNotNull(profile);
            Assert.AreEqual("prod", profile.Name);
            Assert.AreEqual("https://cdn.example.com/updates", profile.BaseUrl);
            Assert.IsTrue(profile.RequireHttps);
            Assert.IsTrue(profile.RequireManifestSignature);
            Assert.AreEqual("prod_key", profile.SigningKeyRef);
            Assert.IsFalse(profile.AllowPlayerPrefsOverride);
        }

        [Test]
        public void FromJson_空串_返回null并给出原因()
        {
            Assert.IsNull(ReleaseProfile.FromJson("", out string error));
            Assert.IsNotNull(error);
        }

        // ── 校验：URL / HTTPS ────────────────────────────────────────────────

        [Test]
        public void Gate_prod_明文HTTP_阻断()
        {
            var profile = new ReleaseProfile
            {
                Name = "prod",
                BaseUrl = "http://cdn.example.com/updates",
                RequireHttps = true,
                RequireManifestSignature = false
            };

            Assert.IsFalse(ReleaseProfileGate.Validate(profile, hasUsablePrivateKey: true, out string report));
            StringAssert.Contains("阻断项", report);
        }

        [Test]
        public void Gate_staging_要求HTTPS但配了HTTP_阻断()
        {
            // 非 prod 环境，但 profile 显式 RequireHttps=true，同样应拦下明文。
            var profile = new ReleaseProfile
            {
                Name = "staging",
                BaseUrl = "http://staging.example.com/updates",
                RequireHttps = true,
                RequireManifestSignature = false
            };

            Assert.IsFalse(ReleaseProfileGate.Validate(profile, hasUsablePrivateKey: true, out _));
        }

        [Test]
        public void Gate_缺BaseUrl_阻断()
        {
            var profile = new ReleaseProfile { Name = "dev", BaseUrl = "" };
            Assert.IsFalse(ReleaseProfileGate.Validate(profile, hasUsablePrivateKey: false, out _));
        }

        [Test]
        public void Gate_null_阻断()
        {
            Assert.IsFalse(ReleaseProfileGate.Validate(null, hasUsablePrivateKey: true, out string report));
            Assert.IsNotNull(report);
        }

        // ── 校验：签名准入（错误 4 收口）─────────────────────────────────────

        [Test]
        public void Gate_要求签名但无私钥_阻断()
        {
            var profile = new ReleaseProfile
            {
                Name = "prod",
                BaseUrl = "https://cdn.example.com/updates",
                RequireHttps = true,
                RequireManifestSignature = true,
                SigningKeyRef = "prod_key"
            };

            Assert.IsFalse(ReleaseProfileGate.Validate(profile, hasUsablePrivateKey: false, out string report));
            StringAssert.Contains("私钥", report);
        }

        [Test]
        public void Gate_prod_HTTPS且有私钥_放行()
        {
            var profile = new ReleaseProfile
            {
                Name = "prod",
                BaseUrl = "https://cdn.example.com/updates",
                RequireHttps = true,
                RequireManifestSignature = true,
                SigningKeyRef = "prod_key"
            };

            Assert.IsTrue(ReleaseProfileGate.Validate(profile, hasUsablePrivateKey: true, out _));
        }

        [Test]
        public void Gate_dev_HTTP且不要求签名_放行()
        {
            var profile = new ReleaseProfile
            {
                Name = "dev",
                BaseUrl = "http://127.0.0.1:80/Updates",
                RequireHttps = false,
                RequireManifestSignature = false
            };

            Assert.IsTrue(ReleaseProfileGate.Validate(profile, hasUsablePrivateKey: false, out _));
        }
    }
}
