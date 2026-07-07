using System.Security.Cryptography;
using System.Text;
using Framework.Save;
using NUnit.Framework;

namespace Framework.Tests
{
    /// <summary>
    /// 存档加密核心单测（AesHelper）：加解密往返、随机 IV、HMAC 防篡改、密钥分离、
    /// 换密钥来源后旧档不可解（设备绑定语义）。这是存档安全的地基，同步可测、不碰磁盘。
    /// </summary>
    public class AesHelperTests
    {
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
        }

        [TearDown]
        public void TearDown()
        {
            // 还原默认设备绑定来源，避免污染其它用例/播放态
            AesHelper.SetKeyProvider(new DeviceSaveKeyProvider());
        }

        [Test]
        public void 加解密往返_还原原文()
        {
            const string plain = "玩家昵称=勇者, 金币=12345, note=\"quote\"\n第二行";
            byte[] cipher = AesHelper.Encrypt(plain);
            Assert.AreEqual(plain, AesHelper.Decrypt(cipher));
        }

        [Test]
        public void 每次加密_IV随机导致密文不同()
        {
            const string plain = "same content";
            byte[] a = AesHelper.Encrypt(plain);
            byte[] b = AesHelper.Encrypt(plain);

            Assert.AreNotEqual(System.Convert.ToBase64String(a), System.Convert.ToBase64String(b),
                "随机 IV 应使相同明文每次密文不同");
            Assert.AreEqual(plain, AesHelper.Decrypt(a));
            Assert.AreEqual(plain, AesHelper.Decrypt(b));
        }

        [Test]
        public void HMAC_能检出篡改()
        {
            byte[] cipher = AesHelper.Encrypt("balance=100");
            string mac = AesHelper.HmacSha256Hex(cipher);

            Assert.IsTrue(AesHelper.VerifyHmac(cipher, mac), "未篡改应校验通过");

            cipher[cipher.Length - 1] ^= 0xFF; // 翻转最后一字节模拟篡改
            Assert.IsFalse(AesHelper.VerifyHmac(cipher, mac), "篡改后 HMAC 必须校验失败");
        }

        [Test]
        public void HMAC_攻击者无密钥无法伪造合法码()
        {
            byte[] cipher = AesHelper.Encrypt("vip=false");
            string legitMac = AesHelper.HmacSha256Hex(cipher);

            // 攻击者只有裸 SHA-256（无 MAC Key），算出的码与 HMAC 不同 → 无法冒充
            string forged = AesHelper.Sha256Hex(cipher);
            Assert.AreNotEqual(legitMac, forged);
            Assert.IsFalse(AesHelper.VerifyHmac(cipher, forged));
        }

        [Test]
        public void 换密钥来源后_旧档无法解密()
        {
            byte[] cipher = AesHelper.Encrypt("secret");

            // 模拟换设备/换账号：主密钥来源变化 → 派生密钥变化 → 旧密文解密应失败
            AesHelper.SetKeyProvider(new FixedKeyProvider("a-different-secret"));

            Assert.Throws<CryptographicException>(() => AesHelper.Decrypt(cipher),
                "不同主密钥不应能解开旧密文（存档设备绑定语义）");
        }

        [Test]
        public void 解密_密文过短抛异常()
        {
            Assert.Throws<CryptographicException>(() => AesHelper.Decrypt(new byte[8]),
                "短于 IV 长度的输入应判定为损坏");
        }

        [Test]
        public void HmacHex_长度与格式()
        {
            string mac = AesHelper.HmacSha256Hex(Encoding.UTF8.GetBytes("x"));
            Assert.AreEqual(64, mac.Length, "HMAC-SHA256 十六进制应为 64 字符");
            StringAssert.IsMatch("^[0-9a-f]+$", mac, "应为小写十六进制");
        }
    }
}
