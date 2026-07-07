using System.Collections.Generic;

namespace Framework.Analytics
{
    /// <summary>埋点属性类型（对应管道支持的扁平值类型）。</summary>
    public enum AnalyticsPropType
    {
        String,
        Bool,
        Integer,
        Float,
    }

    /// <summary>
    /// 单个事件的 schema：事件名 + 必带属性（含类型）+ 可选属性（含类型）。
    /// 用 Fluent 方式构造：
    /// <code>
    /// new AnalyticsEventSchema("stage_enter")
    ///     .Require("stage", AnalyticsPropType.String)
    ///     .Optional("from", AnalyticsPropType.String)
    /// </code>
    /// </summary>
    public sealed class AnalyticsEventSchema
    {
        public string EventName { get; }

        internal readonly Dictionary<string, AnalyticsPropType> RequiredProps =
            new Dictionary<string, AnalyticsPropType>();

        internal readonly Dictionary<string, AnalyticsPropType> OptionalProps =
            new Dictionary<string, AnalyticsPropType>();

        /// <summary>是否允许 schema 之外的额外属性（默认 true：字典管住关键字段即可，不堵探索性埋点）。</summary>
        public bool AllowExtraProps = true;

        public AnalyticsEventSchema(string eventName)
        {
            EventName = eventName ?? string.Empty;
        }

        /// <summary>声明必带属性。</summary>
        public AnalyticsEventSchema Require(string propName, AnalyticsPropType type)
        {
            if (!string.IsNullOrEmpty(propName))
                RequiredProps[propName] = type;
            return this;
        }

        /// <summary>声明可选属性（出现时校验类型）。</summary>
        public AnalyticsEventSchema Optional(string propName, AnalyticsPropType type)
        {
            if (!string.IsNullOrEmpty(propName))
                OptionalProps[propName] = type;
            return this;
        }

        /// <summary>禁止 schema 之外的额外属性（严格模式）。</summary>
        public AnalyticsEventSchema Strict()
        {
            AllowExtraProps = false;
            return this;
        }
    }

    /// <summary>
    /// 埋点事件字典：事件 schema 的注册与校验中心（纯逻辑，可单测）。
    ///
    /// 埋点最大的质量问题不是丢数据，是**脏数据**——事件名打错、属性名各写各的、
    /// 类型时而字符串时而数字，采集端看板全是碎片。字典把事件契约代码化：
    /// 组合根启动时注册全部事件 schema，Track 时（仅 Editor / Development Build）
    /// 校验违规立即以 Error 日志暴露，开发期就地修正；正式包不校验（零开销）。
    ///
    /// 校验规则：事件未注册（去重告警，每个事件名只报一次）；缺必带属性；
    /// 属性类型不匹配；Strict 事件出现字典外属性。
    /// 违规不拦截发送（埋点宁脏勿丢，修正靠开发期告警闭环）。
    /// </summary>
    public sealed class AnalyticsSchemaRegistry
    {
        private readonly Dictionary<string, AnalyticsEventSchema> _schemas =
            new Dictionary<string, AnalyticsEventSchema>();

        private readonly HashSet<string> _warnedUnregistered = new HashSet<string>();

        private static AnalyticsSchemaRegistry _shared;

        /// <summary>框架默认字典（已预注册框架内置事件；业务组合根启动时补注册自己的事件）。</summary>
        public static AnalyticsSchemaRegistry Shared
        {
            get
            {
                if (_shared == null)
                    _shared = CreateWithFrameworkEvents();
                return _shared;
            }
            set => _shared = value ?? CreateWithFrameworkEvents();
        }

        /// <summary>已注册事件数（诊断/测试用）。</summary>
        public int Count => _schemas.Count;

        /// <summary>创建预注册框架内置事件的字典。</summary>
        public static AnalyticsSchemaRegistry CreateWithFrameworkEvents()
        {
            var registry = new AnalyticsSchemaRegistry();

            registry.Register(new AnalyticsEventSchema("launch_run")
                .Require("run_id", AnalyticsPropType.String)
                .Require("success", AnalyticsPropType.Bool)
                .Require("end_reason", AnalyticsPropType.String)
                .Require("total_ms", AnalyticsPropType.Integer)
                .Optional("phase_count", AnalyticsPropType.Integer));

            registry.Register(new AnalyticsEventSchema("launch_phase")
                .Require("run_id", AnalyticsPropType.String)
                .Require("phase", AnalyticsPropType.String)
                .Require("success", AnalyticsPropType.Bool)
                .Require("duration_ms", AnalyticsPropType.Integer)
                .Optional("detail", AnalyticsPropType.String));

            registry.Register(new AnalyticsEventSchema("analytics_dropped")
                .Require("count", AnalyticsPropType.Integer));

            registry.Register(new AnalyticsEventSchema("server_error")
                .Require("code", AnalyticsPropType.Integer)
                .Require("reaction", AnalyticsPropType.String));

            return registry;
        }

        /// <summary>注册事件 schema（重复注册后者覆盖，便于业务覆写框架内置事件的契约）。</summary>
        public void Register(AnalyticsEventSchema schema)
        {
            if (schema == null || string.IsNullOrEmpty(schema.EventName))
                return;
            _schemas[schema.EventName] = schema;
        }

        /// <summary>事件是否已注册。</summary>
        public bool IsRegistered(string eventName)
        {
            return !string.IsNullOrEmpty(eventName) && _schemas.ContainsKey(eventName);
        }

        /// <summary>
        /// 校验一条事件，返回违规描述列表（空表 = 通过）。
        /// 未注册事件的告警按事件名去重（同一个未登记事件只报一次，不刷屏）。
        /// </summary>
        public List<string> Validate(string eventName, IReadOnlyDictionary<string, object> properties)
        {
            var violations = new List<string>();

            if (!_schemas.TryGetValue(eventName ?? string.Empty, out AnalyticsEventSchema schema))
            {
                if (_warnedUnregistered.Add(eventName ?? string.Empty))
                    violations.Add($"事件 \"{eventName}\" 未在事件字典注册（本告警每个事件名只报一次）");
                return violations;
            }

            // 必带属性：存在性 + 类型
            foreach (KeyValuePair<string, AnalyticsPropType> required in schema.RequiredProps)
            {
                if (properties == null || !properties.TryGetValue(required.Key, out object value))
                {
                    violations.Add($"事件 \"{eventName}\" 缺少必带属性 \"{required.Key}\"");
                    continue;
                }
                if (!MatchesType(value, required.Value))
                {
                    violations.Add($"事件 \"{eventName}\" 属性 \"{required.Key}\" 类型应为 {required.Value}，" +
                                   $"实际为 {value?.GetType().Name ?? "null"}");
                }
            }

            if (properties == null)
                return violations;

            foreach (KeyValuePair<string, object> prop in properties)
            {
                if (schema.RequiredProps.ContainsKey(prop.Key))
                    continue;

                if (schema.OptionalProps.TryGetValue(prop.Key, out AnalyticsPropType optType))
                {
                    if (!MatchesType(prop.Value, optType))
                    {
                        violations.Add($"事件 \"{eventName}\" 可选属性 \"{prop.Key}\" 类型应为 {optType}，" +
                                       $"实际为 {prop.Value?.GetType().Name ?? "null"}");
                    }
                    continue;
                }

                if (!schema.AllowExtraProps)
                    violations.Add($"事件 \"{eventName}\" 出现字典外属性 \"{prop.Key}\"（该事件为 Strict）");
            }

            return violations;
        }

        private static bool MatchesType(object value, AnalyticsPropType type)
        {
            switch (type)
            {
                case AnalyticsPropType.String:
                    return value is string;
                case AnalyticsPropType.Bool:
                    return value is bool;
                case AnalyticsPropType.Integer:
                    return value is int || value is long || value is short || value is byte ||
                           value is uint || value is ulong || value is ushort || value is sbyte;
                case AnalyticsPropType.Float:
                    // 整数当浮点用无损，放行；反向（浮点当整数）不放行
                    return value is float || value is double || value is decimal ||
                           value is int || value is long;
                default:
                    return false;
            }
        }
    }
}
