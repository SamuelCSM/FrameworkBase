using System;
using System.Collections.Generic;

namespace Framework.Core.Errors
{
    /// <summary>
    /// 协议错误码字典：code → 处理规则（反应类型 + 文案键）的表驱动注册中心。
    ///
    /// 解析优先级：精确注册 &gt; 区间注册（跨度小者优先，同跨度后注册覆盖）&gt; 默认规则。
    /// 区间用于模块级兜底——业务先给整段注册通用规则（如 2000~2999 商店段全 Toast），
    /// 再对个别码精确覆写（如 2001 需要弹窗），新增码天然有兜底不至于无提示。
    ///
    /// 文案回退链：MessageKey 经 localizer 翻译 → localizer 未注入/翻不出则原样用 key
    /// （key 可以直接写中文）→ key 为空用服务端随包 message → 都没有用默认文案。
    /// 纯逻辑可单测；框架默认实例 <see cref="Shared"/>，业务组合根启动时批量注册。
    /// </summary>
    public sealed class ErrorCodeRegistry
    {
        private struct RangeRule
        {
            public int From;
            public int To;
            public ErrorRule Rule;
            public int Order; // 注册顺序（同跨度后注册覆盖用）
        }

        private readonly Dictionary<int, ErrorRule> _exact = new Dictionary<int, ErrorRule>();
        private readonly List<RangeRule> _ranges = new List<RangeRule>();
        private Func<string, string> _localizer;
        private int _orderCounter;

        /// <summary>未命中任何注册时的默认规则（可整体替换）。</summary>
        public ErrorRule DefaultRule = new ErrorRule(ErrorReaction.Toast, "操作失败，请稍后重试");

        private static ErrorCodeRegistry _shared;

        /// <summary>框架默认注册表（业务组合根启动时注册；测试可 new 独立实例）。</summary>
        public static ErrorCodeRegistry Shared
        {
            get
            {
                if (_shared == null)
                    _shared = CreateWithFrameworkDefaults();
                return _shared;
            }
            set => _shared = value ?? CreateWithFrameworkDefaults();
        }

        /// <summary>创建带框架内置规则（客户端本地负数码段）的注册表。</summary>
        public static ErrorCodeRegistry CreateWithFrameworkDefaults()
        {
            var registry = new ErrorCodeRegistry();
            registry.Register(ClientErrorCodes.Timeout, new ErrorRule(ErrorReaction.PopupRetry, "网络请求超时，请重试"));
            registry.Register(ClientErrorCodes.Disconnected, new ErrorRule(ErrorReaction.PopupRetry, "网络连接已断开"));
            registry.Register(ClientErrorCodes.ParseError, new ErrorRule(ErrorReaction.Toast, "数据异常，请稍后重试"));
            return registry;
        }

        // ── 注册 ─────────────────────────────────────────────────────────────

        /// <summary>精确注册（重复注册后者覆盖）。</summary>
        public void Register(int code, ErrorRule rule)
        {
            _exact[code] = rule;
        }

        /// <summary>精确注册（便捷重载）。</summary>
        public void Register(int code, ErrorReaction reaction, string messageKey = null)
        {
            _exact[code] = new ErrorRule(reaction, messageKey);
        }

        /// <summary>
        /// 区间注册 [from, to]（含端点）：模块段兜底。跨度小者优先，同跨度后注册覆盖。
        /// </summary>
        public void RegisterRange(int from, int to, ErrorRule rule)
        {
            if (from > to)
            {
                int tmp = from;
                from = to;
                to = tmp;
            }
            _ranges.Add(new RangeRule { From = from, To = to, Rule = rule, Order = ++_orderCounter });
        }

        /// <summary>区间注册（便捷重载）。</summary>
        public void RegisterRange(int from, int to, ErrorReaction reaction, string messageKey = null)
        {
            RegisterRange(from, to, new ErrorRule(reaction, messageKey));
        }

        /// <summary>
        /// 注入本地化委托（如 Localization 表查询）。返回 null/空 表示该键翻不出，回退原样 key。
        /// </summary>
        public void SetLocalizer(Func<string, string> localizer)
        {
            _localizer = localizer;
        }

        // ── 解析 ─────────────────────────────────────────────────────────────

        /// <summary>解析错误码对应规则：精确 → 最窄区间 → 默认。</summary>
        public ErrorRule ResolveRule(int code)
        {
            if (_exact.TryGetValue(code, out ErrorRule exact))
                return exact;

            bool found = false;
            RangeRule best = default;
            foreach (RangeRule range in _ranges)
            {
                if (code < range.From || code > range.To)
                    continue;

                if (!found)
                {
                    best = range;
                    found = true;
                    continue;
                }

                long bestSpan = (long)best.To - best.From;
                long span = (long)range.To - range.From;
                // 跨度更小（更具体）者优先；同跨度后注册覆盖
                if (span < bestSpan || (span == bestSpan && range.Order > best.Order))
                    best = range;
            }

            return found ? best.Rule : DefaultRule;
        }

        /// <summary>
        /// 解析完整决策（规则 + 最终文案）。
        /// 文案回退链：MessageKey 经 localizer → 原样 key → 服务端 message → 默认文案。
        /// </summary>
        public ErrorDecision Resolve(int code, string serverMessage = null)
        {
            ErrorRule rule = ResolveRule(code);
            return new ErrorDecision
            {
                Code = code,
                Reaction = rule.Reaction,
                Message = ResolveMessage(rule.MessageKey, serverMessage),
            };
        }

        private string ResolveMessage(string messageKey, string serverMessage)
        {
            if (!string.IsNullOrEmpty(messageKey))
            {
                if (_localizer != null)
                {
                    string localized = _localizer(messageKey);
                    if (!string.IsNullOrEmpty(localized))
                        return localized;
                }
                return messageKey; // localizer 未注入或翻不出：key 原样显示（key 可直接写中文）
            }

            if (!string.IsNullOrEmpty(serverMessage))
                return serverMessage;

            return ResolveMessageOfDefaultRule();
        }

        private string ResolveMessageOfDefaultRule()
        {
            string key = DefaultRule.MessageKey;
            if (string.IsNullOrEmpty(key))
                return string.Empty;
            if (_localizer != null)
            {
                string localized = _localizer(key);
                if (!string.IsNullOrEmpty(localized))
                    return localized;
            }
            return key;
        }
    }
}
