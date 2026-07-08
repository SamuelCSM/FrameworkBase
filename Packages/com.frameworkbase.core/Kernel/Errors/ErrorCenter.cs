using System;
using System.Collections.Generic;

namespace Framework.Core.Errors
{
    /// <summary>
    /// 统一错误处理门面：业务收到服务端返回码只调一行
    /// <c>ErrorCenter.Shared.Handle(code, msg)</c>——查字典、执行反应（Toast/弹窗/登出广播）、
    /// 限流上报埋点，一站完成。规则注册见 <see cref="ErrorCodeRegistry"/>，
    /// 分段约定与接入方式见 ERROR_HANDLING_GUIDE.md。
    ///
    /// <para>本类属 Kernel 层，不反向依赖 Tips/Event/Analytics：呈现动作经
    /// <see cref="IErrorPresenter"/> 抽象外发，埋点经 <see cref="ErrorReported"/> 事件外发，
    /// 二者均由组合根（GameEntry）在上层注入/订阅。</para>
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

        /// <summary>
        /// 埋点上报钩子：一次错误经同码限流后触发一次（成功码不触发，被限流的重复码不触发）。
        /// 组合根订阅此事件把错误分布转发给埋点管道——ErrorCenter 自身（Kernel 层）
        /// 不认识 Analytics，保持零上行依赖。
        /// </summary>
        public event Action<ErrorDecision> ErrorReported;

        /// <summary>
        /// 框架默认实例（挂 Shared 注册表 + 仅日志兜底呈现器）。
        /// 兜底呈现器只写日志、不触达 Toast/事件广播；真正的 UI 呈现器
        /// （<c>DefaultErrorPresenter</c>，位于 Framework 层）由组合根 GameEntry 在 Manager 就绪后
        /// 经 <see cref="SetPresenter"/> 注入，从而 Kernel 层不反向依赖 Tips/Event。
        /// </summary>
        public static ErrorCenter Shared
        {
            get
            {
                if (_shared == null)
                    _shared = new ErrorCenter(ErrorCodeRegistry.Shared, new LoggingErrorPresenter());
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

            ReportThrottled(decision);
            return decision;
        }

        /// <summary>
        /// 埋点上报（同码限流）：错误码分布是服务端异常的一手监控信号。
        /// 限流通过后触发 <see cref="ErrorReported"/>，由上层组合根转发埋点。
        /// </summary>
        private void ReportThrottled(ErrorDecision decision)
        {
            double now = _clockSeconds();
            if (_lastReportAt.TryGetValue(decision.Code, out double last) &&
                now - last < ReportThrottleSeconds)
            {
                return;
            }
            _lastReportAt[decision.Code] = now;

            ErrorReported?.Invoke(decision);
        }
    }

    /// <summary>
    /// Kernel 内置兜底呈现器：只把决策写入日志，不触达 Toast/事件广播（Kernel 层不认识 Tips/Event）。
    /// 用于 <see cref="ErrorCenter.Shared"/> 在组合根注入真正 UI 呈现器之前的极早期，
    /// 以及纯单测环境。业务/框架接入 UI 呈现器后经 <see cref="ErrorCenter.SetPresenter"/> 替换。
    /// </summary>
    public sealed class LoggingErrorPresenter : IErrorPresenter
    {
        public void Present(ErrorDecision decision)
        {
            switch (decision.Reaction)
            {
                case ErrorReaction.Silent:
                    GameLog.Log($"[ErrorCenter] 静默错误 {decision}");
                    break;
                default:
                    GameLog.Warning($"[ErrorCenter] 错误（UI 呈现器未注入，仅日志）: {decision}");
                    break;
            }
        }
    }
}
