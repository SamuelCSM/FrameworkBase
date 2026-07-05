using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using UnityEngine;

namespace Framework.Data
{
    /// <summary>
    /// 配置单元格强类型解析器，统一处理枚举、数组、列表和业务值对象。
    /// </summary>
    public static class ConfigCellValueParser
    {
        /// <summary>
        /// 将配置文本解析为目标字段类型。
        /// </summary>
        /// <param name="value">配置单元格原始文本。</param>
        /// <param name="targetType">目标字段类型。</param>
        /// <param name="memberInfo">字段或属性元信息，用于读取字段级解析器特性。</param>
        /// <param name="context">错误日志上下文。</param>
        /// <returns>解析后的强类型值。</returns>
        public static object Parse(string value, Type targetType, MemberInfo memberInfo = null, string context = null)
        {
            if (targetType == null)
            {
                throw new ArgumentNullException(nameof(targetType));
            }

            try
            {
                return ParseCore(value, targetType, memberInfo);
            }
            catch (Exception ex)
            {
                string typeName = targetType.FullName ?? targetType.Name;
                string location = string.IsNullOrEmpty(context) ? typeName : context;
                throw new FormatException($"配置字段解析失败: {location}, 目标类型: {typeName}, 原始值: '{value ?? string.Empty}'。", ex);
            }
        }

        /// <summary>
        /// 判断指定类型是否需要配置文本解析器参与映射。
        /// </summary>
        /// <param name="targetType">目标字段类型。</param>
        /// <param name="memberInfo">字段或属性元信息。</param>
        /// <returns>需要解析器时返回 true。</returns>
        public static bool RequiresParser(Type targetType, MemberInfo memberInfo = null)
        {
            Type actualType = Nullable.GetUnderlyingType(targetType) ?? targetType;
            if (HasParserAttribute(actualType, memberInfo) || HasStaticParse(actualType))
            {
                return true;
            }

            return actualType.IsArray
                || IsListType(actualType)
                || IsCustomValueObject(actualType);
        }

        /// <summary>
        /// 执行单元格解析的主体流程。
        /// </summary>
        /// <param name="value">配置文本。</param>
        /// <param name="targetType">目标类型。</param>
        /// <param name="memberInfo">成员元信息。</param>
        /// <returns>解析后的值。</returns>
        private static object ParseCore(string value, Type targetType, MemberInfo memberInfo)
        {
            Type nullableType = Nullable.GetUnderlyingType(targetType);
            Type actualType = nullableType ?? targetType;
            string text = value?.Trim();

            if (string.IsNullOrEmpty(text))
            {
                return GetEmptyValue(targetType, actualType, nullableType != null);
            }

            if (actualType == typeof(string))
            {
                return value ?? string.Empty;
            }

            ConfigValueParserAttribute parserAttribute = GetParserAttribute(actualType, memberInfo);
            if (parserAttribute != null)
            {
                return ParseByAttribute(parserAttribute, text, actualType);
            }

            if (actualType.IsEnum)
            {
                return ParseEnum(text, actualType);
            }

            if (actualType.IsArray)
            {
                return ParseArray(text, actualType);
            }

            if (IsListType(actualType))
            {
                return ParseList(text, actualType);
            }

            if (TryParsePrimitive(text, actualType, out object primitiveValue))
            {
                return primitiveValue;
            }

            object parsedByStaticMethod = ParseByStaticMethod(text, actualType);
            if (parsedByStaticMethod != null)
            {
                return parsedByStaticMethod;
            }

            return JsonUtility.FromJson(text, actualType);
        }

        /// <summary>
        /// 返回空单元格对应的默认值。
        /// </summary>
        /// <param name="targetType">声明类型。</param>
        /// <param name="actualType">去掉 Nullable 后的实际类型。</param>
        /// <param name="isNullable">是否为 Nullable 类型。</param>
        /// <returns>空值对应结果。</returns>
        private static object GetEmptyValue(Type targetType, Type actualType, bool isNullable)
        {
            if (actualType == typeof(string))
            {
                return string.Empty;
            }

            if (isNullable)
            {
                return null;
            }

            if (actualType.IsArray)
            {
                return Array.CreateInstance(actualType.GetElementType(), 0);
            }

            if (IsListType(actualType))
            {
                return Activator.CreateInstance(actualType);
            }

            return actualType.IsValueType ? Activator.CreateInstance(actualType) : null;
        }

        /// <summary>
        /// 使用特性指定的解析器解析文本。
        /// </summary>
        /// <param name="attribute">解析器特性。</param>
        /// <param name="text">配置文本。</param>
        /// <param name="actualType">目标类型。</param>
        /// <returns>解析结果。</returns>
        private static object ParseByAttribute(ConfigValueParserAttribute attribute, string text, Type actualType)
        {
            var parser = Activator.CreateInstance(attribute.ParserType) as IConfigValueParser;
            if (parser == null || !parser.CanParse(actualType))
            {
                throw new InvalidOperationException($"配置值解析器 {attribute.ParserType.FullName} 不支持类型 {actualType.FullName}。");
            }

            return parser.Parse(text, actualType);
        }

        /// <summary>
        /// 使用类型公开的 static Parse(string) 方法解析文本。
        /// </summary>
        /// <param name="text">配置文本。</param>
        /// <param name="actualType">目标类型。</param>
        /// <returns>解析结果，没有 Parse 方法时返回 null。</returns>
        private static object ParseByStaticMethod(string text, Type actualType)
        {
            MethodInfo parseMethod = actualType.GetMethod(
                "Parse",
                BindingFlags.Public | BindingFlags.Static,
                null,
                new[] { typeof(string) },
                null);

            if (parseMethod == null)
            {
                return null;
            }

            object result = parseMethod.Invoke(null, new object[] { text });
            if (result != null && !actualType.IsInstanceOfType(result))
            {
                throw new InvalidOperationException($"{actualType.FullName}.Parse(string) 返回类型不匹配。");
            }

            return result;
        }

        /// <summary>
        /// 解析枚举文本，兼容枚举名和枚举底层数值。
        /// </summary>
        /// <param name="text">配置文本。</param>
        /// <param name="enumType">枚举类型。</param>
        /// <returns>枚举值。</returns>
        private static object ParseEnum(string text, Type enumType)
        {
            if (long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out long numericValue))
            {
                return Enum.ToObject(enumType, numericValue);
            }

            return Enum.Parse(enumType, text, ignoreCase: true);
        }

        /// <summary>
        /// 解析数组配置文本。
        /// </summary>
        /// <param name="text">配置文本。</param>
        /// <param name="arrayType">数组类型。</param>
        /// <returns>数组实例。</returns>
        private static Array ParseArray(string text, Type arrayType)
        {
            Type elementType = arrayType.GetElementType();
            string[] items = SplitCollection(text, elementType);
            Array array = Array.CreateInstance(elementType, items.Length);
            for (int i = 0; i < items.Length; i++)
            {
                array.SetValue(Parse(items[i], elementType), i);
            }

            return array;
        }

        /// <summary>
        /// 解析泛型 List 配置文本。
        /// </summary>
        /// <param name="text">配置文本。</param>
        /// <param name="listType">List 类型。</param>
        /// <returns>List 实例。</returns>
        private static object ParseList(string text, Type listType)
        {
            Type elementType = listType.GetGenericArguments()[0];
            string[] items = SplitCollection(text, elementType);
            var list = (IList)Activator.CreateInstance(listType);
            foreach (string item in items)
            {
                list.Add(Parse(item, elementType));
            }

            return list;
        }

        /// <summary>
        /// 按配置集合约定拆分文本。
        /// </summary>
        /// <param name="text">配置文本。</param>
        /// <param name="elementType">集合元素类型。</param>
        /// <returns>拆分后的元素文本。</returns>
        private static string[] SplitCollection(string text, Type elementType)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return Array.Empty<string>();
            }

            string normalized = text.Trim();
            if (normalized.StartsWith("[", StringComparison.Ordinal) && normalized.EndsWith("]", StringComparison.Ordinal))
            {
                normalized = normalized.Substring(1, normalized.Length - 2);
            }

            char[] separators = normalized.IndexOf(';') >= 0 || IsCustomValueObject(elementType)
                ? new[] { ';' }
                : new[] { ',', ';' };

            return normalized.Split(separators, StringSplitOptions.RemoveEmptyEntries);
        }

        /// <summary>
        /// 尝试解析基础类型。
        /// </summary>
        /// <param name="text">配置文本。</param>
        /// <param name="actualType">目标类型。</param>
        /// <param name="value">解析结果。</param>
        /// <returns>属于基础类型时返回 true。</returns>
        private static bool TryParsePrimitive(string text, Type actualType, out object value)
        {
            if (actualType == typeof(bool))
            {
                string lowerText = text.ToLowerInvariant();
                value = lowerText == "true" || lowerText == "1" || lowerText == "yes" || lowerText == "y" || lowerText == "是";
                return true;
            }

            if (actualType == typeof(int))
            {
                value = int.Parse(text, NumberStyles.Integer, CultureInfo.InvariantCulture);
                return true;
            }

            if (actualType == typeof(long))
            {
                value = long.Parse(text, NumberStyles.Integer, CultureInfo.InvariantCulture);
                return true;
            }

            if (actualType == typeof(short))
            {
                value = short.Parse(text, NumberStyles.Integer, CultureInfo.InvariantCulture);
                return true;
            }

            if (actualType == typeof(byte))
            {
                value = byte.Parse(text, NumberStyles.Integer, CultureInfo.InvariantCulture);
                return true;
            }

            if (actualType == typeof(float))
            {
                value = float.Parse(text, NumberStyles.Float, CultureInfo.InvariantCulture);
                return true;
            }

            if (actualType == typeof(double))
            {
                value = double.Parse(text, NumberStyles.Float, CultureInfo.InvariantCulture);
                return true;
            }

            if (actualType == typeof(decimal))
            {
                value = decimal.Parse(text, NumberStyles.Float, CultureInfo.InvariantCulture);
                return true;
            }

            value = null;
            return false;
        }

        /// <summary>
        /// 判断类型是否为 List&lt;T&gt;。
        /// </summary>
        /// <param name="type">待判断类型。</param>
        /// <returns>是 List&lt;T&gt; 时返回 true。</returns>
        private static bool IsListType(Type type)
        {
            return type.IsGenericType && type.GetGenericTypeDefinition() == typeof(List<>);
        }

        /// <summary>
        /// 判断类型是否为业务配置值对象。
        /// </summary>
        /// <param name="type">待判断类型。</param>
        /// <returns>需要自定义解析时返回 true。</returns>
        private static bool IsCustomValueObject(Type type)
        {
            Type actualType = Nullable.GetUnderlyingType(type) ?? type;
            return actualType != typeof(string)
                && !actualType.IsEnum
                && !actualType.IsPrimitive
                && actualType != typeof(decimal)
                && actualType != typeof(DateTime)
                && actualType != typeof(byte[]);
        }

        /// <summary>
        /// 判断类型或成员是否声明了解析器特性。
        /// </summary>
        /// <param name="actualType">目标类型。</param>
        /// <param name="memberInfo">字段或属性元信息。</param>
        /// <returns>存在解析器特性时返回 true。</returns>
        private static bool HasParserAttribute(Type actualType, MemberInfo memberInfo)
        {
            return GetParserAttribute(actualType, memberInfo) != null;
        }

        /// <summary>
        /// 获取成员级或类型级解析器特性。
        /// </summary>
        /// <param name="actualType">目标类型。</param>
        /// <param name="memberInfo">字段或属性元信息。</param>
        /// <returns>解析器特性。</returns>
        private static ConfigValueParserAttribute GetParserAttribute(Type actualType, MemberInfo memberInfo)
        {
            return memberInfo?.GetCustomAttribute<ConfigValueParserAttribute>()
                ?? actualType.GetCustomAttribute<ConfigValueParserAttribute>();
        }

        /// <summary>
        /// 判断类型是否提供 static Parse(string)。
        /// </summary>
        /// <param name="actualType">目标类型。</param>
        /// <returns>存在静态解析方法时返回 true。</returns>
        private static bool HasStaticParse(Type actualType)
        {
            if (actualType == typeof(string)
                || actualType.IsEnum
                || actualType.IsPrimitive
                || actualType == typeof(decimal)
                || actualType == typeof(DateTime))
            {
                return false;
            }

            return actualType.GetMethod(
                "Parse",
                BindingFlags.Public | BindingFlags.Static,
                null,
                new[] { typeof(string) },
                null) != null;
        }
    }
}
