using System;
using System.Collections;
using System.Reflection;
using System.Text;
using UnityEngine;

namespace Framework.Network
{
    /// <summary>
    /// 网络协议日志格式化器，统一输出收发方向、协议名、包体大小和字段内容。
    /// </summary>
    internal static class NetworkProtocolLogger
    {
        /// <summary>发送协议日志颜色。</summary>
        private const string SendColor = "#00B050";

        /// <summary>接收协议日志颜色。</summary>
        private const string ReceiveColor = "#D7DF01";

        /// <summary>单条协议日志最大字符数，避免大快照刷爆控制台。</summary>
        private const int MaxLogChars = 2000;

        /// <summary>字符串字段最大展示字符数。</summary>
        private const int MaxStringChars = 160;

        /// <summary>集合字段最多展开元素数量。</summary>
        private const int MaxCollectionItems = 8;

        /// <summary>对象字段递归展开深度。</summary>
        private const int MaxDepth = 3;

        /// <summary>
        /// 打印客户端发送协议。
        /// </summary>
        /// <param name="message">协议消息对象。</param>
        /// <param name="packetSize">完整包字节数。</param>
        /// <param name="seqId">请求序列号。</param>
        public static void LogSend(IMessage message, int packetSize, ushort seqId)
        {
            if (message == null)
            {
                return;
            }

            string body = FormatObject(message, 0);
            string name = message.GetType().Name;
            GameLog.Warning(BuildLine("SEND", "C -> S", SendColor, name, packetSize, seqId, body));
        }

        /// <summary>
        /// 打印客户端发送的无消息体协议，如心跳空包。
        /// </summary>
        /// <param name="mainId">主消息 ID。</param>
        /// <param name="subId">子消息 ID。</param>
        /// <param name="packetSize">完整包字节数。</param>
        /// <param name="seqId">请求序列号。</param>
        public static void LogSend(byte mainId, byte subId, int packetSize, ushort seqId, string messageName = null)
        {
            string name = string.IsNullOrEmpty(messageName) ? $"Unknown_{mainId}_{subId}" : messageName;
            GameLog.Warning(BuildLine("SEND", "C -> S", SendColor, name, packetSize, seqId, "{}"));
        }

        /// <summary>
        /// 打印服务端接收协议。
        /// </summary>
        /// <param name="registry">协议类型注册表，用于把 payload 还原为消息对象。</param>
        /// <param name="mainId">主消息 ID。</param>
        /// <param name="subId">子消息 ID。</param>
        /// <param name="seqId">请求序列号。</param>
        /// <param name="payload">消息体字节数据。</param>
        public static void LogReceive(NetworkMessageTypeRegistry registry, byte mainId, byte subId, ushort seqId, byte[] payload)
        {
            int payloadSize = payload?.Length ?? 0;
            int packetSize = MessagePacket.HeaderSize + payloadSize;
            string name = registry != null ? registry.GetMessageName(mainId, subId) : $"Unknown_{mainId}_{subId}";
            string body = $"{{payloadSize:{payloadSize}}}";

            try
            {
                if (registry != null && registry.TryParseMessage(mainId, subId, payload, out IMessage message))
                {
                    name = message.GetType().Name;
                    body = FormatObject(message, 0);
                }
            }
            catch (Exception ex)
            {
                body = $"{{payloadSize:{payloadSize}, parseError:{SanitizeString(ex.Message)}}}";
            }

            GameLog.Warning(BuildLine("RECV", "S -> C", ReceiveColor, name, packetSize, seqId, body));
        }

        /// <summary>
        /// 拼接协议日志主行。
        /// </summary>
        /// <param name="label">收发标签。</param>
        /// <param name="direction">协议方向。</param>
        /// <param name="color">本条协议日志颜色。</param>
        /// <param name="name">协议类型名。</param>
        /// <param name="packetSize">完整包字节数。</param>
        /// <param name="seqId">请求序列号。</param>
        /// <param name="body">协议字段内容。</param>
        /// <returns>可直接输出到 Unity Console 的日志文本。</returns>
        private static string BuildLine(
            string label,
            string direction,
            string color,
            string name,
            int packetSize,
            ushort seqId,
            string body)
        {
            string coloredDirection = $"<color={color}>{direction}</color>";
            string line = $"<color={color}><b>{label}</b></color> [frame:{Time.frameCount}, size:{packetSize}, seq:{seqId}] {coloredDirection}: <color={color}>{name}</color> : {body}";
            if (line.Length <= MaxLogChars)
            {
                return line;
            }

            return line.Substring(0, MaxLogChars) + "...";
        }

        /// <summary>
        /// 展开对象字段。
        /// </summary>
        /// <param name="value">待展开对象。</param>
        /// <param name="depth">当前递归深度。</param>
        /// <returns>对象字段文本。</returns>
        private static string FormatObject(object value, int depth)
        {
            if (value == null)
            {
                return "null";
            }

            if (TryFormatSimpleValue(value, out string simple))
            {
                return simple;
            }

            if (depth >= MaxDepth)
            {
                return $"{{{value.GetType().Name}}}";
            }

            if (value is IEnumerable enumerable)
            {
                return FormatEnumerable(enumerable, depth);
            }

            Type type = value.GetType();
            StringBuilder sb = new StringBuilder();
            sb.Append("{");

            bool hasAny = false;
            AppendProperties(value, type, sb, depth, ref hasAny);
            AppendFields(value, type, sb, depth, ref hasAny);

            sb.Append("}");
            return sb.ToString();
        }

        /// <summary>
        /// 展开公开属性。
        /// </summary>
        /// <param name="value">协议对象。</param>
        /// <param name="type">协议对象类型。</param>
        /// <param name="sb">日志内容构建器。</param>
        /// <param name="depth">当前递归深度。</param>
        /// <param name="hasAny">是否已有字段写入。</param>
        private static void AppendProperties(object value, Type type, StringBuilder sb, int depth, ref bool hasAny)
        {
            PropertyInfo[] properties = type.GetProperties(BindingFlags.Instance | BindingFlags.Public);
            Array.Sort(properties, CompareMember);

            for (int i = 0; i < properties.Length; i++)
            {
                PropertyInfo property = properties[i];
                if (!property.CanRead || property.GetIndexParameters().Length > 0)
                {
                    continue;
                }

                object propertyValue;
                try
                {
                    propertyValue = property.GetValue(value, null);
                }
                catch
                {
                    continue;
                }

                AppendMember(sb, property.Name, propertyValue, depth, ref hasAny);
            }
        }

        /// <summary>
        /// 展开公开字段，兼容少量使用字段定义的协议对象。
        /// </summary>
        /// <param name="value">协议对象。</param>
        /// <param name="type">协议对象类型。</param>
        /// <param name="sb">日志内容构建器。</param>
        /// <param name="depth">当前递归深度。</param>
        /// <param name="hasAny">是否已有字段写入。</param>
        private static void AppendFields(object value, Type type, StringBuilder sb, int depth, ref bool hasAny)
        {
            FieldInfo[] fields = type.GetFields(BindingFlags.Instance | BindingFlags.Public);
            Array.Sort(fields, CompareMember);

            for (int i = 0; i < fields.Length; i++)
            {
                FieldInfo field = fields[i];
                object fieldValue;
                try
                {
                    fieldValue = field.GetValue(value);
                }
                catch
                {
                    continue;
                }

                AppendMember(sb, field.Name, fieldValue, depth, ref hasAny);
            }
        }

        /// <summary>
        /// 追加单个字段文本。
        /// </summary>
        /// <param name="sb">日志内容构建器。</param>
        /// <param name="name">字段名。</param>
        /// <param name="value">字段值。</param>
        /// <param name="depth">当前递归深度。</param>
        /// <param name="hasAny">是否已有字段写入。</param>
        private static void AppendMember(StringBuilder sb, string name, object value, int depth, ref bool hasAny)
        {
            if (hasAny)
            {
                sb.Append(", ");
            }

            sb.Append(name);
            sb.Append(":");
            sb.Append(FormatObject(value, depth + 1));
            hasAny = true;
        }

        /// <summary>
        /// 展开集合字段。
        /// </summary>
        /// <param name="enumerable">集合对象。</param>
        /// <param name="depth">当前递归深度。</param>
        /// <returns>集合字段文本。</returns>
        private static string FormatEnumerable(IEnumerable enumerable, int depth)
        {
            if (enumerable is byte[] bytes)
            {
                return $"[bytes:{bytes.Length}]";
            }

            StringBuilder sb = new StringBuilder();
            int count = enumerable is ICollection collection ? collection.Count : -1;
            sb.Append("[");
            if (count >= 0)
            {
                sb.Append("size:");
                sb.Append(count);
                sb.Append(" ");
            }

            int index = 0;
            foreach (object item in enumerable)
            {
                if (index >= MaxCollectionItems)
                {
                    sb.Append("...");
                    break;
                }

                if (index > 0)
                {
                    sb.Append(", ");
                }

                sb.Append(FormatObject(item, depth + 1));
                index++;
            }

            sb.Append("]");
            return sb.ToString();
        }

        /// <summary>
        /// 尝试格式化基础类型。
        /// </summary>
        /// <param name="value">待格式化值。</param>
        /// <param name="text">格式化后的文本。</param>
        /// <returns>基础类型可直接格式化时返回 true。</returns>
        private static bool TryFormatSimpleValue(object value, out string text)
        {
            text = null;

            if (value is string stringValue)
            {
                text = SanitizeString(stringValue);
                return true;
            }

            if (value is byte[] bytes)
            {
                text = $"[bytes:{bytes.Length}]";
                return true;
            }

            Type type = value.GetType();
            if (type.IsPrimitive || type.IsEnum || value is decimal)
            {
                text = Convert.ToString(value);
                return true;
            }

            return false;
        }

        /// <summary>
        /// 清理字符串字段，避免换行和富文本标签破坏控制台显示。
        /// </summary>
        /// <param name="value">原始字符串。</param>
        /// <returns>适合单行日志显示的字符串。</returns>
        private static string SanitizeString(string value)
        {
            if (value == null)
            {
                return "null";
            }

            string text = value.Replace("\r", "\\r").Replace("\n", "\\n").Replace("<", "[").Replace(">", "]");
            if (text.Length > MaxStringChars)
            {
                text = text.Substring(0, MaxStringChars) + "...";
            }

            return text;
        }

        /// <summary>
        /// 按源码元数据顺序输出字段，失败时退回名称排序。
        /// </summary>
        /// <param name="left">左侧成员。</param>
        /// <param name="right">右侧成员。</param>
        /// <returns>排序比较结果。</returns>
        private static int CompareMember(MemberInfo left, MemberInfo right)
        {
            try
            {
                return left.MetadataToken.CompareTo(right.MetadataToken);
            }
            catch
            {
                return string.CompareOrdinal(left.Name, right.Name);
            }
        }
    }
}
