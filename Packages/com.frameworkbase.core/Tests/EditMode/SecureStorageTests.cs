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

        [Test]
        public void EncryptedPrefs_DeleteAll_RemovesAllKeys()
        {
            var s = new EncryptedPrefsSecureStorage();
            string k1 = UniqueKey();
            string k2 = UniqueKey();
            s.Set(k1, "v1");
            s.Set(k2, "v2");
            Assert.IsTrue(s.Contains(k1));
            Assert.IsTrue(s.Contains(k2));

            // RTBF / 账号注销：一次抹除后端写入的全部键。
            s.DeleteAll();

            Assert.IsFalse(s.Contains(k1));
            Assert.IsFalse(s.Contains(k2));
            Assert.IsFalse(s.TryGet(k1, out _));
            Assert.IsFalse(s.TryGet(k2, out _));
        }

        [Test]
        public void EncryptedPrefs_ReservedIndexKey_IsRejected_AndKeepsIndexIntact()
        {
            var s = new EncryptedPrefsSecureStorage();
            string real = UniqueKey();
            s.Set(real, "v");

            // 业务误用保留键 "__index__"：应被拒绝（不写入、读不到、Contains=false），
            // 且不覆盖 / 污染 DeleteAll 依赖的键索引。
            s.Set("__index__", "attacker");
            Assert.IsFalse(s.Contains("__index__"));
            Assert.IsFalse(s.TryGet("__index__", out _));

            // 真实键仍在，索引未被污染，DeleteAll 仍能抹除它。
            Assert.IsTrue(s.Contains(real));
            s.DeleteAll();
            Assert.IsFalse(s.Contains(real));
        }

        // ── 能力接口（ISecureStorageBulkErase）兼容降级 ──────────────────────

        /// <summary>只实现 ISecureStorage、不实现 ISecureStorageBulkErase 的后端（模拟既有 / 扩展包后端）。</summary>
        private sealed class NoBulkEraseStorage : ISecureStorage
        {
            public string Name => "no-bulk-erase";
            public bool TryGet(string key, out string value) { value = null; return false; }
            public void Set(string key, string value) { }
            public bool Contains(string key) => false;
            public void Delete(string key) { }
            // 刻意不实现 ISecureStorageBulkErase：验证不支持整体抹除的后端能正常编译（无破坏）。
        }

        [Test]
        public void SecureStorage_DeleteAll_Throws_WhenBackendLacksBulkErase()
        {
            SecureStorage.SetBackend(new NoBulkEraseStorage());
            // 后端未声明批量抹除能力：统一入口显式失败（而非静默漏删机密），供 RTBF 报告如实计失败。
            Assert.Throws<System.NotSupportedException>(() => SecureStorage.DeleteAll());
        }

        [Test]
        public void SecureStorage_DeleteAll_Erases_WhenBackendSupportsBulkErase()
        {
            var mem = new InMemorySecureStorage();
            mem.Set("a", "1");
            SecureStorage.SetBackend(mem);

            SecureStorage.DeleteAll(); // InMemory 实现 ISecureStorageBulkErase
            Assert.IsFalse(mem.Contains("a"));
        }

        // ── InMemory DeleteAll ──────────────────────────────────────────────

        [Test]
        public void InMemory_DeleteAll_Clears()
        {
            var s = new InMemorySecureStorage();
            s.Set("a", "1");
            s.Set("b", "2");

            s.DeleteAll();

            Assert.IsFalse(s.Contains("a"));
            Assert.IsFalse(s.Contains("b"));
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
