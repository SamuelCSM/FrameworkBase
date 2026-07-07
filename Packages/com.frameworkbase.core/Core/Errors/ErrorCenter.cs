using System;
using System.Collections.Generic;

namespace Framework.Core.Errors
{
    /// <summary>
    /// 统一错误处理门面：业务收到服务端返回码只调一行
    /// <c>ErrorCenter.Shared.Handle(code, msg)</c>——查字典、执行反应（Toast/弹窗/登出广播）、
    /// 限流上报埋点，一站完成。规则注册见 <see cref="ErrorCodeRegistry"/>，
    /// 分段约定与接入方式见 ERROR_HANDLING_GUIDE.md。
    /// </summary>
    public sealed class ErrorCenter
    {
        /// <summary>同一错误码的埋点上报限流窗口（秒）：服务端批量报错时不刷爆埋点管道。</summary>
        private const double ReportThrottleSeconds = 60;

        private readonly ErrorCodeRegistry _registry;
        private IErrorPresenter _presenter;
        private readonly Func<double> _clockSeconds;
        private readonly Dictionary<int, double> _lastReportAt = new Dictionary<int, double>();

        private static ErrorCenter _shared;

        /// <summary>框架默认实例（挂 Shared 注册表 + 默认呈现器）。</summary>
        public static ErrorCenter Shared
        {
            get
            {
                if (_shared == null)
                    _shared = new ErrorCenter(ErrorCodeRegistry.Shared, new DefaultErrorPresenter());
                return _shared;
            }
            set => _shared = value;
        }

        /// <param name="registry">错误码注册表。</param>
        /// <param name="presenter">决策呈现器。</param>
        /// <param name="clockSeconds">单调时钟（秒），默认真实运行时钟；测试注入假时钟验证限流。</param>
        public ErrorCenter(ErrorCodeRegistry registry, IErrorPresenter presenter, Func<double> clockSeconds = null)
        {
            _registry = registry ?? throw new ArgumentNullException(nameof(registry));
            _presenter = presenter ?? throw new ArgumentNullException(nameof(presenter));
            _clockSeconds = clockSeconds ?? DefaultClock;
        }

        private static double DefaultClock() => UnityEngine.Time.realtimeSinceStartupAsDouble;

        /// <summary>替换呈现器（业务接入自己的弹窗/维护页系统）。</summary>
        public void SetPresenter(IErrorPresenter presenter)
        {
            if (presenter == null)
            {
                GameLog.Error("[ErrorCenter] SetPresenter 传入 null，忽略");
                return;
            }
            _presenter = presenter;
        }

        /// <summary>
        /// 处理一个服务端返回码。code == 0（成功）直接返回 Silent 决策，不产生任何动作。
        /// 返回决策供调用方需要时做额外分支（绝大多数调用点忽略返回值即可）。
        /// </summary>
        public ErrorDecision Handle(int code, string serverMessage = null)
        {
            if (code == 0)
                return new ErrorDecision { Code = 0, Reaction = ErrorReaction.Silent, Message = string.Empty };

            ErrorDecision decision = _registry.Resolve(code, serverMessage);

            try
            {
                _presenter.Present(decision);
            }
            catch (Exception ex)
            {
                // 呈现器异常不能反过来打断业务错误分支
                GameLog.Error($"[ErrorCenter] 呈现器异常（决策 {decision}）: {ex.Message}");
            }

            TrackThrottled(decision);
            return decision;
        }

        /// <summary>埋点上报（同码限流）：错误码分布是服务端异常的一手监控信号。</summary>
        private void TrackThrottled(ErrorDecision decision)
        {
            double now = _clockSeconds();
            if (_lastReportAt.TryGetValue(decision.Code, out double last) &&
                now - last < ReportThrottleSeconds)
            {
                return;
            }
            _lastReportAt[decision.Code] = now;

            GameEntry.Analytics?.Track("server_error", new Dictionary<string, object>
            {
                { "code", decision.Code },
                { "reaction", decision.Reaction.ToString() },
            });
        }
    }

    /// <summary>
    /// 框架默认呈现器：Toast 走 TipManager；弹窗类降级为 Error 样式 Toast（并日志提醒
    /// 接入业务弹窗）；强制登出/维护广播 GameMessage 事件由业务订阅执行跳转。
    /// TipManager 未就绪（纯单测/启动极早期）时全部降级日志。
    /// </summary>
    public sealed class DefaultErrorPresenter : IErrorPresenter
    {
        public void Present(ErrorDecision decision)
        {
            switch (decision.Reaction)
            {
                case ErrorReaction.Silent:
                    GameLog.Log($"[ErrorCenter] 静默错误 {decision}");
                    break;

                case ErrorReaction.Toast:
                    ShowTip(decision.Message, TipStyle.Warning);
                    break;

                case ErrorReaction.Popup:
                case ErrorReaction.PopupRetry:
                    // 框架不内置通用模态弹窗：降级 Toast 保证玩家有感知，业务接入弹窗后替换呈现器
                    GameLog.Warning($"[ErrorCenter] 弹窗类错误降级为 Toast（接入业务弹窗后 SetPresenter 替换）: {decision}");
                    ShowTip(decision.Message, TipStyle.Error);
                    break;

                case ErrorReaction.ForceLogout:
                    GameLog.Warning($"[ErrorCenter] 强制登出: {decision}");
                    ShowTip(decision.Message, TipStyle.Error);
                    GameEntry.Event?.Publish(GameMessage.ServerForceLogout, decision.Code);
                    break;

                case ErrorReaction.Maintenance:
                    GameLog.Warning($"[ErrorCenter] 停服维护: {decision}");
                    ShowTip(decision.Message, TipStyle.Error);
                    GameEntry.Event?.Publish(GameMessage.ServerMaintenance, decision.Code);
                    break;
            }
        }

        private static void ShowTip(string message, TipStyle style)
        {
            if (string.IsNullOrEmpty(message))
                return;

            var tips = GameEntry.Tips;
            if (tips != null)
                tips.ShowRaw(message, style);
            else
                GameLog.Warning($"[ErrorCenter] TipManager 未就绪，降级日志: {message}");
        }
    }
}
