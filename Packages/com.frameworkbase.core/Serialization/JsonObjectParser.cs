using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Framework.Serialization
{
    /// <summary>
    /// Lightweight JSON object parser for dynamic configuration payloads.
    /// It supports JSON objects, arrays, strings, numbers, booleans and null, and maps objects to
    /// <c>Dictionary&lt;string, object&gt;</c>. It is intentionally small and dependency-free, not a full JSON DOM library.
    /// </summary>
    public static class JsonObjectParser
    {
        /// <summary>
        /// 允许的最大嵌套层数。递归下降解析在深层嵌套输入上会耗尽调用栈，而 StackOverflowException
        /// 在 .NET 上<b>无法捕获</b>、直接终止进程——远端配置或磁盘缓存里的一段深嵌套 JSON 就足以让
        /// 客户端起不来，所以这里必须有硬上限，而不能靠调用方的 try/catch 兜底。
        /// 32 层远超配置类载荷的实际需要（业务配置极少超过 5 层）。
        /// </summary>
        private const int MaxDepth = 32;

        /// <summary>
        /// Parse a top-level JSON object. Invalid input returns false instead of throwing.
        /// Number mapping: integer tokens become <see cref="long"/>, decimal/exponent tokens become <see cref="double"/>.
        /// Nesting deeper than <see cref="MaxDepth"/> levels is rejected as invalid input.
        /// </summary>
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

                Dictionary<string, object> parsed = ParseObject(json, ref pos, 1);
                SkipWhitespace(json, ref pos);
                if (pos != json.Length)
                    return false;

                result = parsed;
                return true;
            }
            catch (FormatException)
            {
                return false;
            }
        }

        // Recursive descent parser kept private so the public API stays small and stable.
        // depth is the nesting level of the container currently being parsed; containers check it on entry.
        private static object ParseValue(string json, ref int pos, int depth)
        {
            SkipWhitespace(json, ref pos);
            if (pos >= json.Length)
                throw Malformed(pos, "Unexpected end.");

            char c = json[pos];
            switch (c)
            {
                case '{': return ParseObject(json, ref pos, depth + 1);
                case '[': return ParseArray(json, ref pos, depth + 1);
                case '"': return ParseString(json, ref pos);
                case 't': ExpectLiteral(json, ref pos, "true"); return true;
                case 'f': ExpectLiteral(json, ref pos, "false"); return false;
                case 'n': ExpectLiteral(json, ref pos, "null"); return null;
                default:
                    if (c == '-' || (c >= '0' && c <= '9'))
                        return ParseNumber(json, ref pos);
                    throw Malformed(pos, $"Unexpected character '{c}'.");
            }
        }

        private static Dictionary<string, object> ParseObject(string json, ref int pos, int depth)
        {
            if (depth > MaxDepth)
                throw Malformed(pos, $"Nesting is deeper than the {MaxDepth}-level limit.");

            pos++;
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
                    throw Malformed(pos, "Object key must be a string.");

                string key = ParseString(json, ref pos);
                SkipWhitespace(json, ref pos);
                if (pos >= json.Length || json[pos] != ':')
                    throw Malformed(pos, "Missing ':' after object key.");
                pos++;

                obj[key] = ParseValue(json, ref pos, depth);

                SkipWhitespace(json, ref pos);
                if (pos >= json.Length)
                    throw Malformed(pos, "Object is not closed.");
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
                throw Malformed(pos, "Object members must be separated by ',' or closed by '}'.");
            }
        }

        private static List<object> ParseArray(string json, ref int pos, int depth)
        {
            if (depth > MaxDepth)
                throw Malformed(pos, $"Nesting is deeper than the {MaxDepth}-level limit.");

            pos++;
            var list = new List<object>();

            SkipWhitespace(json, ref pos);
            if (pos < json.Length && json[pos] == ']')
            {
                pos++;
                return list;
            }

            while (true)
            {
                list.Add(ParseValue(json, ref pos, depth));

                SkipWhitespace(json, ref pos);
                if (pos >= json.Length)
                    throw Malformed(pos, "Array is not closed.");
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
                throw Malformed(pos, "Array items must be separated by ',' or closed by ']'.");
            }
        }

        private static string ParseString(string json, ref int pos)
        {
            pos++;
            var sb = new StringBuilder();

            while (true)
            {
                if (pos >= json.Length)
                    throw Malformed(pos, "String is not closed.");

                char c = json[pos++];
                if (c == '"')
                    return sb.ToString();

                if (c != '\\')
                {
                    sb.Append(c);
                    continue;
                }

                if (pos >= json.Length)
                    throw Malformed(pos, "Escape sequence is not completed.");

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
                            throw Malformed(pos, "\\u escape is too short.");
                        sb.Append((char)ushort.Parse(
                            json.Substring(pos, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture));
                        pos += 4;
                        break;
                    default:
                        throw Malformed(pos, $"Invalid escape '\\{esc}'.");
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
            throw Malformed(start, $"Invalid number '{token}'.");
        }

        private static void ExpectLiteral(string json, ref int pos, string literal)
        {
            if (pos + literal.Length > json.Length ||
                string.CompareOrdinal(json, pos, literal, 0, literal.Length) != 0)
            {
                throw Malformed(pos, $"Expected literal '{literal}'.");
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
        {
            return new FormatException($"Invalid JSON at position {pos}: {reason}");
        }
    }
}
