namespace Framework.Save.Cloud
{
    /// <summary>
    /// 一条完整云存档 = 同步元数据 + 存档正文字节。
    /// 正文对后端<b>不透明</b>（通常是 SaveManager 产出的已加密封包），后端只负责按 key 存取字节。
    /// </summary>
    public sealed class CloudSaveRecord
    {
        /// <summary>同步元数据（版本计数器、摘要、时间戳、设备）。</summary>
        public CloudSaveMetadata Metadata { get; }

        /// <summary>存档正文字节（后端不透明存储）。</summary>
        public byte[] Payload { get; }

        /// <summary>构造云存档条目。</summary>
        public CloudSaveRecord(CloudSaveMetadata metadata, byte[] payload)
        {
            Metadata = metadata;
            Payload = payload ?? System.Array.Empty<byte>();
        }

        /// <summary>
        /// 由正文与版本便捷构造：自动计算摘要、填入当前 UTC 时间。
        /// </summary>
        /// <param name="version">同步计数器（每次成功上传应递增）。</param>
        /// <param name="payload">存档正文字节。</param>
        /// <param name="deviceId">写入方设备标识（可空）。</param>
        /// <returns>云存档条目。</returns>
        public static CloudSaveRecord Create(long version, byte[] payload, string deviceId = null)
        {
            long now = System.DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            var meta = new CloudSaveMetadata(version, now, CloudSaveMetadata.HashPayload(payload), deviceId);
            return new CloudSaveRecord(meta, payload);
        }
    }
}
