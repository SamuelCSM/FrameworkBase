using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;

namespace Framework.Save
{
    /// <summary>
    /// AES-128-CBC 加解密 + HMAC-SHA256 完整性工具（框架内部使用）。
    ///
    /// 主密钥种子由 <see cref="ISaveKeyProvider"/> 提供（默认绑定本设备），
    /// 与项目级域分隔 Salt（见 <see cref="SetAppSalt"/>）拼接后经 SHA-256 派生出两把彼此独立的子密钥：
    ///   · 加密 Key（AES-128）：SHA256(种子 + Salt) 取前 16 字节；
    ///   · MAC Key（HMAC-SHA256）：追加独立标签派生，用于防篡改完整性校验。
    /// 不在代码中硬编码裸 Key；IV 随机生成并前置于密文，解密时自动读取。
    /// 种子与 Salt 任一变化都会换出新密钥，旧存档随之不可解——这是刻意的隔离语义，不是缺陷。
    /// </summary>
    internal static class AesHelper
    {
        // 域分隔 Salt 的框架兜底值。按设计不要求保密，故明文常量无妨。
        // internal 而非 private：单测换 Salt 后需据此还原，避免污染其它用例。
        internal const string DefaultAppSalt = "FrameworkBase_SaveSalt_v1";

        // 域分隔 Salt —— 它是同设备上不同产品之间的唯一区分量，必须按项目取值：
        // 默认主密钥种子是 deviceUniqueIdentifier，同一台设备上两个 FrameworkBase 产品拿到的种子
        // 完全相同，全靠此 Salt 把两者的派生密钥分开。留用框架兜底值不会立即出错，但会让
        // 同设备上的兄弟产品派生出同一把存档密钥，故正式项目须经 SaveManager.SetSaveSalt 覆盖。
        // 只在 _keyLock 内被密钥派生读取，故无需 volatile。
        private static string _appSalt = DefaultAppSalt;
        // MAC 子密钥派生标签 —— 与加密 Key 做密钥分离，避免同一把 Key 既加密又签名
        private const string MacLabel = "|mac";
        private const int KeyBytes = 16; // AES-128

        private const int IvBytes  = 16;

        /// <summary>
        /// 用途域标签：不同用途派生出彼此独立的密钥，一个域的密钥泄露不会波及另一个域。
        /// 存档是玩家数据、凭证是会话机密，两者的价值与生命周期都不同，不该共用一把 Key。
        /// </summary>
        internal static class Purpose
        {
            /// <summary>玩家存档（SaveManager）。</summary>
            public const string Save = "save";

            /// <summary>会话凭证等机密（SecureStorage 默认后端）。</summary>
            public const string SecureStorage = "secure-storage";
        }

        // 存档主密钥来源：默认绑定本设备；上云/跨设备时可通过 SetKeyProvider 替换
        private static ISaveKeyProvider _keyProvider = new DeviceSaveKeyProvider();

        // 按用途域缓存的子密钥。_keyLock：本类被 SaveManager 与 EncryptedPrefsSecureStorage 共用，
        // 二者的调用线程不由任何单一外部锁串行，故同步必须自带，不能倚赖"调用方持有档案锁"的隐式约定。
        // 换 Salt / 换密钥源与派生若并发，非同步的 check-then-act 会撕裂读到半初始化的密钥对。
        private static readonly Dictionary<string, DerivedKeys> _keysByPurpose =
            new Dictionary<string, DerivedKeys>(StringComparer.Ordinal);
        private static readonly object _keyLock = new object();

        /// <summary>某个用途域下派生出的一对子密钥。两者一次性整体发布，读侧不会看到半初始化状态。</summary>
        private sealed class DerivedKeys
        {
            /// <summary>AES 加解密 Key（16 字节）。</summary>
            public byte[] Enc;

            /// <summary>HMAC-SHA256 Key（32 字节）。</summary>
            public byte[] Mac;
        }

        /// <summary>
        /// 替换域分隔 Salt 并清空密钥缓存，下次读写时按新 Salt 重新派生。
        /// 须在任何读写存档之前调用；更换 Salt 会使此前用旧 Salt 加密的存档无法解密。
        /// </summary>
        /// <param name="salt">项目级唯一串，建议用包名（如 com.yourcompany.yourgame）。不得为空白。</param>
        /// <exception cref="ArgumentException">salt 为 null 或全空白时抛出——空 Salt 会静默退化成无域分隔。</exception>
        internal static void SetAppSalt(string salt)
        {
            if (string.IsNullOrWhiteSpace(salt)) throw new ArgumentException("Salt 不得为空白", nameof(salt));
            // 与派生同锁，理由同 SetKeyProvider：换 Salt 与清缓存必须相对派生原子
            lock (_keyLock)
            {
                _appSalt = salt;
                _keysByPurpose.Clear();
            }
        }

        /// <summary>
        /// 替换存档主密钥来源并清空密钥缓存，下次读写时按新来源重新派生。
        /// 注意：更换来源会使此前用旧来源加密的存档无法解密。
        /// </summary>
        internal static void SetKeyProvider(ISaveKeyProvider provider)
        {
            if (provider == null) throw new ArgumentNullException(nameof(provider));
            // 与派生同锁：换源与清缓存必须相对派生原子，避免旧源派生结果回写覆盖新源。
            lock (_keyLock)
            {
                _keyProvider = provider;
                _keysByPurpose.Clear();
            }
        }

        /// <summary>
        /// 取指定用途域的子密钥，未派生时在锁内派生并缓存。
        /// 派生输入含用途标签，因此存档域与凭证域即使同种子同 Salt 也得到互不相关的密钥。
        /// </summary>
        /// <param name="purpose">用途域标签，取自 <see cref="Purpose"/>。</param>
        /// <returns>该用途域的加密与 MAC 子密钥。</returns>
        private static DerivedKeys GetKeys(string purpose)
        {
            lock (_keyLock)
            {
                if (_keysByPurpose.TryGetValue(purpose, out DerivedKeys cached))
                    return cached;

                var master = _keyProvider.GetMasterSecret() ?? string.Empty;
                string domain = master + _appSalt + "|" + purpose;

                // 加密 Key = SHA256(master + Salt + 用途) 取前 16 字节
                byte[] encKey = new byte[KeyBytes];
                using (var sha = SHA256.Create())
                {
                    var encHash = sha.ComputeHash(Encoding.UTF8.GetBytes(domain));
                    Array.Copy(encHash, encKey, KeyBytes);
                }

                // MAC Key：独立标签派生的 32 字节，仅用于 HMAC-SHA256
                byte[] macKey;
                using (var sha = SHA256.Create())
                {
                    macKey = sha.ComputeHash(Encoding.UTF8.GetBytes(domain + MacLabel));
                }

                var keys = new DerivedKeys { Enc = encKey, Mac = macKey };
                _keysByPurpose[purpose] = keys;
                return keys;
            }
        }

        /// <summary>用指定用途域的密钥加密字符串，返回 IV(16) + 密文。</summary>
        /// <param name="purpose">用途域标签，取自 <see cref="Purpose"/>。</param>
        /// <param name="plainText">待加密明文。</param>
        /// <returns>IV 前置的密文字节。</returns>
        public static byte[] Encrypt(string purpose, string plainText)
        {
            DerivedKeys keys = GetKeys(purpose);
            var plainBytes = Encoding.UTF8.GetBytes(plainText);

            using (var aes = Aes.Create())
            {
                aes.Key     = keys.Enc;
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

        /// <summary>用指定用途域的密钥解密，输入 IV(16) + 密文，返回原始字符串。</summary>
        /// <param name="purpose">用途域标签，须与加密时一致，否则解不开。</param>
        /// <param name="ivAndCipher">IV 前置的密文字节。</param>
        /// <returns>解密后的明文。</returns>
        public static string Decrypt(string purpose, byte[] ivAndCipher)
        {
            if (ivAndCipher == null || ivAndCipher.Length <= IvBytes)
                throw new CryptographicException("Cipher data too short");

            DerivedKeys keys = GetKeys(purpose);
            var iv     = new byte[IvBytes];
            var cipher = new byte[ivAndCipher.Length - IvBytes];
            Array.Copy(ivAndCipher, 0, iv, 0, IvBytes);
            Array.Copy(ivAndCipher, IvBytes, cipher, 0, cipher.Length);

            using (var aes = Aes.Create())
            {
                aes.Key     = keys.Enc;
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
        /// <param name="purpose">用途域标签，取自 <see cref="Purpose"/>。</param>
        /// <param name="data">待计算完整性码的字节。</param>
        /// <returns>小写十六进制的 HMAC-SHA256。</returns>
        public static string HmacSha256Hex(string purpose, byte[] data)
        {
            DerivedKeys keys = GetKeys(purpose);
            using (var hmac = new HMACSHA256(keys.Mac))
            {
                return ToHex(hmac.ComputeHash(data));
            }
        }

        /// <summary>
        /// 常数时间校验 <paramref name="data"/> 的 HMAC 是否等于 <paramref name="expectedHex"/>，
        /// 避免按字符提前返回带来的时序侧信道。
        /// </summary>
        /// <param name="purpose">用途域标签，取自 <see cref="Purpose"/>。</param>
        /// <param name="data">待校验字节。</param>
        /// <param name="expectedHex">文件中记录的完整性码。</param>
        /// <returns>匹配返回 true。</returns>
        public static bool VerifyHmac(string purpose, byte[] data, string expectedHex)
            => FixedTimeEquals(HmacSha256Hex(purpose, data), expectedHex);

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
