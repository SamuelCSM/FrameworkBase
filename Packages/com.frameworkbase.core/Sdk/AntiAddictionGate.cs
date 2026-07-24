using System;
using System.Threading;
using Cysharp.Threading.Tasks;

namespace Framework.Sdk
{
    /// <summary>
    /// 防沉迷门控编排（框架增值层，架在 <see cref="ISdkComplianceService"/> 缝之上）。
    /// <para>
    /// 单纯的"查一次时长裁决"接口对业务价值有限——防沉迷的实际价值在<b>持续门控</b>。本类把渠道缝之外
    /// 该由框架统一做的三件事收口：① 周期上报在线时长心跳 + 拉取最新裁决；② 订阅渠道主动下发的裁决变更；
    /// ③ 裁决状态变化时抛 <see cref="RestrictionChanged"/>，业务据此封玩 / 宵禁提示 / 放行。
    /// </para>
    /// <para>
    /// 框架<b>不定任何法规规则</b>（宵禁时段/时长上限由渠道或游戏服裁决），也<b>不管封玩怎么表现</b>
    /// （弹合规文案 + 挡输入 + 踢回登录属业务与表现策略）。所有裁决来源（渠道推送、周期拉取、乃至业务
    /// 自游戏服拿到的裁决）都汇流到 <see cref="ApplyVerdict"/> 单一入口，保证封玩判定不分叉。
    /// </para>
    /// </summary>
    public sealed class AntiAddictionGate
    {
        private readonly ISdkComplianceService _compliance;

        // 上次已抛出的状态：用于状态变化去重，避免同状态每次心跳都惊动业务。基线 Allowed（未触发限制）。
        private SdkPlaytimeState _lastState = SdkPlaytimeState.Allowed;
        private bool _subscribed;
        private CancellationTokenSource _loopCts;

        /// <summary>当前最新裁决；未获取过为 null。</summary>
        public SdkPlaytimeVerdict CurrentVerdict { get; private set; }

        /// <summary>当前是否禁玩（宵禁时段 / 时长用尽 / 未实名被拦）。</summary>
        public bool IsBlocked => CurrentVerdict != null && CurrentVerdict.State == SdkPlaytimeState.Blocked;

        /// <summary>
        /// 裁决状态发生变化时触发（去重：同状态不重复抛）。业务订阅执行封玩/解封；
        /// 剩余秒数等细节从 <see cref="CurrentVerdict"/> 读。
        /// </summary>
        public event Action<SdkPlaytimeVerdict> RestrictionChanged;

        /// <summary>构造门控，绑定合规能力（非 null）。</summary>
        /// <param name="compliance">渠道合规能力（<c>GameEntry.Sdk.Compliance</c>）。</param>
        public AntiAddictionGate(ISdkComplianceService compliance)
        {
            _compliance = compliance ?? throw new ArgumentNullException(nameof(compliance));
        }

        /// <summary>
        /// 应用一次裁决（纯同步核，可单测）：更新 <see cref="CurrentVerdict"/>；仅当状态相对上次<b>变化</b>时抛
        /// <see cref="RestrictionChanged"/>。null 裁决忽略。渠道推送 / 周期拉取 / 业务自服务端拿到的裁决都走这里。
        /// </summary>
        /// <param name="verdict">时长裁决；null 忽略。</param>
        public void ApplyVerdict(SdkPlaytimeVerdict verdict)
        {
            if (verdict == null)
                return;

            CurrentVerdict = verdict;
            if (verdict.State != _lastState)
            {
                _lastState = verdict.State;
                RestrictionChanged?.Invoke(verdict);
            }
        }

        /// <summary>
        /// 查一次防沉迷裁决并应用。查询失败<b>维持现状</b>（不擅自封玩也不擅自解封）并记日志——
        /// 网络抖动不该误封正常玩家，也不该误放已被限制的玩家。
        /// </summary>
        public async UniTask RefreshAsync()
        {
            SdkResult<SdkPlaytimeVerdict> result = await _compliance.QueryPlaytimeAsync();
            if (result != null && result.Success && result.Data != null)
                ApplyVerdict(result.Data);
            else
                GameLog.Warning($"[AntiAddiction] 查询时长裁决失败 code={result?.Code}，维持现状");
        }

        /// <summary>
        /// 启动周期门控：订阅渠道下发 + 立即查一次 + 每 <paramref name="heartbeatSeconds"/> 上报心跳并复查，
        /// 直到 <see cref="Stop"/> 或外部令牌取消。幂等（已启动再调忽略）。
        /// </summary>
        /// <param name="heartbeatSeconds">心跳/复查间隔秒数（下限 1）。</param>
        /// <param name="externalToken">外部取消令牌（会话/退登），与内部停止合并。</param>
        public void Start(int heartbeatSeconds = 60, CancellationToken externalToken = default)
        {
            if (_loopCts != null)
                return; // 已启动

            _loopCts = CancellationTokenSource.CreateLinkedTokenSource(externalToken);
            if (!_subscribed)
            {
                _compliance.OnPlaytimeVerdictChanged += ApplyVerdict;
                _subscribed = true;
            }
            RunLoopAsync(Math.Max(1, heartbeatSeconds), _loopCts.Token).Forget();
        }

        /// <summary>停止周期门控（退登 / 退出时）。幂等。</summary>
        public void Stop()
        {
            if (_subscribed)
            {
                _compliance.OnPlaytimeVerdictChanged -= ApplyVerdict;
                _subscribed = false;
            }
            if (_loopCts != null)
            {
                _loopCts.Cancel();
                _loopCts.Dispose();
                _loopCts = null;
            }
        }

        /// <summary>周期循环：启动即查一次，之后每拍上报心跳并复查裁决。取消即静默退出。</summary>
        private async UniTaskVoid RunLoopAsync(int heartbeatSeconds, CancellationToken token)
        {
            try
            {
                await RefreshAsync();
                while (!token.IsCancellationRequested)
                {
                    await UniTask.Delay(TimeSpan.FromSeconds(heartbeatSeconds), cancellationToken: token);
                    // 上报在线时长（渠道据此累计并可能翻新裁决），再拉最新裁决应用。
                    await _compliance.ReportPlaytimeHeartbeatAsync(heartbeatSeconds);
                    await RefreshAsync();
                }
            }
            catch (OperationCanceledException)
            {
                // Stop / 外部取消：正常退出。
            }
        }
    }
}
