using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using UnityEngine;

namespace Framework.Network
{
    /// <summary>
    /// 网络协议日志格式化器，统一输出收发方向、协议名、包体大小和字段内容。
    /// <para>
    /// <b>安全边界</b>：协议正文常带会话令牌与设备标识，故本类有两道约束——三个日志入口都标了
    /// <see cref="System.Diagnostics.ConditionalAttribute"/>，Release 构建里调用连同实参（含正文展开）
    /// 被编译器整体移除；Editor 与 Development Build 仍打印，但成员名命中
    /// <see cref="SensitiveMarkers"/> 的字段只输出掩码，避免开发包日志被回捞、转发时外泄凭证。
    /// </para>
    /// <para>
    /// 反射元数据按类型缓存：收发是高频路径，逐包 <c>GetProperties</c> + 排序有可观的 CPU 与 GC 开销。
    /// 缓存里连"该成员是否敏感"的判定一并存下，故追加脱敏标记时必须清缓存。
    /// </para>
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

        /// <summary>敏感成员掩码文本：只表示"有值但不打印"，不泄露内容也不泄露长度。</summary>
        private const string RedactedText = "***";

        /// <summary>敏感成员为空值时的展示文本，用于区分"字段没带上"与"字段有值被掩码"。</summary>
        private const string RedactedEmptyText = "(empty)";

        /// <summary>
        /// 敏感成员名标记（全小写）。成员名小写化后包含任一标记即整体掩码。
        /// 框架只覆盖通用凭证与设备标识；业务协议里的实名、手机号等字段由
        /// <see cref="NetworkManager.AddSensitiveProtocolField"/> 在启动早期追加。
        /// </summary>
        private static readonly HashSet<string> SensitiveMarkers = new HashSet<string>(StringComparer.Ordinal)
        {
            "token",
            "password",
            "passwd",
            "pwd",
            "secret",
            "credential",
            "privatekey",
            "signature",
            "deviceid",
            "idcard",
        };

        /// <summary>
        /// 协议类型 → 可展开成员的缓存。
        /// 用并发字典：LogSend 发生在调用业务的线程、LogReceive 在主线程，两者之间没有外部串行保证。
        /// </summary>
        private static readonly ConcurrentDictionary<Type, MemberAccessor[]> MemberCache =
            new ConcurrentDictionary<Type, MemberAccessor[]>();

        /// <summary>单个可展开成员的取值器，缓存反射句柄与敏感判定结果。</summary>
        private readonly struct MemberAccessor
        {
            private readonly PropertyInfo _property;
            private readonly FieldInfo _field;

            /// <summary>成员名，直接用作日志里的字段键。</summary>
            public readonly string Name;

            /// <summary>命中脱敏标记：仍然取值（要区分未赋值与有值），但不输出真实内容。</summary>
            public readonly bool IsSensitive;

            /// <summary>构造取值器。属性与字段二选一，另一个传 null。</summary>
            /// <param name="property">公开可读实例属性；传 null 表示这是字段成员。</param>
            /// <param name="field">公开实例字段；传 null 表示这是属性成员。</param>
            /// <param name="name">成员名。</param>
            /// <param name="isSensitive">是否命中脱敏标记。</param>
            public MemberAccessor(PropertyInfo property, FieldInfo field, string name, bool isSensitive)
            {
                _property = property;
                _field = field;
                Name = name;
                IsSensitive = isSensitive;
            }

            /// <summary>
            /// 取成员值。属性 getter 可能因状态不合法抛异常，此时返回 false 让调用方跳过该成员，
            /// 而不是把异常当成 null 值打印出去。
            /// </summary>
            /// <param name="target">协议对象。</param>
            /// <param name="value">成功时返回成员值。</param>
            /// <returns>取值成功返回 true。</returns>
            public bool TryGetValue(object target, out object value)
            {
                try
                {
                    value = _property != null ? _property.GetValue(target, null) : _field.GetValue(target);
                    return true;
                }
                catch
                {
                    value = null;
                    return false;
                }
            }
        }

        /// <summary>
        /// 追加一个脱敏标记。命中的成员在协议日志里只输出掩码。
        /// 追加后清空成员缓存——缓存里存的是按旧标记集算出的敏感判定，不清会让已登记过的协议类型继续明文输出。
        /// </summary>
        /// <param name="marker">成员名子串（大小写无关），如 <c>idcard</c>；null 或空白忽略。</param>
        internal static void AddSensitiveMarker(string marker)
        {
            if (string.IsNullOrWhiteSpace(marker))
            {
                return;
            }

            string normalized = marker.Trim().ToLowerInvariant();
            lock (SensitiveMarkers)
            {
                if (!SensitiveMarkers.Add(normalized))
                {
                    return;
                }
            }

            MemberCache.Clear();
        }

        /// <summary>
        /// 打印客户端发送协议。
        /// </summary>
        /// <param name="message">协议消息对象。</param>
        /// <param name="packetSize">完整包字节数。</param>
        /// <param name="seqId">请求序列号。</param>
        [System.Diagnostics.Conditional("UNITY_EDITOR")]
        [System.Diagnostics.Conditional("DEVELOPMENT_BUILD")]
        public static void LogSend(INetMessage message, int packetSize, ushort seqId)
        {
            if (message == null)
            {
                return;
            }

            string body = FormatObject(message, 0);
            string name = message.GetType().Name;
            GameLog.Debug(BuildLine("SEND", "C -> S", SendColor, name, packetSize, seqId, body));
        }

        /// <summary>
        /// 打印客户端发送的无消息体协议，如心跳空包。
        /// </summary>
        /// <param name="mainId">主消息 ID。</param>
        /// <param name="subId">子消息 ID。</param>
        /// <param name="packetSize">完整包字节数。</param>
        /// <param name="seqId">请求序列号。</param>
        /// <param name="messageName">协议名；为空时按主子号拼占位名。</param>
        [System.Diagnostics.Conditional("UNITY_EDITOR")]
        [System.Diagnostics.Conditional("DEVELOPMENT_BUILD")]
        public static void LogSend(byte mainId, byte subId, int packetSize, ushort seqId, string messageName = null)
        {
            string name = string.IsNullOrEmpty(messageName) ? $"Unknown_{mainId}_{subId}" : messageName;
            GameLog.Debug(BuildLine("SEND", "C -> S", SendColor, name, packetSize, seqId, "{}"));
        }

        /// <summary>
        /// 打印服务端接收协议。
        /// </summary>
        /// <param name="registry">协议类型注册表，用于把 payload 还原为消息对象。</param>
        /// <param name="mainId">主消息 ID。</param>
        /// <param name="subId">子消息 ID。</param>
        /// <param name="seqId">请求序列号。</param>
        /// <param name="payload">消息体字节数据。</param>
        [System.Diagnostics.Conditional("UNITY_EDITOR")]
        [System.Diagnostics.Conditional("DEVELOPMENT_BUILD")]
        public static void LogReceive(NetworkMessageTypeRegistry registry, byte mainId, byte subId, ushort seqId, byte[] payload)
        {
            int payloadSize = payload?.Length ?? 0;
            int packetSize = MessagePacket.HeaderSize + payloadSize;
            string name = registry != null ? registry.GetMessageName(mainId, subId) : $"Unknown_{mainId}_{subId}";
            string body = $"{{payloadSize:{payloadSize}}}";

            try
            {
                if (registry != null && registry.TryParseMessage(mainId, subId, payload, out INetMessage message))
                {
                    name = message.GetType().Name;
                    body = FormatObject(message, 0);
                }
            }
            catch (Exception ex)
            {
                body = $"{{payloadSize:{payloadSize}, parseError:{SanitizeString(ex.Message)}}}";
            }

            GameLog.Debug(BuildLine("RECV", "S -> C", ReceiveColor, name, packetSize, seqId, body));
        }

        /// <summary>
        /// 格式化协议正文并返回文本。与日志入口共用同一套脱敏、截断与展开规则，
        /// 但不带 <see cref="System.Diagnostics.ConditionalAttribute"/>，供单测在不产生日志副作用的前提下断言输出。
        /// </summary>
        /// <param name="message">协议对象。</param>
        /// <returns>可直接拼进日志行的正文文本。</returns>
        internal static string FormatBody(object message) => FormatObject(message, 0);

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

            StringBuilder sb = new StringBuilder();
            sb.Append("{");

            bool hasAny = false;
            MemberAccessor[] accessors = GetAccessors(value.GetType());
            for (int i = 0; i < accessors.Length; i++)
            {
                if (!accessors[i].TryGetValue(value, out object memberValue))
                {
                    continue;
                }

                AppendMember(sb, accessors[i], memberValue, depth, ref hasAny);
            }

            sb.Append("}");
            return sb.ToString();
        }

        /// <summary>取类型的可展开成员，缓存未命中时反射构建。</summary>
        /// <param name="type">协议对象类型。</param>
        /// <returns>属性在前、字段在后，各自按源码元数据顺序排列的取值器。</returns>
        private static MemberAccessor[] GetAccessors(Type type) => MemberCache.GetOrAdd(type, BuildAccessors);

        /// <summary>
        /// 反射构建某类型的成员取值器。敏感判定在此一次算好并随缓存留存，避免逐包重复匹配标记表。
        /// </summary>
        /// <param name="type">协议对象类型。</param>
        /// <returns>该类型的成员取值器数组。</returns>
        private static MemberAccessor[] BuildAccessors(Type type)
        {
            var accessors = new List<MemberAccessor>();

            PropertyInfo[] properties = type.GetProperties(BindingFlags.Instance | BindingFlags.Public);
            Array.Sort(properties, CompareMember);
            for (int i = 0; i < properties.Length; i++)
            {
                PropertyInfo property = properties[i];
                // 只写属性取不到值，索引器还需要下标，两者都不参与协议正文展开。
                if (!property.CanRead || property.GetIndexParameters().Length > 0)
                {
                    continue;
                }

                accessors.Add(new MemberAccessor(property, null, property.Name, IsSensitiveName(property.Name)));
            }

            // 兼容少量以字段而非属性定义的协议对象。
            FieldInfo[] fields = type.GetFields(BindingFlags.Instance | BindingFlags.Public);
            Array.Sort(fields, CompareMember);
            for (int i = 0; i < fields.Length; i++)
            {
                FieldInfo field = fields[i];
                accessors.Add(new MemberAccessor(null, field, field.Name, IsSensitiveName(field.Name)));
            }

            return accessors.ToArray();
        }

        /// <summary>成员名是否命中脱敏标记（大小写无关的子串匹配）。</summary>
        /// <param name="memberName">成员名。</param>
        /// <returns>命中任一标记返回 true。</returns>
        private static bool IsSensitiveName(string memberName)
        {
            string lowered = memberName.ToLowerInvariant();
            // 与 AddSensitiveMarker 同锁：标记表可能在业务启动早期被追加，构建缓存又可能发生在收发线程。
            lock (SensitiveMarkers)
            {
                foreach (string marker in SensitiveMarkers)
                {
                    if (lowered.Contains(marker))
                    {
                        return true;
                    }
                }
            }

            return false;
        }

        /// <summary>
        /// 追加单个字段文本。命中脱敏标记的成员不再向下递归，整棵子树都不进日志。
        /// </summary>
        /// <param name="sb">日志内容构建器。</param>
        /// <param name="accessor">成员取值器。</param>
        /// <param name="value">字段值。</param>
        /// <param name="depth">当前递归深度。</param>
        /// <param name="hasAny">是否已有字段写入。</param>
        private static void AppendMember(
            StringBuilder sb,
            in MemberAccessor accessor,
            object value,
            int depth,
            ref bool hasAny)
        {
            if (hasAny)
            {
                sb.Append(", ");
            }

            sb.Append(accessor.Name);
            sb.Append(":");
            sb.Append(accessor.IsSensitive ? Redact(value) : FormatObject(value, depth + 1));
            hasAny = true;
        }

        /// <summary>
        /// 敏感成员的展示值：只区分"未赋值 / 空值 / 有值"三态，不输出内容也不输出长度。
        /// 保留三态是为了让"令牌没带上"这类问题仍能从日志判断，而不必把值打出来。
        /// </summary>
        /// <param name="value">敏感成员的真实值。</param>
        /// <returns>掩码文本。</returns>
        private static string Redact(object value)
        {
            if (value == null)
            {
                return "null";
            }

            if (value is string text)
            {
                return text.Length == 0 ? RedactedEmptyText : RedactedText;
            }

            if (value is byte[] bytes)
            {
                return bytes.Length == 0 ? RedactedEmptyText : RedactedText;
            }

            return RedactedText;
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
