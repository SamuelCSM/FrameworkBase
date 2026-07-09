using System;
using Cysharp.Threading.Tasks;
using Framework.Core;

namespace Framework.Save.Cloud
{
    /// <summary>
    /// 云存档同步编排器（框架主干）。职责：拉云端元数据 → 决策上传/下载/冲突 → 执行。
    /// 后端经 <see cref="SetBackend"/> 注入，默认 <see cref="NoOpCloudSaveBackend"/>（云同步关闭）。
    /// </summary>
    /// <remarks>
    /// <para><b>离线优先</b>：本地存档永远权威可玩，云同步是叠加的尽力而为层——后端不可用即 Offline，
    /// 不阻断任何本地读写（与崩溃后端本地兜底、远程配置 last-known-good 同一哲学）。</para>
    /// <para><b>决策与 IO 分离</b>：核心 <see cref="Decide"/> 是纯函数（零 IO/零 Unity，可直接单测）；
    /// <see cref="SyncAsync"/> 只透过 <see cref="ICloudSaveBackend"/> 做 IO，故配 InMemory 后端亦可整链单测。</para>
    /// <para><b>冲突默认策略</b>：<see cref="ResolveConflictByTimestamp"/>（新时间戳胜、并列保本地）。
    /// 时间戳裁决会丢数据——<b>有价值的存档应传入自定义合并解决器</b>（如背包取并集、货币取大值）。</para>
    /// </remarks>
    public static class CloudSaveSync
    {
        private static ICloudSaveBackend _backend;

        /// <summary>当前云存档后端（未注入时为关闭态的 NoOp）。</summary>
        public static ICloudSaveBackend Backend => _backend ??= new NoOpCloudSaveBackend();

        /// <summary>是否已接入真实云后端（NoOp 视为未接入）。</summary>
        public static bool IsEnabled => !(Backend is NoOpCloudSaveBackend);

        /// <summary>
        /// 注入云存档后端（扩展包/组合根启动时调用）。传 null 回退关闭态 NoOp。
        /// </summary>
        /// <param name="backend">云存档后端实现。</param>
        public static void SetBackend(ICloudSaveBackend backend)
        {
            _backend = backend ?? new NoOpCloudSaveBackend();
            GameLog.Log($"[CloudSaveSync] 后端已注入 → {_backend.Name}");
        }

        /// <summary>
        /// 纯决策：比对两端元数据得出同步动作（零 IO，可单测）。
        /// </summary>
        /// <param name="hasLocal">本地是否有存档。</param>
        /// <param name="local">本地元数据（无本地时忽略）。</param>
        /// <param name="hasCloud">云端是否有存档。</param>
        /// <param name="cloud">云端元数据（无云端时忽略）。</param>
        /// <returns>同步动作。</returns>
        public static CloudSyncDirection Decide(bool hasLocal, in CloudSaveMetadata local, bool hasCloud, in CloudSaveMetadata cloud)
        {
            if (!hasLocal && !hasCloud)
                return CloudSyncDirection.None;
            if (hasLocal && !hasCloud)
                return CloudSyncDirection.Upload;
            if (!hasLocal && hasCloud)
                return CloudSyncDirection.Download;

            // 两端都有：先比同步计数器
            if (cloud.Version > local.Version)
                return CloudSyncDirection.Download;
            if (local.Version > cloud.Version)
                return CloudSyncDirection.Upload;

            // 同版本：内容一致则无需同步，否则真冲突
            return string.Equals(local.ContentHash, cloud.ContentHash, StringComparison.Ordinal)
                ? CloudSyncDirection.None
                : CloudSyncDirection.Conflict;
        }

        /// <summary>
        /// 默认冲突解决器：新时间戳胜；时间戳并列则保本地（上传）。确定性、不抛异常。
        /// </summary>
        /// <param name="local">本地元数据。</param>
        /// <param name="cloud">云端元数据。</param>
        /// <returns>裁决出的方向（Upload 保本地 / Download 取云端）。</returns>
        public static CloudSyncDirection ResolveConflictByTimestamp(CloudSaveMetadata local, CloudSaveMetadata cloud)
        {
            if (cloud.TimestampUtc > local.TimestampUtc)
                return CloudSyncDirection.Download;
            return CloudSyncDirection.Upload; // 本地更新或并列 → 保本地
        }

        /// <summary>
        /// 执行一次同步：后端可用性检查 → 拉云端 → <see cref="Decide"/> → 冲突裁决 → 上传/下载。
        /// 下载结果由调用方落盘（编排器不碰文件 IO，保持可测）。
        /// </summary>
        /// <param name="key">存档唯一标识（建议含账号+类型+槽位）。</param>
        /// <param name="local">本地存档条目；无本地存档传 null。</param>
        /// <param name="conflictResolver">
        /// 冲突解决器；为空用 <see cref="ResolveConflictByTimestamp"/>。返回 Upload/Download 之外的值按保本地处理。
        /// </param>
        /// <returns>同步结果；Downloaded 时携带待落盘正文。</returns>
        public static async UniTask<CloudSyncResult> SyncAsync(
            string key,
            CloudSaveRecord local,
            Func<CloudSaveMetadata, CloudSaveMetadata, CloudSyncDirection> conflictResolver = null)
        {
            if (string.IsNullOrEmpty(key))
                return CloudSyncResult.UpToDate;

            if (!await Backend.IsAvailableAsync())
                return CloudSyncResult.Offline;

            CloudSaveRecord cloud = await Backend.DownloadAsync(key);
            bool hasLocal = local != null;
            bool hasCloud = cloud != null;

            CloudSyncDirection dir = Decide(
                hasLocal, hasLocal ? local.Metadata : default,
                hasCloud, hasCloud ? cloud.Metadata : default);

            if (dir == CloudSyncDirection.Conflict)
            {
                dir = conflictResolver != null
                    ? conflictResolver(local.Metadata, cloud.Metadata)
                    : ResolveConflictByTimestamp(local.Metadata, cloud.Metadata);
            }

            switch (dir)
            {
                case CloudSyncDirection.Upload:
                    await Backend.UploadAsync(key, local);
                    GameLog.Log($"[CloudSaveSync] 上传 key={key} v{local.Metadata.Version}");
                    return CloudSyncResult.Uploaded;

                case CloudSyncDirection.Download:
                    GameLog.Log($"[CloudSaveSync] 下载 key={key} v{cloud.Metadata.Version}（待落盘）");
                    return CloudSyncResult.Downloaded(cloud);

                default:
                    return CloudSyncResult.UpToDate;
            }
        }
    }
}
