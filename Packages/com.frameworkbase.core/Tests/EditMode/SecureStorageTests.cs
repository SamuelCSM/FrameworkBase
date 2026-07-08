using System.Collections.Generic;
using Framework.Security;
using NUnit.Framework;
using UnityEngine;

namespace Framework.Tests
{
    /// <summary>
    /// 安全存储单测：
    ///  - InMemorySecureStorage 语义；
    ///  - EncryptedPrefsSecureStorage 加密落盘往返 / 防篡改 / 删除（真实 PlayerPrefs，用唯一键 + 清理）；
    ///  - SecureStorage 注入路由。
    /// </summary>
    public class SecureStorageTests
    {
        private readonly List<string> _prefsKeysToClean = new List<string>();

        [TearDown]
        public void TearDown()
        {
            foreach (string k in _prefsKeysToClean)
                PlayerPrefs.DeleteKey(EncryptedPrefsSecureStorage.KeyPrefix + k);
            _prefsKeysToClean.Clear();
            PlayerPrefs.Save();
        }

        /// <summary>返回唯一键并登记清理，避免测试间 / 多次运行 PlayerPrefs 残留。</summary>
        private string UniqueKey()
        {
            string k = "test_" + System.Guid.NewGuid().ToString("N");
            _prefsKeysToClean.Add(k);
            return k;
        }

        // ── InMemory ────────────────────────────────────────────────────────

        [Test]
        public void InMemory_RoundTrip()
        {
            var s = new InMemorySecureStorage();
            Assert.IsFalse(s.TryGet("k", out _));

            s.Set("k", "v");
            Assert.IsTrue(s.Contains("k"));
            Assert.IsTrue(s.TryGet("k", out string got));
            Assert.AreEqual("v", got);

            s.Delete("k");
            Assert.IsFalse(s.Contains("k"));
            Assert.IsFalse(s.TryGet("k", out _));
        }

        // ── EncryptedPrefs ──────────────────────────────────────────────────

        [Test]
        public void EncryptedPrefs_RoundTrip()
        {
            var s = new EncryptedPrefsSecureStorage();
            string key = UniqueKey();
            const string token = "session-token-abc.123-XYZ";

            Assert.IsFalse(s.TryGet(key, out _), "写入前应读不到");

            s.Set(key, token);
            Assert.IsTrue(s.Contains(key));
            Assert.IsTrue(s.TryGet(key, out string got));
            Assert.AreEqual(token, got);
        }

        [Test]
        public void EncryptedPrefs_StoredValueIsNotPlaintext()
        {
            var s = new EncryptedPrefsSecureStorage();
            string key = UniqueKey();
            const string secret = "PLAINTEXT_SECRET_MARKER";

            s.Set(key, secret);
            string raw = PlayerPrefs.GetString(EncryptedPrefsSecureStorage.KeyPrefix + key);
            StringAssert.DoesNotContain(secret, raw, "落盘内容不应含明文");
        }

        [Test]
        public void EncryptedPrefs_TamperedValue_ReadsAsMiss()
        {
            var s = new EncryptedPrefsSecureStorage();
            string key = UniqueKey();
            s.Set(key, "v");

            // 外部改写密文（模拟篡改）：HMAC 校验应失败，按读不到处理。
            PlayerPrefs.SetString(EncryptedPrefsSecureStorage.KeyPrefix + key, "dGFtcGVy.deadbeef");
            PlayerPrefs.Save();

            Assert.IsFalse(s.TryGet(key, out string got));
            Assert.IsNull(got);
        }

        [Test]
        public void EncryptedPrefs_Delete()
        {
            var s = new EncryptedPrefsSecureStorage();
            string key = UniqueKey();
            s.Set(key, "v");
            Assert.IsTrue(s.Contains(key));

            s.Delete(key);
            Assert.IsFalse(s.Contains(key));
            Assert.IsFalse(s.TryGet(key, out _));
        }

        // ── SecureStorage 注入路由 ──────────────────────────────────────────

        [Test]
        public void SecureStorage_SetBackend_Routes()
        {
            var mem = new InMemorySecureStorage();
            SecureStorage.SetBackend(mem);

            Assert.AreSame(mem, SecureStorage.Shared);
            Assert.AreEqual("in-memory", SecureStorage.BackendName);

            SecureStorage.Shared.Set("k", "v");
            Assert.IsTrue(mem.TryGet("k", out string got));
            Assert.AreEqual("v", got);
        }
    }
}
