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

        /// <summary>
        /// 整体抹除当前后端写入的<b>全部</b>机密（账号注销 / RTBF 被遗忘权用）。
        /// <para>
        /// 统一入口：仅当后端实现可选能力 <see cref="ISecureStorageBulkErase"/> 时执行；否则抛
        /// <see cref="System.NotSupportedException"/>。刻意<b>不</b>把该语义塞进 <see cref="ISecureStorage"/>
        /// 主接口（避免破坏既有 / 扩展包后端编译），而由此入口做能力探测——不支持时显式失败，
        /// 让 RTBF 合规流程如实计入失败项（而非静默漏删机密）。
        /// </para>
        /// </summary>
        public static void DeleteAll()
        {
            ISecureStorage backend = Shared;
            if (backend is ISecureStorageBulkErase eraser)
            {
                eraser.DeleteAll();
                return;
            }

            throw new System.NotSupportedException(
                $"安全存储后端 \"{backend.Name}\" 未实现 ISecureStorageBulkErase：无法整体抹除其机密" +
                "（RTBF / 账号注销）。请让该后端实现 ISecureStorageBulkErase。");
        }
    }
}
