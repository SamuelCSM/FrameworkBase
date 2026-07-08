using Framework.Core.Errors;

namespace Framework.Core
{
    /// <summary>
    /// 框架默认错误呈现器（Framework 层）：Toast 走 TipManager；弹窗类降级为 Error 样式 Toast
    /// （并日志提醒接入业务弹窗）；强制登出/维护广播 GameMessage 事件由业务订阅执行跳转。
    /// TipManager 未就绪（纯单测/启动极早期）时全部降级日志。
    ///
    /// <para>由组合根 GameEntry 在 Manager 就绪后经 <see cref="ErrorCenter.SetPresenter"/> 注入。
    /// 之所以放在 Framework 层而非 Kernel：它反向依赖 Tips/Event/GameEntry，而 ErrorCenter
    /// 所在的 Kernel 层必须保持零上行依赖（见 ADR-002）。</para>
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
