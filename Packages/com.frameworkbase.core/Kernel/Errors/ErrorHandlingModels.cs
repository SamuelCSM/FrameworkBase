namespace Framework.Core.Errors
{
    /// <summary>
    /// 错误处理反应类型：错误码字典把服务端返回码映射到这些统一动作，
    /// 业务不再散落 if (code == …) 手写分支。
    /// </summary>
    public enum ErrorReaction
    {
        /// <summary>静默：只记日志/埋点，不打扰玩家（如幂等重复提交）。</summary>
        Silent = 0,

        /// <summary>轻提示（Toast）：可自愈的普通失败（余额不足、冷却中等）。</summary>
        Toast = 1,

        /// <summary>模态弹窗：需要玩家知晓并确认的失败。</summary>
        Popup = 2,

        /// <summary>带重试的模态弹窗：网络类/可重试失败。</summary>
        PopupRetry = 3,

        /// <summary>强制登出：会话失效/被顶号/封禁——回登录界面。</summary>
        ForceLogout = 4,

        /// <summary>停服维护：进维护页/公告。</summary>
        Maintenance = 5,
    }

    /// <summary>单条错误码规则（注册进 <see cref="ErrorCodeRegistry"/>）。</summary>
    public struct ErrorRule
    {
        /// <summary>处理反应。</summary>
        public ErrorReaction Reaction;

        /// <summary>
        /// 文案键：注入 localizer 后按键翻译；未注入时原样显示（可直接写中文）。
        /// 留空表示优先用服务端随包下发的 message，都没有时用注册表默认文案。
        /// </summary>
        public string MessageKey;

        public ErrorRule(ErrorReaction reaction, string messageKey = null)
        {
            Reaction = reaction;
            MessageKey = messageKey;
        }
    }

    /// <summary>一次错误处理的最终决策（Resolve 产物，交给呈现器执行）。</summary>
    public struct ErrorDecision
    {
        /// <summary>原始错误码。</summary>
        public int Code;

        /// <summary>处理反应。</summary>
        public ErrorReaction Reaction;

        /// <summary>最终展示文案（已过 localizer / 服务端 message 回退链）。</summary>
        public string Message;

        public override string ToString() => $"code={Code} reaction={Reaction} msg={Message}";
    }

    /// <summary>
    /// 错误决策呈现器：执行 Toast/弹窗/登出广播等动作。
    /// 框架默认实现走 TipManager + GameMessage 广播（弹窗降级 Toast）；
    /// 业务接入自己的弹窗系统后经 <see cref="ErrorCenter.SetPresenter"/> 替换。
    /// </summary>
    public interface IErrorPresenter
    {
        void Present(ErrorDecision decision);
    }

    /// <summary>
    /// 客户端本地合成错误码（负数段）。
    /// 分段约定：0 = 成功；正数 = 服务端下发（1~999 框架建议保留，业务 ≥1000 按模块分段）；
    /// 负数 = 客户端本地合成（服务器永远不会下发负数，两侧空间天然不冲突）。
    /// </summary>
    public static class ClientErrorCodes
    {
        /// <summary>请求超时（客户端本地判定）。</summary>
        public const int Timeout = -1;

        /// <summary>连接断开/不可用。</summary>
        public const int Disconnected = -2;

        /// <summary>响应解析失败（协议不匹配/数据损坏）。</summary>
        public const int ParseError = -3;
    }
}
