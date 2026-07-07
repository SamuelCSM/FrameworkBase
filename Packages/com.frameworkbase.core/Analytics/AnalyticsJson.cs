using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Framework.Analytics
{
    /// <summary>
    /// 埋点事件的最小 JSON 序列化器。
    /// 自写原因：JsonUtility 不支持 Dictionary，埋点属性天然是动态键值；
    /// 只需支持扁平属性（string/bool/整数/浮点，其余 ToString），无需引入三方 JSON 库。
    /// </summary>
    public static class AnalyticsJson
    {
        /// <summary>
        /// 序列化单条事件为 JSON 对象文本。
        /// 固定字段在前（event_id/event/ts/session_id/device_id/user_id/app_version/channel），
        /// 自定义属性平铺进 props 子对象。
        /// <para>
        /// event_id 是每条事件的唯一幂等键，序列化时冻结。埋点管道做 at-least-once
        /// 投递（切后台落盘 + 启动补报，宁重复不丢失），采集端须按 event_id 去重才能得到
        /// 精确计数——这是幂等的锚点，客户端单方面无法保证 exactly-once。
        /// </para>
        /// </summary>
        public static string SerializeEvent(
            string eventId,
            string eventName,
            long timestampMs,
            string sessionId,
            string deviceId,
            string userId,
            string appVersion,
            string channel,
            IReadOnlyDictionary<string, object> properties)
        {
            var sb = new StringBuilder(256);
            sb.Append('{');
            AppendString(sb, "event_id", eventId);
            sb.Append(',');
            AppendString(sb, "event", eventName);
            sb.Append(',');
            AppendNumber(sb, "ts", timestampMs);
            sb.Append(',');
            AppendString(sb, "session_id", sessionId);
            sb.Append(',');
            AppendString(sb, "device_id", deviceId);
            sb.Append(',');
            AppendString(sb, "user_id", userId ?? string.Empty);
            sb.Append(',');
            AppendString(sb, "app_version", appVersion);
            sb.Append(',');
            AppendString(sb, "channel", channel);

            if (properties != null && properties.Count > 0)
            {
                sb.Append(",\"props\":{");
                bool first = true;
                foreach (KeyValuePair<string, object> kv in properties)
                {
                    if (string.IsNullOrEmpty(kv.Key))
                        continue;

                    if (!first) sb.Append(',');
                    first = false;
                    AppendValue(sb, kv.Key, kv.Value);
                }
                sb.Append('}');
            }

            sb.Append('}');
            return sb.ToString();
        }

        /// <summary>按值类型写 "key":value（bool/数值裸写，其余按转义字符串写）。</summary>
        private static void AppendValue(StringBuilder sb, string key, object value)
        {
            switch (value)
            {
                case null:
                    WriteEscaped(sb, key);
                    sb.Append(":null");
                    break;
                case bool b:
                    WriteEscaped(sb, key);
                    sb.Append(':').Append(b ? "true" : "false");
                    break;
                case int i:
                    AppendNumber(sb, key, i);
                    break;
                case long l:
                    AppendNumber(sb, key, l);
                    break;
                case float f:
                    WriteEscaped(sb, key);
                    sb.Append(':').Append(f.ToString("R", CultureInfo.InvariantCulture));
                    break;
                case double d:
                    WriteEscaped(sb, key);
                    sb.Append(':').Append(d.ToString("R", CultureInfo.InvariantCulture));
                    break;
                default:
                    AppendString(sb, key, value.ToString());
                    break;
            }
        }

        private static void AppendNumber(StringBuilder sb, string key, long value)
        {
            WriteEscaped(sb, key);
            sb.Append(':').Append(value.ToString(CultureInfo.InvariantCulture));
        }

        private static void AppendString(StringBuilder sb, string key, string value)
        {
            WriteEscaped(sb, key);
            sb.Append(':');
            WriteEscaped(sb, value ?? string.Empty);
        }

        /// <summary>写转义后的 JSON 字符串（含双引号定界）。</summary>
        private static void WriteEscaped(StringBuilder sb, string s)
        {
            sb.Append('"');
            foreach (char c in s)
            {
                switch (c)
                {
                    case '"':  sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n");  break;
                    case '\r': sb.Append("\\r");  break;
                    case '\t': sb.Append("\\t");  break;
                    default:
                        if (c < 0x20)
                            sb.Append("\\u").Append(((int)c).ToString("x4"));
                        else
                            sb.Append(c);
                        break;
                }
            }
            sb.Append('"');
        }
    }
}
