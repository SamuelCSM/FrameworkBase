using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Framework.RemoteConfig
{
    /// <summary>
    /// 极简 JSON 解析器（只解析；序列化见 <c>Analytics.AnalyticsJson</c>）。
    /// 远程配置负载是任意键名的对象，JsonUtility 不支持 Dictionary，故自带解析；
    /// 不引三方 JSON 库，避免与业务工程的依赖版本冲突。
    ///
    /// 类型映射：object → Dictionary&lt;string, object&gt;；array → List&lt;object&gt;；
    /// string → string；整数 → long；小数/指数 → double；true/false → bool；null → null。
    /// </summary>
    public static class RemoteConfigJson
    {
        /// <summary>解析顶层 JSON 对象。任何非法输入返回 false（不抛异常）。</summary>
        public static bool TryParseObject(string json, out Dictionary<string, object> result)
        {
            result = null;
            if (string.IsNullOrEmpty(json))
                return false;

            try
            {
                int pos = 0;
                SkipWhitespace(json, ref pos);
                if (pos >= json.Length || json[pos] != '{')
                    return false;

                var parsed = ParseObject(json, ref pos);
                SkipWhitespace(json, ref pos);
                if (pos != json.Length)
                    return false; // 顶层对象后还有内容，视为非法

                result = parsed;
                return true;
            }
            catch (FormatException)
            {
                return false;
            }
        }

        // ── 递归下降解析 ─────────────────────────────────────────────────────

        private static object ParseValue(string json, ref int pos)
        {
            SkipWhitespace(json, ref pos);
            if (pos >= json.Length)
                throw Malformed(pos, "意外结束");

            char c = json[pos];
            switch (c)
            {
                case '{': return ParseObject(json, ref pos);
                case '[': return ParseArray(json, ref pos);
                case '"': return ParseString(json, ref pos);
                case 't': ExpectLiteral(json, ref pos, "true"); return true;
                case 'f': ExpectLiteral(json, ref pos, "false"); return false;
                case 'n': ExpectLiteral(json, ref pos, "null"); return null;
                default:
                    if (c == '-' || (c >= '0' && c <= '9'))
                        return ParseNumber(json, ref pos);
                    throw Malformed(pos, $"意外字符 '{c}'");
            }
        }

        private static Dictionary<string, object> ParseObject(string json, ref int pos)
        {
            pos++; // 跳过 '{'
            var obj = new Dictionary<string, object>();

            SkipWhitespace(json, ref pos);
            if (pos < json.Length && json[pos] == '}')
            {
                pos++;
                return obj;
            }

            while (true)
            {
                SkipWhitespace(json, ref pos);
                if (pos >= json.Length || json[pos] != '"')
                    throw Malformed(pos, "对象键必须是字符串");

                string key = ParseString(json, ref pos);
                SkipWhitespace(json, ref pos);
                if (pos >= json.Length || json[pos] != ':')
                    throw Malformed(pos, "键后缺少 ':'");
                pos++;

                obj[key] = ParseValue(json, ref pos);

                SkipWhitespace(json, ref pos);
                if (pos >= json.Length)
                    throw Malformed(pos, "对象未闭合");
                if (json[pos] == ',')
                {
                    pos++;
                    continue;
                }
                if (json[pos] == '}')
                {
                    pos++;
                    return obj;
                }
                throw Malformed(pos, "对象成员间需要 ',' 或 '}'");
            }
        }

        private static List<object> ParseArray(string json, ref int pos)
        {
            pos++; // 跳过 '['
            var list = new List<object>();

            SkipWhitespace(json, ref pos);
            if (pos < json.Length && json[pos] == ']')
            {
                pos++;
                return list;
            }

            while (true)
            {
                list.Add(ParseValue(json, ref pos));

                SkipWhitespace(json, ref pos);
                if (pos >= json.Length)
                    throw Malformed(pos, "数组未闭合");
                if (json[pos] == ',')
                {
                    pos++;
                    continue;
                }
                if (json[pos] == ']')
                {
                    pos++;
                    return list;
                }
                throw Malformed(pos, "数组元素间需要 ',' 或 ']'");
            }
        }

        private static string ParseString(string json, ref int pos)
        {
            pos++; // 跳过开头 '"'
            var sb = new StringBuilder();

            while (true)
            {
                if (pos >= json.Length)
                    throw Malformed(pos, "字符串未闭合");

                char c = json[pos++];
                if (c == '"')
                    return sb.ToString();

                if (c != '\\')
                {
                    sb.Append(c);
                    continue;
                }

                if (pos >= json.Length)
                    throw Malformed(pos, "转义序列未完成");

                char esc = json[pos++];
                switch (esc)
                {
                    case '"': sb.Append('"'); break;
                    case '\\': sb.Append('\\'); break;
                    case '/': sb.Append('/'); break;
                    case 'b': sb.Append('\b'); break;
                    case 'f': sb.Append('\f'); break;
                    case 'n': sb.Append('\n'); break;
                    case 'r': sb.Append('\r'); break;
                    case 't': sb.Append('\t'); break;
                    case 'u':
                        if (pos + 4 > json.Length)
                            throw Malformed(pos, "\\u 转义长度不足");
                        sb.Append((char)ushort.Parse(
                            json.Substring(pos, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture));
                        pos += 4;
                        break;
                    default:
                        throw Malformed(pos, $"非法转义 '\\{esc}'");
                }
            }
        }

        private static object ParseNumber(string json, ref int pos)
        {
            int start = pos;
            bool isFloat = false;

            while (pos < json.Length)
            {
                char c = json[pos];
                if (c == '.' || c == 'e' || c == 'E')
                {
                    isFloat = true;
                    pos++;
                }
                else if (c == '-' || c == '+' || (c >= '0' && c <= '9'))
                {
                    pos++;
                }
                else
                {
                    break;
                }
            }

            string token = json.Substring(start, pos - start);
            if (!isFloat && long.TryParse(token, NumberStyles.Integer, CultureInfo.InvariantCulture, out long l))
                return l;
            if (double.TryParse(token, NumberStyles.Float, CultureInfo.InvariantCulture, out double d))
                return d;
            throw Malformed(start, $"非法数字 '{token}'");
        }

        private static void ExpectLiteral(string json, ref int pos, string literal)
        {
            if (pos + literal.Length > json.Length ||
                string.CompareOrdinal(json, pos, literal, 0, literal.Length) != 0)
            {
                throw Malformed(pos, $"期望字面量 '{literal}'");
            }
            pos += literal.Length;
        }

        private static void SkipWhitespace(string json, ref int pos)
        {
            while (pos < json.Length)
            {
                char c = json[pos];
                if (c == ' ' || c == '\t' || c == '\n' || c == '\r')
                    pos++;
                else
                    break;
            }
        }

        private static FormatException Malformed(int pos, string reason)
            => new FormatException($"JSON 非法（位置 {pos}）: {reason}");
    }
}
