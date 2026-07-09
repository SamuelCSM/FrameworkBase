using Cysharp.Threading.Tasks;

namespace Framework.Save.Cloud
{
    /// <summary>
    /// 云存档后端抽象（框架主干只有这条缝，零厂商依赖）。
    /// 具体实现（Google Play Games Saved Games / iCloud / 自建服务端等）进扩展包，
    /// 经 <see cref="CloudSaveSync.SetBackend"/> 注入。
    /// </summary>
    /// <remarks>
    /// 契约要点：后端<b>只按 key 存取不透明字节 + 元数据</b>，不解读正文、不做冲突判定
    /// （冲突判定在 <see cref="CloudSaveSync"/>，保持后端"哑存储"、可任意替换）。
    /// key 由上层给出并唯一标识一条存档（建议含账号+类型+槽位，如 <c>u_10001/PlayerData_0</c>）。
    /// </remarks>
    public interface ICloudSaveBackend
    {
        /// <summary>后端名（日志/诊断用）。</summary>
        string Name { get; }

        /// <summary>后端当前是否可用（已登录云服务、有网）。不可用时同步保持本地权威。</summary>
        UniTask<bool> IsAvailableAsync();

        /// <summary>
        /// 按 key 拉取云端存档；不存在返回 null。
        /// </summary>
        /// <param name="key">存档唯一标识。</param>
        /// <returns>云端存档条目，或 null。</returns>
        UniTask<CloudSaveRecord> DownloadAsync(string key);

        /// <summary>
        /// 按 key 上传存档（覆盖云端同 key 条目）。
        /// </summary>
        /// <param name="key">存档唯一标识。</param>
        /// <param name="record">待上传存档条目。</param>
        UniTask UploadAsync(string key, CloudSaveRecord record);

        /// <summary>
        /// 删除云端 key 对应的存档（不存在时应静默成功）。
        /// </summary>
        /// <param name="key">存档唯一标识。</param>
        UniTask DeleteAsync(string key);
    }
}
