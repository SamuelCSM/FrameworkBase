using System;
using System.Security.Cryptography;
using System.Text;

namespace Framework.Save.Cloud
{
    /// <summary>
    /// 云存档条目的同步元数据（不含存档正文，正文在 <see cref="CloudSaveRecord"/>）。
    /// 冲突判定只看这份元数据，不解密正文——后端与决策层都不接触明文。
    /// </summary>
    /// <remarks>
    /// <para><b>Version</b> 是"同步计数器"，每次成功上传 +1，是冲突判定的<b>首要依据</b>——
    /// 它与 <see cref="SaveData.dataVersion"/>（结构 schema 版本）是<b>两个不同概念</b>，别混。</para>
    /// <para><b>ContentHash</b> 是正文摘要：同 Version 下 hash 相同 = 内容一致（无需同步）；
    /// hash 不同 = 两端从同一基线各自改过 = 真冲突。</para>
    /// <para><b>TimestampUtc</b> 是墙钟(Unix 秒)，仅作冲突兜底与展示；不作首要依据（多设备时钟不可信）。</para>
    /// </remarks>
    [Serializable]
    public readonly struct CloudSaveMetadata
    {
        /// <summary>同步计数器：每次成功上传 +1，冲突判定首要依据。</summary>
        public readonly long Version;

        /// <summary>写入时的墙钟（Unix 秒，UTC）；冲突兜底与展示用。</summary>
        public readonly long TimestampUtc;

        /// <summary>正文摘要（同 Version 下判"内容是否一致"）。</summary>
        public readonly string ContentHash;

        /// <summary>写入方设备标识；用于"其它设备有更新存档"的提示，非判定依据。</summary>
        public readonly string DeviceId;

        /// <summary>构造同步元数据。</summary>
        public CloudSaveMetadata(long version, long timestampUtc, string contentHash, string deviceId)
        {
            Version = version;
            TimestampUtc = timestampUtc;
            ContentHash = contentHash ?? string.Empty;
            DeviceId = deviceId ?? string.Empty;
        }

        /// <summary>计算正文摘要（SHA-256 十六进制小写），用于填充 <see cref="ContentHash"/>。</summary>
        /// <param name="payload">存档正文字节（通常是已加密封包）。</param>
        /// <returns>十六进制摘要；空正文返回空串。</returns>
        public static string HashPayload(byte[] payload)
        {
            if (payload == null || payload.Length == 0)
                return string.Empty;

            using (var sha = SHA256.Create())
            {
                byte[] hash = sha.ComputeHash(payload);
                var sb = new StringBuilder(hash.Length * 2);
                foreach (byte b in hash)
                    sb.Append(b.ToString("x2"));
                return sb.ToString();
            }
        }
    }
}
