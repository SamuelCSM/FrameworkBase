using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;

namespace Framework
{
    /// <summary>
    /// general 配置文本值 → 运行时类型的纯解析器（无 UnityEngine / 无 ConfigManager 实例状态）。
    /// <para>
    /// 从 <see cref="ConfigManager"/> 抽出：general 表把每个配置项存成 (Key, Value 文本)，读取时要按目标属性类型
    /// 把文本强转为 bool / 枚举 / 数组 / List / 可 Parse 的值类型等。这段类型强制逻辑最易出错（多种 bool 写法、
    /// 嵌套集合、Parse vs ChangeType 回退），抽成纯类后可在 EditMode 独立单测，不必拉起数据库。
    /// </para>
    /// </summary>
    internal static class GeneralConfigValueParser
    {
        /// <summary>
        /// 将 general 表的 Value 文本转换为目标属性所需的运行时类型。
        /// <para>
        /// 支持：可空类型解包、空值→默认值/ null、string 原样、多写法 bool（true/1/yes/y/是）、枚举（忽略大小写）、
        /// 数组与 <see cref="List{T}"/>（递归解析元素）、含静态 <c>Parse(string)</c> 的类型，最后回退
        /// <see cref="Convert.ChangeType(object, Type, IFormatProvider)"/>（不变文化）。
        /// </para>
        /// </summary>
        /// <param name="value">general 表中的原始文本值，可为 null/空白。</param>
        /// <param name="targetType">目标属性类型（可为 <see cref="Nullable{T}"/>）。</param>
        /// <returns>转换后的运行时值；空白值对值类型返回其默认实例、对引用类型返回 null。</returns>
        public static object Parse(string value, Type targetType)
        {
            Type actualType = Nullable.GetUnderlyingType(targetType) ?? targetType;

            if (string.IsNullOrWhiteSpace(value))
            {
                return actualType.IsValueType ? Activator.CreateInstance(actualType) : null;
            }

            if (actualType == typeof(string))
            {
                return value;
            }

            if (actualType == typeof(bool))
            {
                string boolText = value.Trim().ToLowerInvariant();
                return boolText == "true" || boolText == "1" || boolText == "yes" || boolText == "y" || boolText == "是";
            }

            if (actualType.IsEnum)
            {
                return Enum.Parse(actualType, value, ignoreCase: true);
            }

            if (actualType.IsArray)
            {
                Type elementType = actualType.GetElementType();
                string[] parts = SplitCollection(value);
                Array array = Array.CreateInstance(elementType, parts.Length);

                for (int i = 0; i < parts.Length; i++)
                {
                    array.SetValue(Parse(parts[i], elementType), i);
                }

                return array;
            }

            if (actualType.IsGenericType && actualType.GetGenericTypeDefinition() == typeof(List<>))
            {
                Type elementType = actualType.GetGenericArguments()[0];
                string[] parts = SplitCollection(value);
                var list = (IList)Activator.CreateInstance(actualType);

                foreach (string part in parts)
                {
                    list.Add(Parse(part, elementType));
                }

                return list;
            }

            MethodInfo parseMethod = actualType.GetMethod(
                "Parse",
                BindingFlags.Public | BindingFlags.Static,
                null,
                new[] { typeof(string) },
                null);

            if (parseMethod != null)
            {
                return parseMethod.Invoke(null, new object[] { value });
            }

            return Convert.ChangeType(value, actualType, CultureInfo.InvariantCulture);
        }

        /// <summary>
        /// 拆分 general 配置中的数组或列表文本：去掉可选的方括号包裹，按逗号或分号切分并丢弃空段。
        /// </summary>
        public static string[] SplitCollection(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return Array.Empty<string>();
            }

            string normalized = value.Trim();
            if (normalized.StartsWith("[", StringComparison.Ordinal) && normalized.EndsWith("]", StringComparison.Ordinal))
            {
                normalized = normalized.Substring(1, normalized.Length - 2);
            }

            return normalized.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries);
        }
    }
}
