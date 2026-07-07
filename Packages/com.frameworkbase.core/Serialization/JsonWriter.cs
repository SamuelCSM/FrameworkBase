using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Framework.Serialization
{
    /// <summary>
    /// Lightweight JSON writer for dynamic dictionaries and arrays.
    /// This complements <see cref="IJsonSerializer"/>, because Unity JsonUtility does not support dictionary payloads.
    /// </summary>
    public static class JsonWriter
    {
        /// <summary>Serialize a dictionary as a JSON object.</summary>
        public static string SerializeObject(IReadOnlyDictionary<string, object> values)
        {
            var sb = new StringBuilder(256);
            AppendObject(sb, values);
            return sb.ToString();
        }

        /// <summary>Append a dictionary as a JSON object to an existing string builder.</summary>
        public static void AppendObject(StringBuilder sb, IReadOnlyDictionary<string, object> values)
        {
            sb.Append('{');
            bool first = true;
            if (values != null)
            {
                foreach (KeyValuePair<string, object> pair in values)
                {
                    if (string.IsNullOrEmpty(pair.Key))
                        continue;
                    if (!first) sb.Append(',');
                    first = false;
                    AppendProperty(sb, pair.Key, pair.Value);
                }
            }
            sb.Append('}');
        }

        /// <summary>Append one JSON object property, including the escaped key and serialized value.</summary>
        public static void AppendProperty(StringBuilder sb, string key, object value)
        {
            AppendEscapedString(sb, key);
            sb.Append(':');
            AppendValue(sb, value);
        }

        /// <summary>
        /// Append one supported JSON value. Unknown value types are serialized through <see cref="object.ToString"/>.
        /// </summary>
        public static void AppendValue(StringBuilder sb, object value)
        {
            if (value == null)
            {
                sb.Append("null");
                return;
            }

            switch (value)
            {
                case string s:
                    AppendEscapedString(sb, s);
                    return;
                case bool b:
                    sb.Append(b ? "true" : "false");
                    return;
                case byte _:
                case sbyte _:
                case short _:
                case ushort _:
                case int _:
                case uint _:
                case long _:
                case ulong _:
                case decimal _:
                    sb.Append(Convert.ToString(value, CultureInfo.InvariantCulture));
                    return;
                case float f:
                    AppendFloat(sb, f);
                    return;
                case double d:
                    AppendDouble(sb, d);
                    return;
                case IReadOnlyDictionary<string, object> readOnlyDictionary:
                    AppendObject(sb, readOnlyDictionary);
                    return;
                case IDictionary<string, object> dictionary:
                    AppendObject(sb, new Dictionary<string, object>(dictionary));
                    return;
                case IEnumerable enumerable:
                    AppendArray(sb, enumerable);
                    return;
                default:
                    AppendEscapedString(sb, value.ToString());
                    return;
            }
        }

        /// <summary>Append a JSON string literal with required escaping and surrounding quotes.</summary>
        public static void AppendEscapedString(StringBuilder sb, string value)
        {
            sb.Append('"');
            if (!string.IsNullOrEmpty(value))
            {
                foreach (char c in value)
                {
                    switch (c)
                    {
                        case '"': sb.Append("\\\""); break;
                        case '\\': sb.Append("\\\\"); break;
                        case '\b': sb.Append("\\b"); break;
                        case '\f': sb.Append("\\f"); break;
                        case '\n': sb.Append("\\n"); break;
                        case '\r': sb.Append("\\r"); break;
                        case '\t': sb.Append("\\t"); break;
                        default:
                            if (c < 0x20)
                                sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                            else
                                sb.Append(c);
                            break;
                    }
                }
            }
            sb.Append('"');
        }

        private static void AppendArray(StringBuilder sb, IEnumerable values)
        {
            sb.Append('[');
            bool first = true;
            foreach (object item in values)
            {
                if (!first) sb.Append(',');
                first = false;
                AppendValue(sb, item);
            }
            sb.Append(']');
        }

        private static void AppendFloat(StringBuilder sb, float value)
        {
            if (float.IsNaN(value) || float.IsInfinity(value))
                sb.Append("null");
            else
                sb.Append(value.ToString("R", CultureInfo.InvariantCulture));
        }

        private static void AppendDouble(StringBuilder sb, double value)
        {
            if (double.IsNaN(value) || double.IsInfinity(value))
                sb.Append("null");
            else
                sb.Append(value.ToString("R", CultureInfo.InvariantCulture));
        }
    }
}
