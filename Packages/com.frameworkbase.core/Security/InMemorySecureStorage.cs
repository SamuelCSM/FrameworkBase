using System.Collections.Generic;

namespace Framework.Security
{
    /// <summary>
    /// 内存安全存储：不落盘、不加密，仅进程内保存。供单测（不污染 PlayerPrefs）与
    /// 需要「重启即清空」语义的场景使用。<b>不具备任何持久化或安全性</b>，勿用于真实凭证持久化。
    /// </summary>
    public sealed class InMemorySecureStorage : ISecureStorage
    {
        private readonly Dictionary<string, string> _map = new Dictionary<string, string>();

        /// <inheritdoc />
        public string Name => "in-memory";

        /// <inheritdoc />
        public bool TryGet(string key, out string value)
        {
            if (!string.IsNullOrEmpty(key))
                return _map.TryGetValue(key, out value);

            value = null;
            return false;
        }

        /// <inheritdoc />
        public void Set(string key, string value)
        {
            if (!string.IsNullOrEmpty(key))
                _map[key] = value ?? string.Empty;
        }

        /// <inheritdoc />
        public bool Contains(string key) => !string.IsNullOrEmpty(key) && _map.ContainsKey(key);

        /// <inheritdoc />
        public void Delete(string key)
        {
            if (!string.IsNullOrEmpty(key))
                _map.Remove(key);
        }
    }
}
