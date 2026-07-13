using System;
using System.IO;
using Framework.Serialization;
using Framework.Storage;

namespace Framework.HotUpdate
{
    /// <summary>
    /// 内容发行事务（Pending / Active / LastKnownGood 状态机 + 持久化）。
    /// <para>
    /// 职责：把代码槽（HotUpdateSlotManager）、Addressables Catalog（CatalogCacheSnapshotManager）
    /// 与配置数据库（ConfigDatabaseInstaller）纳入同一个确认边界。统一确认点位于 LaunchFlow
    /// Step9（HotfixEntry.Start 成功之后），只有全部内容确认成功，Pending 才提升为 Active + LKG。
    /// </para>
    /// <para>
    /// 事务边界与恢复语义：
    /// 1. BeginPending 在任何内容安装动作（UpdateCatalogs / 配置替换 / 代码槽提交）之前落盘，
    ///    并先创建 Catalog 缓存快照——进程死在安装中途也能在下次启动依据 Pending 记录回滚；
    /// 2. 确认前任何失败（含进程被杀）：下次启动 PrepareForLaunch 检测到未确认 Pending，
    ///    恢复 Catalog 快照；配置恢复由调用方按 ConfigManager 备份状态执行（文件各自独立可恢复）；
    ///    代码槽回滚由 HotUpdateSlotManager.PrepareForLaunch 自身完成；
    /// 3. 已确认发行连续多次启动未到达确认点（内容级 Crash-loop）：清空 Catalog 缓存与全部记录，
    ///    回退包内出厂内容，与代码槽的出厂回退语义一致；
    /// 4. 状态文件损坏：失败安全——视为无 Pending，仅告警重置，禁止执行半信半疑的回滚。
    /// </para>
    /// <para>线程边界：仅在启动流程单线程串行使用；持久化全部走原子写。</para>
    /// </summary>
    public sealed class ContentReleaseTransaction
    {
        private const string StateFileName = "release-state.json";
        private const string SnapshotDirectoryName = "catalog-lkg";

        /// <summary>
        /// 同一已确认发行允许的最大连续未确认启动次数，超过即触发内容级出厂回退。
        /// 与 HotUpdateSlotManager.MaxUnconfirmedLaunchAttempts 保持一致的语义与阈值。
        /// </summary>
        public const int MaxUnconfirmedLaunchAttempts = 3;

        private readonly string _rootDirectory;
        private readonly string _appVersion;
        private readonly CatalogCacheSnapshotManager _catalogSnapshot;
        private readonly Action<string> _log;
        private readonly Action<string> _logError;
        private ContentReleaseState _state;

        private string StatePath => Path.Combine(_rootDirectory, StateFileName);

        /// <summary>启动准备阶段的恢复报告：调用方据此执行配置恢复与遥测上报。</summary>
        public sealed class LaunchRecoveryReport
        {
            /// <summary>检测到未确认 Pending 并已执行回滚。</summary>
            public bool PendingRolledBack;

            /// <summary>Catalog 缓存已从快照恢复。</summary>
            public bool CatalogRestored;

            /// <summary>触发了内容级出厂回退（Catalog 缓存清空 + 全部记录重置）。</summary>
            public bool FactoryResetPerformed;

            /// <summary>被回滚的发行 ID（诊断/遥测用）。</summary>
            public string RolledBackReleaseId = string.Empty;
        }

        /// <param name="rootDirectory">事务状态根目录（{persistent}/FrameworkBase/ContentRelease；测试注入临时目录）。</param>
        /// <param name="appVersion">当前整包版本（Application.version；测试注入）。</param>
        /// <param name="catalogCacheDirectory">Addressables Catalog 缓存目录（{persistent}/com.unity.addressables）。</param>
        /// <param name="log">普通日志回调。</param>
        /// <param name="logError">错误日志回调。</param>
        public ContentReleaseTransaction(
            string rootDirectory,
            string appVersion,
            string catalogCacheDirectory,
            Action<string> log = null,
            Action<string> logError = null)
        {
            if (string.IsNullOrEmpty(rootDirectory))
                throw new ArgumentException("事务状态根目录不能为空。", nameof(rootDirectory));
            if (string.IsNullOrEmpty(appVersion))
                throw new ArgumentException("整包版本不能为空。", nameof(appVersion));

            _rootDirectory = rootDirectory;
            _appVersion = appVersion;
            _log = log ?? (_ => { });
            _logError = logError ?? (_ => { });
            _catalogSnapshot = new CatalogCacheSnapshotManager(
                catalogCacheDirectory,
                Path.Combine(rootDirectory, SnapshotDirectoryName),
                _log,
                _logError);
        }

        /// <summary>当前待确认发行（只读快照；无 Pending 时 IsEmpty）。</summary>
        public ContentReleaseRecord Pending => State.Pending.Clone();

        /// <summary>当前生效发行（只读快照）。</summary>
        public ContentReleaseRecord Active => State.Active.Clone();

        /// <summary>最近一次确认成功的发行（只读快照）。</summary>
        public ContentReleaseRecord LastKnownGood => State.LastKnownGood.Clone();

        /// <summary>是否存在未确认 Pending。</summary>
        public bool HasPending => !State.Pending.IsEmpty;

        /// <summary>
        /// 启动准备（必须在 Addressables 初始化之前调用）：
        /// 整包版本隔离 → 未确认 Pending 回滚（恢复 Catalog 快照）→ 内容级 Crash-loop 检测与出厂回退。
        /// </summary>
        public LaunchRecoveryReport PrepareForLaunch()
        {
            var report = new LaunchRecoveryReport();
            ContentReleaseState state = State;

            // ── 1. 整包版本隔离：旧 AppVersion 的 Pending/Active/快照一律不参与本次启动 ──
            if (!string.Equals(state.AppVersion, _appVersion, StringComparison.Ordinal))
            {
                if (!string.IsNullOrEmpty(state.AppVersion))
                    _log($"[ContentRelease] 整包版本已变化：{state.AppVersion} -> {_appVersion}，内容发行状态重置。");
                _state = NewState();
                _catalogSnapshot.Discard();
                Save();
                return report;
            }

            // ── 2. 未确认 Pending：上次启动未走到统一确认点，执行内容回滚 ────────────
            if (!state.Pending.IsEmpty)
            {
                report.PendingRolledBack = true;
                report.RolledBackReleaseId = state.Pending.ReleaseId;

                // 恢复失败时保留 Pending 与快照：下一次启动重试恢复，直到成功或内容级
                // Crash-loop 触发出厂回退兜底。绝不在恢复未完成时清除回滚凭据。
                bool restoreNeeded = state.Pending.HasCatalogSnapshot && _catalogSnapshot.HasSnapshot;
                if (restoreNeeded)
                    report.CatalogRestored = _catalogSnapshot.RestoreSnapshot();
                else
                    _catalogSnapshot.Discard();

                if (restoreNeeded && !report.CatalogRestored)
                {
                    _logError($"[ContentRelease] 未确认发行 {state.Pending.ReleaseId} 的 Catalog 恢复失败，" +
                              "Pending 与快照保留，下次启动重试恢复。");
                }
                else
                {
                    _logError($"[ContentRelease] 检测到未确认发行 {state.Pending.ReleaseId}，已回滚" +
                              $"（Catalog 恢复={report.CatalogRestored}）。代码槽由槽管理器自行回滚，" +
                              "配置数据库由调用方按备份状态恢复。");
                    state.Pending = new ContentReleaseRecord();
                    Save();
                }
            }

            // ── 3. 内容级 Crash-loop：已确认发行也反复启动失败时回退出厂内容 ──────────
            if (!state.Active.IsEmpty)
            {
                state.UnconfirmedLaunchCount++;
                if (state.UnconfirmedLaunchCount > MaxUnconfirmedLaunchAttempts)
                {
                    _logError($"[ContentRelease] 发行 {state.Active.ReleaseId} 连续 " +
                              $"{state.UnconfirmedLaunchCount - 1} 次启动未确认，触发内容级出厂回退。");
                    _catalogSnapshot.ResetCacheToFactory();
                    _state = NewState();
                    Save();
                    report.FactoryResetPerformed = true;
                    return report;
                }
                Save();
            }

            return report;
        }

        /// <summary>
        /// 开启待确认发行。必须在任何内容安装动作（UpdateCatalogs / 配置替换 / 代码槽提交）之前调用：
        /// 先创建 Catalog 缓存快照，再把 Pending 落盘——落盘成功后进程无论死在哪一步都能回滚。
        /// </summary>
        /// <param name="record">发行描述（ReleaseId 必须来自已签名清单的 ManifestId）。</param>
        /// <returns>快照与落盘均成功返回 true；失败返回 false（调用方必须失败关闭，中止本次更新）。</returns>
        public bool BeginPending(ContentReleaseRecord record)
        {
            if (record == null || record.IsEmpty)
            {
                _logError("[ContentRelease] 拒绝开启空发行记录。");
                return false;
            }
            if (!string.Equals(record.AppVersion, _appVersion, StringComparison.Ordinal))
            {
                _logError($"[ContentRelease] 发行 AppVersion={record.AppVersion} 与当前整包 {_appVersion} 不一致，拒绝开启。");
                return false;
            }

            // Catalog 快照创建失败即失败关闭：没有快照就没有回滚能力，绝不允许"先更了再说"。
            if (!_catalogSnapshot.CreateSnapshot())
            {
                _logError("[ContentRelease] Catalog 快照创建失败，本次内容更新中止（失败关闭）。");
                return false;
            }

            record = record.Clone();
            record.HasCatalogSnapshot = true;
            record.InstalledAtUnixSeconds = Now();

            ContentReleaseState state = State;
            state.Pending = record;
            state.UpdatedAtUnixSeconds = Now();

            try
            {
                Save();
            }
            catch (Exception ex)
            {
                _logError($"[ContentRelease] Pending 状态落盘失败，本次内容更新中止：{ex.Message}");
                _catalogSnapshot.Discard();
                state.Pending = new ContentReleaseRecord();
                return false;
            }

            _log($"[ContentRelease] 待确认发行已开启：{record.ReleaseId}" +
                 $"（res={record.ResourceVersion}, code={record.CodeVersion}）。");
            return true;
        }

        /// <summary>
        /// 统一启动确认点：全部内容（Catalog/资源/配置/AOT/热更程序集/HotfixEntry.Start）成功后调用。
        /// Pending 提升为 Active + LastKnownGood，丢弃 Catalog 快照，清零崩溃循环计数。
        /// 无 Pending 时仅清零计数（已确认发行正常启动）。
        /// </summary>
        /// <returns>本次确认的发行记录；无 Pending 时返回 null。</returns>
        public ContentReleaseRecord ConfirmPending()
        {
            ContentReleaseState state = State;
            if (state.Pending.IsEmpty)
            {
                if (state.UnconfirmedLaunchCount != 0)
                {
                    state.UnconfirmedLaunchCount = 0;
                    state.UpdatedAtUnixSeconds = Now();
                    Save();
                }
                return null;
            }

            ContentReleaseRecord confirmed = state.Pending;
            state.Active = confirmed;
            state.LastKnownGood = confirmed.Clone();
            state.Pending = new ContentReleaseRecord();
            state.UnconfirmedLaunchCount = 0;
            state.UpdatedAtUnixSeconds = Now();
            Save();
            _catalogSnapshot.Discard();

            _log($"[ContentRelease] 发行已确认并提升为 LKG：{confirmed.ReleaseId}。");
            return confirmed.Clone();
        }

        /// <summary>
        /// 本进程内主动标记 Pending 失败（AOT/程序集/HotfixEntry 等步骤失败时由 LaunchFlow 调用）。
        /// 立即恢复 Catalog 快照文件（本进程已加载的 Catalog 无法卸载，调用方仍应中止本次启动）。
        /// </summary>
        /// <param name="reason">失败原因（诊断与遥测）。</param>
        public void MarkPendingFailed(string reason)
        {
            ContentReleaseState state = State;
            if (state.Pending.IsEmpty)
                return;

            string failedId = state.Pending.ReleaseId;
            bool restoreNeeded = state.Pending.HasCatalogSnapshot && _catalogSnapshot.HasSnapshot;
            bool restored = restoreNeeded && _catalogSnapshot.RestoreSnapshot();

            if (restoreNeeded && !restored)
            {
                // 恢复失败：保留 Pending 与快照作为回滚凭据，下次启动 PrepareForLaunch 重试。
                _logError($"[ContentRelease] 发行 {failedId} 标记失败（{reason}）但 Catalog 恢复失败，" +
                          "Pending 与快照保留待下次启动重试恢复。");
                return;
            }

            state.Pending = new ContentReleaseRecord();
            state.UpdatedAtUnixSeconds = Now();
            Save();

            _logError($"[ContentRelease] 发行 {failedId} 已标记失败（{reason}），" +
                      $"Catalog 恢复={restored}。本进程内已激活的 Catalog 无法卸载，请结束本次启动。");
        }

        private ContentReleaseState State => _state ??= Load();

        private ContentReleaseState Load()
        {
            try
            {
                if (!File.Exists(StatePath))
                    return NewState();
                ContentReleaseState state = JsonSerializers.Shared.FromJson<ContentReleaseState>(File.ReadAllText(StatePath));
                if (state == null)
                    return NewState();
                // 失败安全：结构版本超出当前实现认知时不猜测语义，重置为无状态（只影响回滚提示，不影响正确性）。
                if (state.SchemaVersion != 1)
                {
                    _logError($"[ContentRelease] 状态文件 SchemaVersion={state.SchemaVersion} 不受支持，已重置。");
                    return NewState();
                }
                // 反序列化器可能把缺失对象置 null，统一补空记录，避免下游空引用。
                state.Pending ??= new ContentReleaseRecord();
                state.Active ??= new ContentReleaseRecord();
                state.LastKnownGood ??= new ContentReleaseRecord();
                return state;
            }
            catch (Exception ex)
            {
                // 失败安全：损坏的状态文件视为"无事务状态"，仅告警，不执行半信半疑的回滚。
                _logError($"[ContentRelease] 状态文件损坏已重置：{ex.Message}");
                return NewState();
            }
        }

        private ContentReleaseState NewState() => new ContentReleaseState
        {
            AppVersion = _appVersion,
            UpdatedAtUnixSeconds = Now(),
        };

        private void Save()
        {
            Directory.CreateDirectory(_rootDirectory);
            ContentReleaseState state = State;
            state.AppVersion = _appVersion;
            FileStorages.Shared.AtomicWriteText(
                StatePath, JsonSerializers.Shared.ToJson(state, true), StatePath + ".bak");
        }

        private static long Now() => DateTimeOffset.UtcNow.ToUnixTimeSeconds();
    }
}
