namespace Framework.Security
{
    /// <summary>
    /// 安全存储访问入口与后端注册点。
    ///
    /// 默认走 <see cref="EncryptedPrefsSecureStorage"/>（无需接线即可用，比明文强的兜底）；
    /// 接入硬件级实现（Keychain / Keystore 扩展包）时，在业务启动早期
    /// <see cref="SetBackend"/> 注入即可，调用方经 <see cref="Shared"/> 统一访问、不感知实现。
    /// </summary>
    public static class SecureStorage
    {
        private static ISecureStorage _backend;

        /// <summary>当前后端；未注入时惰性创建默认加密 Prefs 后端。</summary>
        public static ISecureStorage Shared => _backend ??= new EncryptedPrefsSecureStorage();

        /// <summary>当前后端名。</summary>
        public static string BackendName => Shared.Name;

        /// <summary>
        /// 注入安全存储后端（如渠道 / 平台扩展包的 Keychain / Keystore 实现）。
        /// 通常在业务启动早期调用一次；传 null 忽略。
        /// </summary>
        public static void SetBackend(ISecureStorage backend)
        {
            if (backend == null)
            {
                GameLog.Error("[SecureStorage] SetBackend 传入 null，忽略");
                return;
            }

            _backend = backend;
            GameLog.Log($"[SecureStorage] 后端已注入: {backend.Name}");
        }
    }
}
