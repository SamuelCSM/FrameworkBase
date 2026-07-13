using System;
using Framework.Save;
using UnityEngine;

namespace Framework.Security
{
    /// <summary>
    /// 默认安全存储：设备密钥 <b>AES 加密 + HMAC 防篡改</b>，密文存 <see cref="PlayerPrefs"/>。
    ///
    /// <para>复用 <see cref="Framework.Save.SaveManager"/> 同一套设备绑定密钥（<c>AesHelper</c>）：
    /// 换设备无法解密（刻意的低门槛防作弊）。存储格式：<c>base64(IV+密文).hmacHex</c>，读时先验 HMAC
    /// 再解密，任一失败按「读不到」处理（防篡改、防跨设备/密钥不匹配的脏数据）。</para>
    ///
    /// <para><b>安全边界（重要）</b>：PlayerPrefs 非机密存储（Android 明文 XML / iOS NSUserDefaults），
    /// 本实现只是「加密后再放进去」，比明文强、能挡住普通翻存档，但<b>密钥源自可推断的
    /// deviceUniqueIdentifier，非硬件级</b>。高价值凭证请接 iOS Keychain / Android Keystore 扩展包
    /// （实现 <see cref="ISecureStorage"/> 经 <see cref="SecureStorage.SetBackend"/> 注入）。</para>
    /// </summary>
    public sealed class EncryptedPrefsSecureStorage : ISecureStorage
    {
        /// <summary>PlayerPrefs 键前缀（与其它 PlayerPrefs 用途隔离；测试也据此定位键）。</summary>
        public const string KeyPrefix = "fb_secure_";

        /// <summary>
        /// 键索引 PlayerPrefs 键：PlayerPrefs 无法枚举键，维护一份已写入全键清单以支持
        /// <see cref="DeleteAll"/>（RTBF 抹除机密）。业务键不得等于保留名 <c>__index__</c>。
        /// </summary>
        private const string IndexKey = KeyPrefix + "__index__";
        private const char IndexSeparator = '\n';

        /// <inheritdoc />
        public string Name => "encrypted-prefs";

        /// <inheritdoc />
        public void Set(string key, string value)
        {
            if (string.IsNullOrEmpty(key))
            {
                GameLog.Error("[SecureStorage] Set: key 为空，忽略");
                return;
            }

            try
            {
                string full = FullKey(key);
                byte[] enc = AesHelper.Encrypt(value ?? string.Empty);
                string mac = AesHelper.HmacSha256Hex(enc);
                PlayerPrefs.SetString(full, Convert.ToBase64String(enc) + "." + mac);
                AddToIndex(full);
                PlayerPrefs.Save();
            }
            catch (Exception ex)
            {
                GameLog.Error($"[SecureStorage] 写入失败 key={key}: {ex.Message}");
            }
        }

        /// <inheritdoc />
        public bool TryGet(string key, out string value)
        {
            value = null;
            if (string.IsNullOrEmpty(key))
                return false;

            string full = FullKey(key);
            if (!PlayerPrefs.HasKey(full))
                return false;

            try
            {
                string stored = PlayerPrefs.GetString(full);
                int dot = stored.LastIndexOf('.');
                if (dot <= 0)
                    return false; // 格式异常（被外部改写）

                byte[] enc = Convert.FromBase64String(stored.Substring(0, dot));
                string mac = stored.Substring(dot + 1);

                if (!AesHelper.VerifyHmac(enc, mac))
                {
                    GameLog.Warning($"[SecureStorage] key={key} HMAC 校验失败（被篡改 / 密钥不匹配），按读不到处理");
                    return false;
                }

                value = AesHelper.Decrypt(enc);
                return true;
            }
            catch (Exception ex)
            {
                // 密文损坏 / base64 非法 / 跨设备密钥不匹配：一律按读不到，让上层走重新鉴权。
                GameLog.Warning($"[SecureStorage] 读取失败 key={key}: {ex.Message}");
                return false;
            }
        }

        /// <inheritdoc />
        public bool Contains(string key)
        {
            return !string.IsNullOrEmpty(key) && PlayerPrefs.HasKey(FullKey(key));
        }

        /// <inheritdoc />
        public void Delete(string key)
        {
            if (string.IsNullOrEmpty(key))
                return;

            string full = FullKey(key);
            PlayerPrefs.DeleteKey(full);
            RemoveFromIndex(full);
            PlayerPrefs.Save();
        }

        /// <inheritdoc />
        public void DeleteAll()
        {
            foreach (string full in ReadIndex())
                PlayerPrefs.DeleteKey(full);

            PlayerPrefs.DeleteKey(IndexKey);
            PlayerPrefs.Save();
        }

        private static string FullKey(string key) => KeyPrefix + key;

        // ── 键索引维护（支持 DeleteAll）────────────────────────────────────────
        private static System.Collections.Generic.HashSet<string> ReadIndex()
        {
            var set = new System.Collections.Generic.HashSet<string>();
            if (!PlayerPrefs.HasKey(IndexKey))
                return set;

            string raw = PlayerPrefs.GetString(IndexKey);
            if (string.IsNullOrEmpty(raw))
                return set;

            foreach (string entry in raw.Split(IndexSeparator))
            {
                if (!string.IsNullOrEmpty(entry))
                    set.Add(entry);
            }
            return set;
        }

        private static void WriteIndex(System.Collections.Generic.HashSet<string> set)
        {
            if (set.Count == 0)
            {
                PlayerPrefs.DeleteKey(IndexKey);
                return;
            }
            PlayerPrefs.SetString(IndexKey, string.Join(IndexSeparator.ToString(), set));
        }

        private static void AddToIndex(string fullKey)
        {
            if (fullKey == IndexKey)
                return; // 索引键自身不入索引
            var set = ReadIndex();
            if (set.Add(fullKey))
                WriteIndex(set);
        }

        private static void RemoveFromIndex(string fullKey)
        {
            var set = ReadIndex();
            if (set.Remove(fullKey))
                WriteIndex(set);
        }
    }
}
