namespace Framework.Save.Cloud
{
    /// <summary>本地与云端比对后的同步动作。</summary>
    public enum CloudSyncDirection
    {
        /// <summary>两端一致或都无存档，无需同步。</summary>
        None,

        /// <summary>本地更新，应上传覆盖云端。</summary>
        Upload,

        /// <summary>云端更新，应下载覆盖本地。</summary>
        Download,

        /// <summary>同版本但内容分叉——真冲突，需冲突解决器裁决。</summary>
        Conflict,
    }

    /// <summary>一次同步的最终状态。</summary>
    public enum CloudSyncStatus
    {
        /// <summary>后端不可用（未接入/离线），保持本地权威，稍后重试。</summary>
        Offline,

        /// <summary>两端已一致，无操作。</summary>
        UpToDate,

        /// <summary>本地已上传到云端。</summary>
        Uploaded,

        /// <summary>云端更新已取回，<see cref="CloudSyncResult.DownloadedRecord"/> 待调用方落盘。</summary>
        Downloaded,
    }

    /// <summary>
    /// 同步结果。<see cref="Status"/> 为 <see cref="CloudSyncStatus.Downloaded"/> 时，
    /// <see cref="DownloadedRecord"/> 携带云端正文，由调用方（通常 SaveManager 侧）写回本地。
    /// </summary>
    public sealed class CloudSyncResult
    {
        /// <summary>同步最终状态。</summary>
        public CloudSyncStatus Status { get; }

        /// <summary>下载回来的云端存档；仅 <see cref="CloudSyncStatus.Downloaded"/> 时非空。</summary>
        public CloudSaveRecord DownloadedRecord { get; }

        private CloudSyncResult(CloudSyncStatus status, CloudSaveRecord downloaded)
        {
            Status = status;
            DownloadedRecord = downloaded;
        }

        /// <summary>后端不可用。</summary>
        public static readonly CloudSyncResult Offline = new CloudSyncResult(CloudSyncStatus.Offline, null);

        /// <summary>两端一致。</summary>
        public static readonly CloudSyncResult UpToDate = new CloudSyncResult(CloudSyncStatus.UpToDate, null);

        /// <summary>本地已上传。</summary>
        public static readonly CloudSyncResult Uploaded = new CloudSyncResult(CloudSyncStatus.Uploaded, null);

        /// <summary>云端已取回，携带待落盘正文。</summary>
        public static CloudSyncResult Downloaded(CloudSaveRecord record)
            => new CloudSyncResult(CloudSyncStatus.Downloaded, record);
    }
}
