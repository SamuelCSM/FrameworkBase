using System;

namespace Framework.HotUpdate
{
    /// <summary>
    /// 一次内容发行（Release）的客户端安装记录。
    /// <para>
    /// 统一身份：<see cref="ReleaseId"/> 复用服务端已签名清单的 ManifestId
    /// （发布侧 GenerateManifest 步骤把 ReleaseContext.ReleaseId 写入清单该字段），
    /// 使代码、资源、配置与发布台账共享同一个身份，禁止各自派生互不关联的 ID。
    /// </para>
    /// <para>序列化契约：字段经 JsonSerializers 持久化到 release-state.json，新增字段必须给默认值并递增
    /// <see cref="ContentReleaseState.SchemaVersion"/>；空 <see cref="ReleaseId"/> 表示"无记录"。</para>
    /// </summary>
    [Serializable]
    public sealed class ContentReleaseRecord
    {
        /// <summary>发行身份（= 服务端清单 ManifestId）；空字符串表示无记录。</summary>
        public string ReleaseId = string.Empty;

        /// <summary>发行所属整包版本。跨 AppVersion 的记录在启动准备阶段被隔离，不参与恢复。</summary>
        public string AppVersion = string.Empty;

        /// <summary>发行资源版本。</summary>
        public int ResourceVersion;

        /// <summary>发行代码版本。</summary>
        public int CodeVersion;

        /// <summary>关联的代码槽 ID（HotUpdateSlotManager）；本次无代码更新时为空。</summary>
        public string CodeSlotId = string.Empty;

        /// <summary>本次是否包含资源（Catalog/bundle）更新。</summary>
        public bool ResourceChanged;

        /// <summary>本次是否包含代码更新。</summary>
        public bool CodeChanged;

        /// <summary>本次是否安装了新配置数据库（其 .bak 生命周期由 ConfigDatabaseInstaller 管理）。</summary>
        public bool ConfigChanged;

        /// <summary>安装前是否创建了 Catalog 缓存快照（回滚依据）。</summary>
        public bool HasCatalogSnapshot;

        /// <summary>安装（进入 Pending）时间。</summary>
        public long InstalledAtUnixSeconds;

        /// <summary>是否为空记录（无发行）。</summary>
        public bool IsEmpty => string.IsNullOrEmpty(ReleaseId);

        /// <summary>深拷贝（状态迁移时避免共享可变引用）。</summary>
        public ContentReleaseRecord Clone() => new ContentReleaseRecord
        {
            ReleaseId = ReleaseId,
            AppVersion = AppVersion,
            ResourceVersion = ResourceVersion,
            CodeVersion = CodeVersion,
            CodeSlotId = CodeSlotId,
            ResourceChanged = ResourceChanged,
            CodeChanged = CodeChanged,
            ConfigChanged = ConfigChanged,
            HasCatalogSnapshot = HasCatalogSnapshot,
            InstalledAtUnixSeconds = InstalledAtUnixSeconds,
        };
    }

    /// <summary>
    /// 内容发行事务的持久化状态（Pending / Active / LastKnownGood 三指针 + 内容级崩溃循环计数）。
    /// <para>
    /// 与代码槽状态（install-state.json）的关系：代码槽事务只覆盖热更 DLL；本状态把
    /// Catalog 快照与配置数据库纳入同一确认边界，三者共享统一启动确认点。
    /// 状态迁移必须经 ContentReleaseTransaction 完成并原子写盘，禁止外部直接改字段落盘。
    /// </para>
    /// </summary>
    [Serializable]
    public sealed class ContentReleaseState
    {
        /// <summary>状态文件结构版本；字段变更必须递增并保持旧版本可读（失败安全：读不懂即重置）。</summary>
        public int SchemaVersion = 1;

        /// <summary>状态所属整包版本；不一致时整个状态被隔离重置。</summary>
        public string AppVersion = string.Empty;

        /// <summary>已安装未确认的发行。存在即说明上次启动未走到统一确认点，下次启动必须回滚。</summary>
        public ContentReleaseRecord Pending = new ContentReleaseRecord();

        /// <summary>当前生效的发行（最近一次确认成功）。</summary>
        public ContentReleaseRecord Active = new ContentReleaseRecord();

        /// <summary>最近一次确认成功的发行（回滚参照）。</summary>
        public ContentReleaseRecord LastKnownGood = new ContentReleaseRecord();

        /// <summary>
        /// 连续"存在已确认发行但启动未到达确认点"的次数（内容级崩溃循环计数，语义与代码槽
        /// UnconfirmedLaunchCount 一致）。超过阈值触发内容级出厂回退：清空 Catalog 缓存与全部记录。
        /// </summary>
        public int UnconfirmedLaunchCount;

        /// <summary>状态最近更新时间。</summary>
        public long UpdatedAtUnixSeconds;
    }
}
