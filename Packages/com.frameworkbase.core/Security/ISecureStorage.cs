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

    /// <summary>
    /// 可选能力：整体抹除本后端写入的<b>全部</b>机密（账号注销 / RTBF 被遗忘权用）。
    /// <para>
    /// 刻意与 <see cref="ISecureStorage"/> 分离为「能力接口」而非塞进主接口：整体抹除不是每个后端的
    /// 必备语义（且是危险操作），把它设为必选会<b>破坏既有 / 扩展包后端</b>（如 Keychain / Keystore）的
    /// 编译。后端<b>自愿</b>实现本接口来声明支持；不支持的后端保持只实现 <see cref="ISecureStorage"/> 即可。
    /// </para>
    /// <para>
    /// 调用方经 <see cref="SecureStorage.DeleteAll"/> 统一入口触发：当前后端未实现本能力时该入口显式抛
    /// <see cref="System.NotSupportedException"/>，让 RTBF 合规报告如实计失败（而非静默漏删机密）。
    /// 实现须清空自己管理的所有条目；无法枚举底层存储的实现（如 PlayerPrefs）应自行维护键索引。
    /// </para>
    /// </summary>
    public interface ISecureStorageBulkErase
    {
        /// <summary>删除本后端写入的全部键。</summary>
        void DeleteAll();
    }
}
