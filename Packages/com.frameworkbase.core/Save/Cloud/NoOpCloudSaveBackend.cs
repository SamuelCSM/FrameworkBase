using Cysharp.Threading.Tasks;

namespace Framework.Save.Cloud
{
    /// <summary>
    /// 默认云存档后端：云同步关闭。
    /// 未注入真实后端时兜底——<see cref="IsAvailableAsync"/> 恒 false，同步一律 Offline，
    /// 保证"没接云存档"时 API 可正常调用、游戏纯本地存档照常工作，不崩不阻塞。
    /// </summary>
    public sealed class NoOpCloudSaveBackend : ICloudSaveBackend
    {
        /// <inheritdoc/>
        public string Name => "NoOp";

        /// <inheritdoc/>
        public UniTask<bool> IsAvailableAsync() => UniTask.FromResult(false);

        /// <inheritdoc/>
        public UniTask<CloudSaveRecord> DownloadAsync(string key) => UniTask.FromResult<CloudSaveRecord>(null);

        /// <inheritdoc/>
        public UniTask UploadAsync(string key, CloudSaveRecord record) => UniTask.CompletedTask;

        /// <inheritdoc/>
        public UniTask DeleteAsync(string key) => UniTask.CompletedTask;
    }
}
