using System;
using System.Security.Cryptography;
using System.Text;

namespace Framework.Save
{
    /// <summary>
    /// AES-128-CBC 加解密 + HMAC-SHA256 完整性工具（框架内部使用）。
    ///
    /// 主密钥种子由 <see cref="ISaveKeyProvider"/> 提供（默认绑定本设备），
    /// 再经 SHA-256 派生出两把彼此独立的子密钥：
    ///   · 加密 Key（AES-128）：保持与历史版本一致，旧存档仍可解密；
    ///   · MAC Key（HMAC-SHA256）：独立派生，用于防篡改完整性校验。
    /// 不在代码中硬编码裸 Key；IV 随机生成并前置于密文，解密时自动读取。
    /// </summary>
    internal static class AesHelper
    {
        // 固定 Salt —— 修改此值会导致旧存档无法解密，请勿随意更改
        private const string AppSalt = "ClientBase_SaveSalt_v1";
        // MAC 子密钥派生标签 —— 与加密 Key 做密钥分离，避免同一把 Key 既加密又签名
        private const string MacLabel = "|mac";
        private const int KeyBytes = 16; // AES-128

        private const int IvBytes  = 16;

        // 存档主密钥来源：默认绑定本设备；上云/跨设备时可通过 SetKeyProvider 替换
        private static ISaveKeyProvider _keyProvider = new DeviceSaveKeyProvider();

        private static byte[] _cachedEncKey; // AES 加解密 Key（16 字节）
        private static byte[] _cachedMacKey; // HMAC-SHA256 Key（32 字节）

        /// <summary>
        /// 替换存档主密钥来源并清空密钥缓存，下次读写时按新来源重新派生。
        /// 注意：更换来源会使此前用旧来源加密的存档无法解密。
        /// </summary>
        internal static void SetKeyProvider(ISaveKeyProvider provider)
        {
            _keyProvider = provider ?? throw new ArgumentNullException(nameof(provider));
            _cachedEncKey = null;
            _cachedMacKey = null;
        }

        // 确保两把子密钥已派生并缓存（线程安全由 SaveManager 的档案锁在 IO 层保证）
        private static void EnsureKeys()
        {
            if (_cachedEncKey != null && _cachedMacKey != null) return;

            var master = _keyProvider.GetMasterSecret() ?? string.Empty;

            // 加密 Key：与历史保持一致 = SHA256(master + Salt) 取前 16 字节，保证旧档可解密
            using (var sha = SHA256.Create())
            {
                var encHash = sha.ComputeHash(Encoding.UTF8.GetBytes(master + AppSalt));
                var encKey = new byte[KeyBytes];
                Array.Copy(encHash, encKey, KeyBytes);
                _cachedEncKey = encKey;
            }

            // MAC Key：独立标签派生的 32 字节，仅用于 HMAC-SHA256
            using (var sha = SHA256.Create())
            {
                _cachedMacKey = sha.ComputeHash(Encoding.UTF8.GetBytes(master + AppSalt + MacLabel));
            }
        }

        /// <summary>加密 JSON 字符串，返回 IV(16) + 密文</summary>
        public static byte[] Encrypt(string plainText)
        {
            EnsureKeys();
            var plainBytes = Encoding.UTF8.GetBytes(plainText);

            using (var aes = Aes.Create())
            {
                aes.Key     = _cachedEncKey;
                aes.Mode    = CipherMode.CBC;
                aes.Padding = PaddingMode.PKCS7;
                aes.GenerateIV();

                using (var enc = aes.CreateEncryptor())
                {
                    var cipher = enc.TransformFinalBlock(plainBytes, 0, plainBytes.Length);
                    var result = new byte[IvBytes + cipher.Length];
                    Array.Copy(aes.IV, 0, result, 0, IvBytes);
                    Array.Copy(cipher, 0, result, IvBytes, cipher.Length);
                    return result;
                }
            }
        }

        /// <summary>解密，输入 IV(16) + 密文，返回原始 JSON 字符串</summary>
        public static string Decrypt(byte[] ivAndCipher)
        {
            if (ivAndCipher == null || ivAndCipher.Length <= IvBytes)
                throw new CryptographicException("Cipher data too short");

            EnsureKeys();
            var iv     = new byte[IvBytes];
            var cipher = new byte[ivAndCipher.Length - IvBytes];
            Array.Copy(ivAndCipher, 0, iv, 0, IvBytes);
            Array.Copy(ivAndCipher, IvBytes, cipher, 0, cipher.Length);

            using (var aes = Aes.Create())
            {
                aes.Key     = _cachedEncKey;
                aes.IV      = iv;
                aes.Mode    = CipherMode.CBC;
                aes.Padding = PaddingMode.PKCS7;

                using (var dec = aes.CreateDecryptor())
                {
                    var plain = dec.TransformFinalBlock(cipher, 0, cipher.Length);
                    return Encoding.UTF8.GetString(plain);
                }
            }
        }

        /// <summary>
        /// 对字节数组计算 HMAC-SHA256 并返回小写十六进制字符串（防篡改完整性码）。
        /// 与裸 SHA-256 不同：攻击者无 MAC Key 便无法在篡改后重算出合法完整性码。
        /// </summary>
        public static string HmacSha256Hex(byte[] data)
        {
            EnsureKeys();
            using (var hmac = new HMACSHA256(_cachedMacKey))
            {
                return ToHex(hmac.ComputeHash(data));
            }
        }

        /// <summary>
        /// 常数时间校验 <paramref name="data"/> 的 HMAC 是否等于 <paramref name="expectedHex"/>，
        /// 避免按字符提前返回带来的时序侧信道。
        /// </summary>
        public static bool VerifyHmac(byte[] data, string expectedHex)
            => FixedTimeEquals(HmacSha256Hex(data), expectedHex);

        /// <summary>
        /// 对字节数组计算裸 SHA-256 并返回小写十六进制字符串。
        /// 通用工具：裸摘要<b>无密钥</b>，不可当完整性 MAC 用（谁都能重算）——存档完整性一律走
        /// <see cref="HmacSha256Hex"/>。SaveManager 已不再接受裸 SHA-256 存档；此方法仅供一般摘要用途。
        /// </summary>
        public static string Sha256Hex(byte[] data)
        {
            using (var sha = SHA256.Create())
            {
                return ToHex(sha.ComputeHash(data));
            }
        }

        // 字节数组转小写十六进制
        private static string ToHex(byte[] hash)
        {
            var sb = new StringBuilder(hash.Length * 2);
            foreach (var b in hash) sb.Append(b.ToString("x2"));
            return sb.ToString();
        }

        // 常数时间字符串比较：长度与逐字符差异都累积进 diff，不提前返回
        private static bool FixedTimeEquals(string a, string b)
        {
            if (a == null || b == null) return false;
            var diff = a.Length ^ b.Length;
            var min = Math.Min(a.Length, b.Length);
            for (var i = 0; i < min; i++) diff |= a[i] ^ b[i];
            return diff == 0;
        }
    }
}
