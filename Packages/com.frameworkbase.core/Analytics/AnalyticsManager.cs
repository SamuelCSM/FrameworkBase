using System;
using System.Collections.Generic;
using System.IO;
using Cysharp.Threading.Tasks;
using Framework.Core;
using Framework.Core.Privacy;
using Framework.Storage;
using UnityEngine;

namespace Framework.Analytics
{
    /// <summary>
    /// 埋点事件管道：业务只管 Track，管道负责公共维度封装、缓冲、批量上报、
    /// 失败退避与切后台/退出时落盘防丢。
    ///
    /// 事件信封（序列化时冻结，用户维度以事件发生时刻为准）：
    /// <c>{ event, ts, session_id, device_id, user_id, app_version, channel, props{...} }</c>
    ///
    /// 后端选择：默认按 AppConfig.AnalyticsUrl——非空用 <see cref="HttpJsonAnalyticsBackend"/>，
    /// 留空用 <see cref="LogAnalyticsBackend"/>（开发期看日志）；
    /// 对接三方平台经 <see cref="SetBackend"/> 注入扩展包实现。
    /// </summary>
    public class AnalyticsManager : FrameworkComponent<AnalyticsManager>
    {
        // ── 管道参数（经验默认值，够用且不吃内存）─────────────────────────────
        /// <summary>内存队列上限：超限丢最旧事件（丢弃计数随下批事件补报）。</summary>
        private const int MaxQueuedEvents = 500;

        /// <summary>单批最大事件数。</summary>
        private const int BatchSize = 50;

        /// <summary>定时冲刷间隔（秒）。</summary>
        private const float FlushIntervalSeconds = 15f;

        /// <summary>连续失败的退避上限（秒）。</summary>
        private const float MaxBackoffSeconds = 120f;

        /// <summary>单次冲刷的排水批次上限：一次触发最多连发这么多批，避免积压过大时长时间连续打网络。</summary>
        private const int MaxBatchesPerFlush = 20;

        /// <summary>断电落盘文件名（JSON Lines）。</summary>
        private const string PendingFileName = "analytics_pending.jsonl";

        /// <summary>落盘文件体积上限：超限截断丢弃（埋点丢比撑爆存储可接受）。</summary>
        private const long MaxPendingFileBytes = 512 * 1024;

        // ── 状态 ─────────────────────────────────────────────────────────────
        private readonly List<string> _queue = new List<string>();
        private IAnalyticsBackend _backend;
        private bool _isFlushing;
        private float _flushTimer;
        private int _consecutiveFailures;
        private float _backoffRemaining;
        private int _droppedSinceLastReport;

        private string _sessionId;
        private string _deviceId;
        private string _appVersion;
        private string _userId = string.Empty;
        private string _pendingFilePath;
        private bool _privacyConsentRequired;
        private int _privacyPolicyVersion = 1;
        private EventSubscription _privacyConsentSubscription;

        /// <summary>
        /// 采集闸门（隐私合规）：false 时 Track 直接丢弃（数据根本不产生，而非缓存后不发）、
        /// FlushAsync 不出网。合规市场在用户同意隐私协议前置 false，同意后置 true
        /// （接线见 Core/Privacy/PRIVACY_GUIDE.md）。默认 true 保持既有行为。
        /// </summary>
        public bool CollectionEnabled { get; set; } = true;

        /// <summary>当前会话 ID（每次启动一个）。</summary>
        public string SessionId => _sessionId;

        /// <summary>当前队列长度（监控/测试用）。</summary>
        public int QueuedCount => _queue.Count;

        public override void OnInit()
        {
            _sessionId = Guid.NewGuid().ToString("N");
            _deviceId = SystemInfo.deviceUniqueIdentifier;
            _appVersion = Application.version;
            _pendingFilePath = Path.Combine(Application.persistentDataPath, PendingFileName);

            ApplyPrivacyGateFromConfig();
            RegisterPrivacyConsentListener();

            if (CollectionEnabled)
            {
                LoadPendingFromDisk();
            }
            else
            {
                TryDeletePendingFile();
            }

            GameLog.Log($"[AnalyticsManager] 初始化 session={_sessionId} 待补报={_queue.Count}");
        }

        // ── 对外 API ─────────────────────────────────────────────────────────

        /// <summary>
        /// 注入自定义后端（三方平台扩展包）。应在首次 Track 前调用；
        /// 运行中切换不丢队列（已序列化事件与后端无关）。
        /// </summary>
        public void SetBackend(IAnalyticsBackend backend)
        {
            if (backend == null)
            {
                GameLog.Error("[AnalyticsManager] SetBackend 传入 null，忽略");
                return;
            }
            _backend = backend;
            GameLog.Log($"[AnalyticsManager] 埋点后端: {backend.Name}");
        }

        /// <summary>登录成功后设置用户维度；登出传空。</summary>
        public void SetUserId(string userId)
        {
            _userId = userId ?? string.Empty;
        }

        /// <summary>
        /// 记录一条事件。属性只支持扁平键值（string/bool/整数/浮点，其余 ToString）。
        /// 线程约束：仅主线程调用（与 Manager 生命周期一致）。
        /// </summary>
        public void Track(string eventName, IReadOnlyDictionary<string, object> properties = null)
        {
            if (string.IsNullOrEmpty(eventName))
            {
                GameLog.Warning("[AnalyticsManager] Track 事件名为空，忽略");
                return;
            }

            // 隐私合规闸门：未同意前数据根本不产生（不进队列、不落盘），而非缓存后补发
            if (!CollectionEnabled)
                return;

#if UNITY_EDITOR || DEVELOPMENT_BUILD
            // 事件字典校验（仅开发期，正式包零开销）：违规打 Error 就地暴露，但不拦截发送——
            // 埋点宁脏勿丢，修正闭环靠开发期告警，不靠线上丢事件。
            List<string> violations = AnalyticsSchemaRegistry.Shared.Validate(eventName, properties);
            for (int i = 0; i < violations.Count; i++)
                GameLog.Error($"[AnalyticsManager] 事件字典违规: {violations[i]}");
#endif

            // 丢弃补报：队列曾溢出时，把丢弃数作为质量信号随后续事件带出
            if (_droppedSinceLastReport > 0)
            {
                int dropped = _droppedSinceLastReport;
                _droppedSinceLastReport = 0;
                EnqueueSerialized(AnalyticsJson.SerializeEvent(
                    NewEventId(), "analytics_dropped", NowMs(), _sessionId, _deviceId, _userId, _appVersion, ChannelName(),
                    new Dictionary<string, object> { { "count", dropped } }));
            }

            EnqueueSerialized(AnalyticsJson.SerializeEvent(
                NewEventId(), eventName, NowMs(), _sessionId, _deviceId, _userId, _appVersion, ChannelName(), properties));

            if (_queue.Count >= BatchSize)
                FlushAsync().Forget();
        }

        /// <summary>
        /// 冲刷队列：排水式连发多批（每批 ≤<see cref="BatchSize"/>），直到队列空、遇到失败、
        /// 或达到单次上限 <see cref="MaxBatchesPerFlush"/>。空闲期积压不必再等下个定时周期慢慢发。
        /// 返回本次是否全部成功（空队列视为成功；中途失败即返回 false 并退避）。
        /// </summary>
        public async UniTask<bool> FlushAsync()
        {
            if (!CollectionEnabled)
                return true; // 合规闸门关闭：不出网（同意前的残留队列留在本地，抹除走 ClearQueue）

            if (_isFlushing || _queue.Count == 0)
                return true;

            _isFlushing = true;
            try
            {
                int batchesThisRound = 0;
                while (_queue.Count > 0 && batchesThisRound < MaxBatchesPerFlush)
                {
                    int count = Math.Min(BatchSize, _queue.Count);
                    var batch = new List<string>(count);
                    for (int i = 0; i < count; i++)
                        batch.Add(_queue[i]);

                    bool ok = await Backend().SendAsync(batch);
                    if (!ok)
                    {
                        _consecutiveFailures++;
                        _backoffRemaining = Math.Min(
                            FlushIntervalSeconds * _consecutiveFailures, MaxBackoffSeconds);
                        GameLog.Warning($"[AnalyticsManager] 上报失败（连续 {_consecutiveFailures} 次），退避 {_backoffRemaining:F0}s，队列 {_queue.Count}");
                        return false;
                    }

                    _queue.RemoveRange(0, count);
                    _consecutiveFailures = 0;
                    _backoffRemaining = 0f;
                    batchesThisRound++;
                }

                // 队列已排空：删除落盘快照，避免"回前台发完 → 进程被杀 → 重启重读旧文件"造成的重复补报。
                // （仍可能有"发到一半被杀"的窄窗口重复，靠采集端按 event_id 去重兜底。）
                if (_queue.Count == 0)
                    TryDeletePendingFile();

                return true;
            }
            finally
            {
                _isFlushing = false;
            }
        }

        /// <summary>清空队列（测试隔离 / 合规抹除用）。</summary>
        public void ClearQueue()
        {
            _queue.Clear();
            _droppedSinceLastReport = 0;
            TryDeletePendingFile();
        }

        // ── 生命周期 ─────────────────────────────────────────────────────────

        public override void OnUpdate(float deltaTime)
        {
            if (_backoffRemaining > 0f)
            {
                _backoffRemaining -= deltaTime;
                return;
            }

            _flushTimer += deltaTime;
            if (_flushTimer >= FlushIntervalSeconds)
            {
                _flushTimer = 0f;
                FlushAsync().Forget();
            }
        }

        public override void OnApplicationPause(bool isPaused)
        {
            if (!isPaused)
                return;

            // 切后台：先落盘保命（进程可能随时被杀），再尽力发一批
            PersistQueueToDisk();
            FlushAsync().Forget();
        }

        public override void OnShutdown()
        {
            _privacyConsentSubscription?.Unsubscribe();
            _privacyConsentSubscription = null;

            if (CollectionEnabled)
                PersistQueueToDisk();
            else
                ClearQueue();

            _queue.Clear();
        }

        // ── 内部 ─────────────────────────────────────────────────────────────

        private void EnqueueSerialized(string eventJson)
        {
            if (_queue.Count >= MaxQueuedEvents)
            {
                _queue.RemoveAt(0);
                _droppedSinceLastReport++;
            }
            _queue.Add(eventJson);
        }

        private void ApplyPrivacyGateFromConfig()
        {
            AppConfigAsset config = AppConfig.Load();
            _privacyConsentRequired = config != null && config.RequirePrivacyConsentForAnalytics;
            _privacyPolicyVersion = config != null ? Math.Max(1, config.PrivacyPolicyVersion) : 1;

            if (_privacyConsentRequired)
                CollectionEnabled = PrivacyConsent.IsAccepted(_privacyPolicyVersion);
        }

        private void RegisterPrivacyConsentListener()
        {
            if (!_privacyConsentRequired || GameEntry.Event == null)
                return;

            _privacyConsentSubscription = GameEntry.Event.Subscribe<int>(
                GameMessage.PrivacyConsentChanged,
                OnPrivacyConsentChanged);
        }

        private void OnPrivacyConsentChanged(int acceptedPolicyVersion)
        {
            if (!_privacyConsentRequired)
                return;

            CollectionEnabled = acceptedPolicyVersion >= _privacyPolicyVersion && _privacyPolicyVersion > 0;
            if (!CollectionEnabled)
                ClearQueue();
        }

        /// <summary>取当前后端；未注入时按 AppConfig 惰性选择默认实现。</summary>
        private IAnalyticsBackend Backend()
        {
            if (_backend != null)
                return _backend;

            string url = AppConfig.Load()?.AnalyticsUrl;
            _backend = string.IsNullOrEmpty(url)
                ? (IAnalyticsBackend)new LogAnalyticsBackend()
                : new HttpJsonAnalyticsBackend(url);
            GameLog.Log($"[AnalyticsManager] 默认埋点后端: {_backend.Name}");
            return _backend;
        }

        private string ChannelName()
        {
            // GameEntry 未接线（纯单测环境）时渠道维度留空
            return Core.GameEntry.Sdk != null ? Core.GameEntry.Sdk.ChannelName : string.Empty;
        }

        private static long NowMs() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        /// <summary>生成事件唯一幂等键（采集端去重锚点）。</summary>
        private static string NewEventId() => Guid.NewGuid().ToString("N");

        /// <summary>队列落盘（JSON Lines 覆盖写）。切后台/退出时调用，崩溃防丢。</summary>
        private void PersistQueueToDisk()
        {
            try
            {
                if (_queue.Count == 0)
                {
                    TryDeletePendingFile();
                    return;
                }

                long total = 0;
                var lines = new List<string>(_queue.Count);
                foreach (string line in _queue)
                {
                    total += line.Length + 1;
                    if (total > MaxPendingFileBytes)
                        break;
                    lines.Add(line);
                }

                FileStorages.Shared.WriteLines(_pendingFilePath, lines.ToArray());
            }
            catch (Exception ex)
            {
                GameLog.Warning($"[AnalyticsManager] 落盘失败（放弃本次持久化）: {ex.Message}");
            }
        }

        /// <summary>启动时回捞上次未发出的事件（读完即删，避免重复补报）。</summary>
        private void LoadPendingFromDisk()
        {
            try
            {
                if (!FileStorages.Shared.FileExists(_pendingFilePath))
                    return;

                foreach (string line in FileStorages.Shared.ReadLines(_pendingFilePath))
                {
                    if (!string.IsNullOrWhiteSpace(line))
                        EnqueueSerialized(line);
                }
                TryDeletePendingFile();
            }
            catch (Exception ex)
            {
                GameLog.Warning($"[AnalyticsManager] 读取待补报事件失败: {ex.Message}");
                TryDeletePendingFile();
            }
        }

        private void TryDeletePendingFile()
        {
            FileStorages.Shared.TryDeleteFile(_pendingFilePath);
        }
    }
}
