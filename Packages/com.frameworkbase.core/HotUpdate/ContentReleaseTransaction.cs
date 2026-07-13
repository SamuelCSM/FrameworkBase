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

        /// <summary>当前实现支持的最高状态结构版本（与 <see cref="ContentReleaseState.SchemaVersion"/> 默认值一致）。</summary>
        private const int CurrentSchemaVersion = 2;

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

        /// <summary>最近一次 Load 是否因状态文件损坏而重置（触发启动准备阶段的安全兜底）。</summary>
        private bool _stateWasCorrupt;

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

            /// <summary>
            /// 检测到确认阶段被中断的提交（<see cref="ContentReleaseState.CommitInProgress"/> 为 true），
            /// 已在内容侧前滚补完（Pending→Active+LKG）。调用方（LaunchFlow）必须继续前滚补完配置与
            /// version.json，随后调用 <see cref="EndCommit"/> 清除日志。
            /// </summary>
            public bool CommitReplayed;

            /// <summary>被前滚补完的提交记录（承载 version.json 重建所需字段）；<see cref="CommitReplayed"/> 为 true 时有效。</summary>
            public ContentReleaseRecord CommittingRecord = new ContentReleaseRecord();

            /// <summary>
            /// 状态文件损坏且无法证明当前内容已确认时执行了安全兜底：优先恢复 Catalog 快照
            /// （<see cref="CatalogRestored"/> 为 true），无快照可恢复则回退出厂（<see cref="FactoryResetPerformed"/> 为 true）。
            /// 调用方据此同步恢复/重置配置数据库。
            /// </summary>
            public bool StateCorruptionHandled;
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
        /// 是否存在被中断的确认阶段提交（确认阶段崩溃原子性日志）。协调方（HotUpdateManager）据此决定：
        /// 代码槽在启动恢复时执行<b>前滚确认</b>（幂等）而非回滚，与内容/配置/version 的前滚保持一致。
        /// 状态文件损坏时读不到日志，返回 false，退化为常规回滚（由启动准备阶段的安全兜底另行处理）。
        /// </summary>
        public bool IsCommitInProgress => State.CommitInProgress;

        /// <summary>
        /// 启动准备（必须在 Addressables 初始化之前调用）。优先级从高到低：
        /// 状态损坏安全兜底 → 确认阶段中断提交前滚补完 → 整包版本隔离 →
        /// 未确认 Pending 回滚（恢复 Catalog 快照）→ 内容级 Crash-loop 检测与出厂回退。
        /// </summary>
        public LaunchRecoveryReport PrepareForLaunch()
        {
            var report = new LaunchRecoveryReport();
            ContentReleaseState state = State;

            // ── 0. 状态文件损坏安全兜底：无法证明当前内容已确认时，绝不继续信任磁盘上的 Catalog ──
            // 损坏可能恰好发生在事务中途，此刻缓存里可能是"未确认的新 Catalog"，与旧代码槽错配。
            // 策略（分层）：能恢复快照就优先恢复（回滚到上一份 LKG Catalog）；无快照可恢复才回退出厂
            // （清空 Catalog 缓存回到包内基线）。配置数据库由调用方按同一判据恢复/重置。
            if (_stateWasCorrupt)
            {
                _stateWasCorrupt = false;
                report.StateCorruptionHandled = true;
                if (_catalogSnapshot.HasSnapshot)
                {
                    report.CatalogRestored = _catalogSnapshot.RestoreSnapshot();
                    if (report.CatalogRestored)
                        _logError("[ContentRelease] 状态文件损坏：已恢复上一份 LKG Catalog 快照（安全兜底）。");
                }
                if (!report.CatalogRestored)
                {
                    // 无快照或快照恢复失败：回退出厂，宁可强制重下也不放行可能错配的当前内容。
                    _catalogSnapshot.ResetCacheToFactory();
                    report.FactoryResetPerformed = true;
                    _logError("[ContentRelease] 状态文件损坏且无可用快照：已回退出厂 Catalog 基线（安全兜底）。");
                }
                _state = NewState();
                Save();
                return report;
            }

            // ── 1. 整包版本隔离：旧 AppVersion 的 Pending/Active/提交日志/快照一律不参与本次启动 ──
            // 必须早于提交前滚：若内容提交被中断后应用又整包升级，旧版本的中断提交不得前滚，一律隔离重置。
            if (!string.Equals(state.AppVersion, _appVersion, StringComparison.Ordinal))
            {
                if (!string.IsNullOrEmpty(state.AppVersion))
                    _log($"[ContentRelease] 整包版本已变化：{state.AppVersion} -> {_appVersion}，内容发行状态重置。");
                _state = NewState();
                _catalogSnapshot.Discard();
                Save();
                return report;
            }

            // ── 1.5 确认阶段中断的提交：前滚补完，绝不回滚 ────────────────────────────
            // CommitInProgress 只在 HotfixEntry.Start 成功后、四类内容确认全部落盘前为 true，
            // 说明本次发行已被证明可启动。此处把内容侧前滚（Pending→Active+LKG，丢弃快照），
            // 并交由调用方前滚补完配置与 version.json，最后 EndCommit 清除日志。
            if (state.CommitInProgress)
            {
                report.CommitReplayed = true;
                report.CommittingRecord = state.Committing.IsEmpty
                    ? state.Active.Clone()
                    : state.Committing.Clone();
                if (!state.Pending.IsEmpty)
                    ConfirmPending(); // 幂等：Pending→Active+LKG，丢弃快照
                _logError($"[ContentRelease] 检测到确认阶段被中断的提交 {report.CommittingRecord.ReleaseId}，" +
                          "内容侧已前滚补完，待调用方补完配置与 version.json 后清除提交日志。");
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
        /// 进入统一确认提交阶段：必须作为确认点的<b>第一个动作</b>调用，早于代码槽/内容/配置/version 的任何确认落盘。
        /// 把当前 Pending 记入提交日志（<see cref="ContentReleaseState.CommitInProgress"/>=true）并落盘——
        /// 这是"回滚 ↔ 前滚"的原子开关：此后进程即使被杀，下次启动也会前滚补完而非回滚。
        /// 无 Pending（本次无新发行）时为空操作；重复调用同一发行时幂等。
        /// </summary>
        public void BeginCommit()
        {
            ContentReleaseState state = State;
            if (state.Pending.IsEmpty)
                return; // 本次无新发行可提交：健康启动确认无需提交日志
            if (state.CommitInProgress &&
                string.Equals(state.Committing.ReleaseId, state.Pending.ReleaseId, StringComparison.Ordinal))
            {
                return; // 幂等：同一发行的提交日志已存在
            }

            state.Committing = state.Pending.Clone();
            state.CommitInProgress = true;
            state.UpdatedAtUnixSeconds = Now();
            Save();
            _log($"[ContentRelease] 进入确认提交阶段 {state.Committing.ReleaseId}，此后任何中断一律前滚补完。");
        }

        /// <summary>
        /// 结束统一确认提交阶段：必须作为确认点的<b>最后一个动作</b>调用，晚于代码槽/内容/配置/version 的全部确认落盘。
        /// 清除提交日志（<see cref="ContentReleaseState.CommitInProgress"/>=false）。无进行中提交时为空操作（幂等）。
        /// </summary>
        public void EndCommit()
        {
            ContentReleaseState state = State;
            if (!state.CommitInProgress)
                return; // 幂等

            string id = state.Committing.ReleaseId;
            state.CommitInProgress = false;
            state.Committing = new ContentReleaseRecord();
            state.UpdatedAtUnixSeconds = Now();
            Save();
            _log($"[ContentRelease] 确认提交完成 {id}，提交日志已清除。");
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
                {
                    // 反序列化得到 null 视同损坏：交由启动准备阶段安全兜底（恢复快照/回退出厂），不静默继续。
                    _stateWasCorrupt = true;
                    return NewState();
                }
                // 版本兼容：v1（无提交日志字段）向前兼容，缺失字段取默认值；结构版本高于当前实现（降级安装）
                // 无法证明语义，按损坏处理走安全兜底，绝不猜测后继续信任当前内容。
                if (state.SchemaVersion < 1 || state.SchemaVersion > CurrentSchemaVersion)
                {
                    _logError($"[ContentRelease] 状态文件 SchemaVersion={state.SchemaVersion} 不受支持，按损坏处理安全兜底。");
                    _stateWasCorrupt = true;
                    return NewState();
                }
                // 反序列化器可能把缺失对象置 null，统一补空记录，避免下游空引用。
                state.Pending ??= new ContentReleaseRecord();
                state.Active ??= new ContentReleaseRecord();
                state.LastKnownGood ??= new ContentReleaseRecord();
                state.Committing ??= new ContentReleaseRecord();
                return state;
            }
            catch (Exception ex)
            {
                // 失败安全：损坏的状态文件不再"当作无事务继续"，而是标记损坏，交由 PrepareForLaunch
                // 安全兜底——无法证明当前内容已确认时，优先恢复快照，无快照则回退出厂。
                _logError($"[ContentRelease] 状态文件损坏，将走启动安全兜底：{ex.Message}");
                _stateWasCorrupt = true;
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
