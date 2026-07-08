namespace Framework.Security
{
    /// <summary>
    /// 凭证 / 机密的安全存储抽象。
    ///
    /// 定位：登录令牌、刷新令牌等<b>敏感串</b>的持久化落点——强联网项目要「跨重启静默重登」
    /// 就得把会话令牌存起来，而它<b>不该落进普通存档</b>（<see cref="Framework.Save.SaveManager"/>
    /// 是账号数据，非机密保险箱）。
    ///
    /// <para>默认 <see cref="SecureStorage.Shared"/> = <see cref="EncryptedPrefsSecureStorage"/>
    /// （设备密钥 AES + HMAC over PlayerPrefs，比明文强、防篡改，但<b>非硬件级</b>）。真正硬件级
    /// （iOS Keychain / Android Keystore）由扩展包实现本接口，经 <see cref="SecureStorage.SetBackend"/>
    /// 注入——与崩溃后端 / SDK 同款「主干接口 + 平台实现」模式，主干不含平台原生代码。</para>
    ///
    /// <para>契约：实现须线程安全或明确单线程使用；任何 IO / 解密失败都折算为「读不到」
    /// （<see cref="TryGet"/> 返回 false），不得抛异常打断业务。</para>
    /// </summary>
    public interface ISecureStorage
    {
        /// <summary>后端标识（日志用，如 "encrypted-prefs" / "keychain" / "keystore"）。</summary>
        string Name { get; }

        /// <summary>读取；不存在 / 损坏 / 被篡改时返回 false 且 <paramref name="value"/> 为 null。</summary>
        bool TryGet(string key, out string value);

        /// <summary>写入 / 覆盖。</summary>
        void Set(string key, string value);

        /// <summary>是否存在该键（不校验内容完整性）。</summary>
        bool Contains(string key);

        /// <summary>删除该键（不存在时静默）。</summary>
        void Delete(string key);
    }
}
