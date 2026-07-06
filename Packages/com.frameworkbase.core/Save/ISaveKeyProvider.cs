using UnityEngine;

namespace Framework.Save
{
    /// <summary>
    /// 存档主密钥来源抽象。
    ///
    /// 加解密 Key 与完整性 HMAC Key 都由这里返回的「主密钥种子」派生。
    /// 默认实现 <see cref="DeviceSaveKeyProvider"/> 绑定本设备 deviceUniqueIdentifier，
    /// 因此存档不可跨设备——这是当前的刻意设计（低门槛防作弊）。
    ///
    /// 若未来需要上云 / 跨设备存档，注入一个基于「账号 ID」或「服务端下发密钥」的实现即可，
    /// 无需改动 <see cref="AesHelper"/> / <see cref="SaveManager"/> 的内部逻辑：
    ///   SaveManager.Instance.SetSaveKeyProvider(new AccountSaveKeyProvider(serverKey));
    ///
    /// 注意：更换主密钥来源会使此前用旧来源加密的存档无法解密，需配合迁移策略。
    /// </summary>
    public interface ISaveKeyProvider
    {
        /// <summary>
        /// 返回派生加解密 / HMAC 密钥所用的主密钥种子。
        /// 要求：在目标作用域内稳定（同账号/同设备多次调用一致），且不易被外部公开推断。
        /// </summary>
        string GetMasterSecret();
    }

    /// <summary>
    /// 默认主密钥来源：绑定本设备 deviceUniqueIdentifier。
    /// 存档因此与设备一一对应，换设备后无法解密旧档（刻意的低门槛防作弊语义）。
    /// </summary>
    public sealed class DeviceSaveKeyProvider : ISaveKeyProvider
    {
        /// <summary>以设备唯一标识作为主密钥种子。</summary>
        public string GetMasterSecret() => SystemInfo.deviceUniqueIdentifier;
    }
}
