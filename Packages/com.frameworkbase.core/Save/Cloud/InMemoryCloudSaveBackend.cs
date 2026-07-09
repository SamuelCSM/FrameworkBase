using System.Collections.Generic;
using Cysharp.Threading.Tasks;

namespace Framework.Save.Cloud
{
    /// <summary>
    /// 内存云存档后端：进程内字典模拟云端，供单测与编辑器联调用。
    /// 不落盘、不跨进程；<see cref="Available"/> 可切换以模拟离线。
    /// </summary>
    public sealed class InMemoryCloudSaveBackend : ICloudSaveBackend
    {
        private readonly Dictionary<string, CloudSaveRecord> _store = new Dictionary<string, CloudSaveRecord>();

        /// <summary>后端是否可用（置 false 模拟离线/未登录）。</summary>
        public bool Available { get; set; } = true;

        /// <inheritdoc/>
        public string Name => "InMemory";

        /// <summary>当前云端条目数（断言用）。</summary>
        public int Count => _store.Count;

        /// <inheritdoc/>
        public UniTask<bool> IsAvailableAsync() => UniTask.FromResult(Available);

        /// <inheritdoc/>
        public UniTask<CloudSaveRecord> DownloadAsync(string key)
        {
            _store.TryGetValue(key, out CloudSaveRecord record);
            return UniTask.FromResult(record);
        }

        /// <inheritdoc/>
        public UniTask UploadAsync(string key, CloudSaveRecord record)
        {
            _store[key] = record;
            return UniTask.CompletedTask;
        }

        /// <inheritdoc/>
        public UniTask DeleteAsync(string key)
        {
            _store.Remove(key);
            return UniTask.CompletedTask;
        }

        /// <summary>直接注入云端条目（构造测试前置状态用）。</summary>
        public void Seed(string key, CloudSaveRecord record) => _store[key] = record;
    }
}
