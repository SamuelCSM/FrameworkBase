using System;
using Framework.Save;
using UnityEngine;

namespace Framework.Security
{
    /// <summary>
    /// 默认安全存储：设备密钥 <b>AES 加密 + HMAC 防篡改</b>，密文存 <see cref="PlayerPrefs"/>。
    ///
    /// <para>密钥经 <c>AesHelper</c> 的<b>凭证用途域</b>派生，与存档域彼此独立，一处泄露不波及另一处；
    /// 主密钥种子仍绑定本设备，换设备无法解密（刻意的低门槛防作弊）。
    /// 存储格式：<c>base64(IV+密文).hmacHex</c>，读时先验 HMAC 再解密，任一失败按「读不到」处理
    /// （防篡改、防跨设备/密钥不匹配的脏数据）。键索引走同一套信封。</para>
    ///
    /// <para><b>安全边界（重要）</b>：PlayerPrefs 非机密存储（Android 明文 XML / iOS NSUserDefaults），
    /// 本实现只是「加密后再放进去」，比明文强、能挡住普通翻存档，但<b>密钥源自可推断的
    /// deviceUniqueIdentifier，非硬件级</b>。高价值凭证请接 iOS Keychain / Android Keystore 扩展包
    /// （实现 <see cref="ISecureStorage"/> 经 <see cref="SecureStorage.SetBackend"/> 注入）。</para>
    /// </summary>
    public sealed class EncryptedPrefsSecureStorage : ISecureStorage, ISecureStorageBulkErase
    {
        /// <summary>PlayerPrefs 键前缀（与其它 PlayerPrefs 用途隔离；测试也据此定位键）。</summary>
        public const string KeyPrefix = "fb_secure_";

        /// <summary>
        /// 键索引 PlayerPrefs 键：PlayerPrefs 无法枚举键，维护一份已写入全键清单以支持
        /// <see cref="DeleteAll"/>（RTBF 抹除机密）。业务键若映射到此保留键会破坏索引，
        /// 故读写全路径经 <see cref="IsReservedKey"/> 拒绝（见各 API），不再只靠文档约定。
        /// 索引内容本身也加密并附 HMAC，篡改可被发现（见下方索引维护段）。
        /// </summary>
        private const string IndexKey = KeyPrefix + "__index__";

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
            if (IsReservedKey(key))
            {
                // 撞内部保留键：写入会用密文覆盖键索引、令 DeleteAll 漏删，直接拒绝。
                GameLog.Error($"[SecureStorage] Set: key=\"{key}\" 撞内部保留键，忽略（会破坏 DeleteAll 索引）");
                return;
            }

            try
            {
                string full = FullKey(key);
                byte[] enc = AesHelper.Encrypt(AesHelper.Purpose.SecureStorage, value ?? string.Empty);
                string mac = AesHelper.HmacSha256Hex(AesHelper.Purpose.SecureStorage, enc);
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
            if (string.IsNullOrEmpty(key) || IsReservedKey(key))
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

                if (!AesHelper.VerifyHmac(AesHelper.Purpose.SecureStorage, enc, mac))
                {
                    GameLog.Warning($"[SecureStorage] key={key} HMAC 校验失败（被篡改 / 密钥不匹配），按读不到处理");
                    return false;
                }

                value = AesHelper.Decrypt(AesHelper.Purpose.SecureStorage, enc);
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
            return !string.IsNullOrEmpty(key) && !IsReservedKey(key) && PlayerPrefs.HasKey(FullKey(key));
        }

        /// <inheritdoc />
        public void Delete(string key)
        {
            // 保留键不由业务写入（Set 已拒绝），故此处一并忽略，避免误删整份索引。
            if (string.IsNullOrEmpty(key) || IsReservedKey(key))
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

        /// <summary>业务键是否映射到内部保留键（键索引）。撞上会破坏 <see cref="DeleteAll"/> 索引，一律拒绝。</summary>
        private static bool IsReservedKey(string key) => FullKey(key) == IndexKey;

        // ── 键索引维护（支持 DeleteAll）────────────────────────────────────────
        //
        // 索引与普通值走同一套加密 + HMAC 信封，并用「长度前缀」编码而非分隔符拼接：
        //   · 分隔符方案下，含该字符的键会被拆成两条，索引从此指向不存在的键、真键漏删；
        //   · 不认证的索引可被单独篡改，让 DeleteAll 漏删机密或误删无关 PlayerPrefs 键。
        // 长度前缀对任意键内容都无歧义，认证信封则让篡改可被发现。

        /// <summary>
        /// 读取键索引。索引损坏或被篡改时返回空集并记 Error——
        /// 这会让 <see cref="DeleteAll"/> 漏删，属于必须被看见的合规事件，不能静默当作"没有键"。
        /// </summary>
        /// <returns>已写入的完整键集合。</returns>
        private static System.Collections.Generic.HashSet<string> ReadIndex()
        {
            var set = new System.Collections.Generic.HashSet<string>();
            if (!PlayerPrefs.HasKey(IndexKey))
                return set;

            string stored = PlayerPrefs.GetString(IndexKey);
            if (string.IsNullOrEmpty(stored))
                return set;

            try
            {
                int dot = stored.LastIndexOf('.');
                if (dot <= 0)
                    throw new FormatException("索引信封格式异常");

                byte[] enc = Convert.FromBase64String(stored.Substring(0, dot));
                if (!AesHelper.VerifyHmac(AesHelper.Purpose.SecureStorage, enc, stored.Substring(dot + 1)))
                    throw new FormatException("索引 HMAC 校验失败");

                DecodeIndex(AesHelper.Decrypt(AesHelper.Purpose.SecureStorage, enc), set);
            }
            catch (Exception ex)
            {
                GameLog.Error($"[SecureStorage] 键索引不可用（{ex.Message}），DeleteAll 可能漏删机密");
                set.Clear();
            }

            return set;
        }

        /// <summary>把键集合写回索引；集合为空时直接删除索引键。</summary>
        /// <param name="set">当前完整键集合。</param>
        private static void WriteIndex(System.Collections.Generic.HashSet<string> set)
        {
            if (set.Count == 0)
            {
                PlayerPrefs.DeleteKey(IndexKey);
                return;
            }

            byte[] enc = AesHelper.Encrypt(AesHelper.Purpose.SecureStorage, EncodeIndex(set));
            string mac = AesHelper.HmacSha256Hex(AesHelper.Purpose.SecureStorage, enc);
            PlayerPrefs.SetString(IndexKey, Convert.ToBase64String(enc) + "." + mac);
        }

        /// <summary>把键集合编码为 <c>长度:键</c> 连缀。长度前缀使任意键内容都不产生歧义。</summary>
        /// <param name="set">键集合。</param>
        /// <returns>编码后的文本。</returns>
        private static string EncodeIndex(System.Collections.Generic.HashSet<string> set)
        {
            var builder = new System.Text.StringBuilder();
            foreach (string key in set)
                builder.Append(key.Length).Append(':').Append(key);
            return builder.ToString();
        }

        /// <summary>解析 <see cref="EncodeIndex"/> 的输出。格式不符即抛出，由调用方按索引损坏处理。</summary>
        /// <param name="text">编码文本。</param>
        /// <param name="output">解析出的键写入此集合。</param>
        private static void DecodeIndex(string text, System.Collections.Generic.HashSet<string> output)
        {
            int position = 0;
            while (position < text.Length)
            {
                int colon = text.IndexOf(':', position);
                if (colon <= position)
                    throw new FormatException("索引缺少长度前缀");

                if (!int.TryParse(text.Substring(position, colon - position), out int length) || length <= 0)
                    throw new FormatException("索引长度前缀非法");

                int start = colon + 1;
                if (start + length > text.Length)
                    throw new FormatException("索引长度前缀超出文本范围");

                output.Add(text.Substring(start, length));
                position = start + length;
            }
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
