using System;
using System.Security.Cryptography;
using System.Text;
using Framework.Core;
using Framework.HotUpdate;
using Framework.RemoteConfig;
using Framework.Serialization;
using NUnit.Framework;
using UnityEngine;

namespace Framework.Tests
{
    /// <summary>
    /// 远程配置签名信封校验单测：未配置公钥时原样放行、配置后强制验签、
    /// 签名/密钥/时效任一不符即拒绝。远程配置是一条能改灰度与数值的远程控制通道，
    /// TLS 只保证来源域名，挡不住 CDN 投毒与后台账号被盗。
    /// </summary>
    public class RemoteConfigSignatureTests
    {
        private const string Payload = "{\"flag\":true,\"speed\":10}";

        private string _privateKeyXml;
        private string _publicKeyXml;
        private AppConfigAsset _config;

        [SetUp]
        public void SetUp()
        {
            using (var rsa = new RSACryptoServiceProvider(2048))
            {
                _privateKeyXml = rsa.ToXmlString(true);
                _publicKeyXml = rsa.ToXmlString(false);
            }

            _config = ScriptableObject.CreateInstance<AppConfigAsset>();
        }

        [TearDown]
        public void TearDown()
        {
            UnityEngine.Object.DestroyImmediate(_config);
        }

        /// <summary>给配置装上一把可信公钥，从而启用签名校验。</summary>
        private void TrustKey(string keyId)
        {
            _config.RemoteConfigPublicKeys = new[]
            {
                new UpdateManifestPublicKeyEntry { KeyId = keyId, PublicKeyXml = _publicKeyXml }
            };
        }

        /// <summary>构造一个签名信封文本。</summary>
        private string BuildEnvelope(string keyId, string payload, long expiresAt = 0, bool corruptSignature = false)
        {
            string signature = UpdateSecurity.SignManifest(Encoding.UTF8.GetBytes(payload), _privateKeyXml);
            if (corruptSignature)
            {
                byte[] raw = Convert.FromBase64String(signature);
                raw[0] ^= 0xFF;
                signature = Convert.ToBase64String(raw);
            }

            return JsonSerializers.Shared.ToJson(new RemoteConfigSignature.SignedEnvelope
            {
                KeyId = keyId,
                Signature = signature,
                Payload = payload,
                ExpiresAtUnixSeconds = expiresAt,
            }, false);
        }

        [Test]
        public void 未配置公钥_明文载荷原样放行()
        {
            // 远程配置是可选能力，模板与开发期没有签名服务；强制启用会让这条路整个不可用。
            Assert.IsTrue(RemoteConfigSignature.TryUnwrap(Payload, _config, 1000, out string payload, out string reject));
            Assert.AreEqual(Payload, payload);
            Assert.IsNull(reject);
        }

        [Test]
        public void 配置公钥_合法信封通过并拆出载荷()
        {
            TrustKey("k1");

            Assert.IsTrue(RemoteConfigSignature.TryUnwrap(
                BuildEnvelope("k1", Payload), _config, 1000, out string payload, out _));
            Assert.AreEqual(Payload, payload, "应拆出原始配置 JSON 而非整个信封");
        }

        [Test]
        public void 启用签名后_明文载荷被拒绝()
        {
            TrustKey("k1");

            // 防降级：启用后不能再接受"没有签名"的载荷，否则去掉信封即可绕过。
            Assert.IsFalse(RemoteConfigSignature.TryUnwrap(Payload, _config, 1000, out _, out string reject));
            Assert.IsNotNull(reject);
        }

        [Test]
        public void 签名不匹配_拒绝()
        {
            TrustKey("k1");

            Assert.IsFalse(RemoteConfigSignature.TryUnwrap(
                BuildEnvelope("k1", Payload, corruptSignature: true), _config, 1000, out _, out string reject));
            StringAssert.Contains("签名不匹配", reject);
        }

        [Test]
        public void 载荷被改而签名未变_拒绝()
        {
            TrustKey("k1");
            string envelope = BuildEnvelope("k1", Payload).Replace("\\\"speed\\\":10", "\\\"speed\\\":9999");

            Assert.IsFalse(RemoteConfigSignature.TryUnwrap(envelope, _config, 1000, out _, out string reject));
            Assert.IsNotNull(reject, "改了载荷就必须验签失败");
        }

        [Test]
        public void KeyId未命中信任根_拒绝()
        {
            TrustKey("k1");

            Assert.IsFalse(RemoteConfigSignature.TryUnwrap(
                BuildEnvelope("k2", Payload), _config, 1000, out _, out string reject));
            StringAssert.Contains("KeyId", reject);
        }

        [Test]
        public void 已过期_拒绝()
        {
            TrustKey("k1");
            string envelope = BuildEnvelope("k1", Payload, expiresAt: 500);

            // 过期判定在验签之后：先确认这份声明可信，再看它声明的时效。
            Assert.IsFalse(RemoteConfigSignature.TryUnwrap(envelope, _config, 1000, out _, out string reject));
            StringAssert.Contains("过期", reject);
            Assert.IsTrue(RemoteConfigSignature.TryUnwrap(envelope, _config, 499, out _, out _), "未到期应通过");
        }
    }
}
