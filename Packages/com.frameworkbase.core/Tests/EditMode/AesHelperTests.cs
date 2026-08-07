using System.Security.Cryptography;
using System.Text;
using Framework.Save;
using NUnit.Framework;

namespace Framework.Tests
{
    /// <summary>
    /// 存档加密核心单测（AesHelper）：加解密往返、随机 IV、HMAC 防篡改、密钥分离、
    /// 换密钥来源后旧档不可解（设备绑定语义）、换 Salt 后旧档不可解（同设备跨产品域分隔语义）。
    /// 这是存档安全的地基，同步可测、不碰磁盘。
    /// </summary>
    public class AesHelperTests
    {
        /// <summary>存档用途域，绝大多数用例只关心单一域内的行为。</summary>
        private const string Save = AesHelper.Purpose.Save;

        /// <summary>凭证用途域，用于验证跨域密钥互不相通。</summary>
        private const string Secure = AesHelper.Purpose.SecureStorage;

        private sealed class FixedKeyProvider : ISaveKeyProvider
        {
            private readonly string _secret;
            public FixedKeyProvider(string secret) => _secret = secret;
            public string GetMasterSecret() => _secret;
        }

        [SetUp]
        public void SetUp()
        {
            // 固定主密钥，摆脱对本机 deviceUniqueIdentifier 的依赖，判定确定
            AesHelper.SetKeyProvider(new FixedKeyProvider("unit-test-master-secret"));
            // Salt 同样固定：它与主密钥种子一同参与派生，不钉死则用例间会互相串味
            AesHelper.SetAppSalt("unit-test-salt");
        }

        [TearDown]
        public void TearDown()
        {
            // 还原默认设备绑定来源与框架兜底 Salt，避免污染其它用例/播放态
            AesHelper.SetKeyProvider(new DeviceSaveKeyProvider());
            AesHelper.SetAppSalt(AesHelper.DefaultAppSalt);
        }

        [Test]
        public void 加解密往返_还原原文()
        {
            const string plain = "玩家昵称=勇者, 金币=12345, note=\"quote\"\n第二行";
            byte[] cipher = AesHelper.Encrypt(Save, plain);
            Assert.AreEqual(plain, AesHelper.Decrypt(Save, cipher));
        }

        [Test]
        public void 每次加密_IV随机导致密文不同()
        {
            const string plain = "same content";
            byte[] a = AesHelper.Encrypt(Save, plain);
            byte[] b = AesHelper.Encrypt(Save, plain);

            Assert.AreNotEqual(System.Convert.ToBase64String(a), System.Convert.ToBase64String(b),
                "随机 IV 应使相同明文每次密文不同");
            Assert.AreEqual(plain, AesHelper.Decrypt(Save, a));
            Assert.AreEqual(plain, AesHelper.Decrypt(Save, b));
        }

        [Test]
        public void HMAC_能检出篡改()
        {
            byte[] cipher = AesHelper.Encrypt(Save, "balance=100");
            string mac = AesHelper.HmacSha256Hex(Save, cipher);

            Assert.IsTrue(AesHelper.VerifyHmac(Save, cipher, mac), "未篡改应校验通过");

            cipher[cipher.Length - 1] ^= 0xFF; // 翻转最后一字节模拟篡改
            Assert.IsFalse(AesHelper.VerifyHmac(Save, cipher, mac), "篡改后 HMAC 必须校验失败");
        }

        [Test]
        public void HMAC_攻击者无密钥无法伪造合法码()
        {
            byte[] cipher = AesHelper.Encrypt(Save, "vip=false");
            string legitMac = AesHelper.HmacSha256Hex(Save, cipher);

            // 攻击者只有裸 SHA-256（无 MAC Key），算出的码与 HMAC 不同 → 无法冒充
            string forged = AesHelper.Sha256Hex(cipher);
            Assert.AreNotEqual(legitMac, forged);
            Assert.IsFalse(AesHelper.VerifyHmac(Save, cipher, forged));
        }

        [Test]
        public void 换密钥来源后_旧档无法解密()
        {
            const string secret = "secret";
            byte[] cipher = AesHelper.Encrypt(Save, secret);

            // 模拟换设备/换账号：主密钥来源变化 → 派生密钥变化 → 旧密文不可再解出原文
            AesHelper.SetKeyProvider(new FixedKeyProvider("a-different-secret"));

            // 设备绑定要保证的是"旧档读不出原文"。断言不能是"必抛异常"——
            // AES-CBC+PKCS7 用错 key 时约 255/256 概率 padding 校验失败而抛，但另有约 1/256
            // 概率解出乱码而 padding 恰好合法；随机 IV 使这一概率每次运行独立摇骰子，
            // 断言"必抛"会让 CI 偶发误挂（约 0.4%）。改为断言密码学上必然成立的性质：
            // 换 key 后要么抛异常、要么解出的绝不是原文——两者都满足"旧档不可解"。
            string recovered = null;
            try
            {
                recovered = AesHelper.Decrypt(Save, cipher);
            }
            catch (CryptographicException)
            {
                Assert.Pass("padding 校验失败，旧档不可解（换密钥语义成立）");
            }

            Assert.AreNotEqual(secret, recovered,
                "换密钥来源后绝不能解出原文（存档设备绑定语义）");
        }

        [Test]
        public void 换Salt后_旧档无法解密()
        {
            const string secret = "coins=9999";
            byte[] cipher = AesHelper.Encrypt(Save, secret);

            // 模拟同设备上的另一个产品：主密钥种子相同（同一台设备），仅 Salt 不同。
            // 这正是 Salt 存在的理由——若 Salt 不参与派生，两个产品会解开彼此的存档。
            AesHelper.SetAppSalt("another-product-salt");

            // 断言口径与「换密钥来源」用例一致：不能断言"必抛"。AES-CBC+PKCS7 用错 key 时
            // 约 1/256 概率解出乱码而 padding 恰好合法，断言必抛会让 CI 偶发误挂。
            string recovered = null;
            try
            {
                recovered = AesHelper.Decrypt(Save, cipher);
            }
            catch (CryptographicException)
            {
                Assert.Pass("padding 校验失败，跨产品不可解（域分隔语义成立）");
            }

            Assert.AreNotEqual(secret, recovered,
                "换 Salt 后绝不能解出原文（同设备不同产品的存档域分隔语义）");
        }

        [Test]
        public void 设置空白Salt_抛异常()
        {
            // 空 Salt 会静默退化成"无域分隔"，必须在入口拒绝而不是让它悄悄生效
            Assert.Throws<System.ArgumentException>(() => AesHelper.SetAppSalt(null));
            Assert.Throws<System.ArgumentException>(() => AesHelper.SetAppSalt("   "));
        }

        [Test]
        public void 解密_密文过短抛异常()
        {
            Assert.Throws<CryptographicException>(() => AesHelper.Decrypt(Save, new byte[8]),
                "短于 IV 长度的输入应判定为损坏");
        }

        [Test]
        public void HmacHex_长度与格式()
        {
            string mac = AesHelper.HmacSha256Hex(Save, Encoding.UTF8.GetBytes("x"));
            Assert.AreEqual(64, mac.Length, "HMAC-SHA256 十六进制应为 64 字符");
            StringAssert.IsMatch("^[0-9a-f]+$", mac, "应为小写十六进制");
        }

        // ── 用途域隔离 ───────────────────────────────────────────────────────

        [Test]
        public void 不同用途域_加密密钥互不相通()
        {
            byte[] cipher = AesHelper.Encrypt(Save, "session-token-value");

            // 同种子同 Salt 下若两域共用一把 Key，凭证域能直接解开存档域的密文，
            // 一处密钥泄露就波及另一处。
            Assert.Throws<CryptographicException>(() => AesHelper.Decrypt(Secure, cipher),
                "凭证域不得解开存档域的密文");
            Assert.AreEqual("session-token-value", AesHelper.Decrypt(Save, cipher), "本域仍应正常解开");
        }

        [Test]
        public void 不同用途域_完整性码互不通过()
        {
            byte[] data = Encoding.UTF8.GetBytes("balance=100");
            string saveMac = AesHelper.HmacSha256Hex(Save, data);

            Assert.IsFalse(AesHelper.VerifyHmac(Secure, data, saveMac), "存档域的 MAC 不得在凭证域通过校验");
            Assert.IsTrue(AesHelper.VerifyHmac(Save, data, saveMac));
        }
    }
}
