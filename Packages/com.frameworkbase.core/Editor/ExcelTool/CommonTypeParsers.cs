using System;
using System.Linq;
using UnityEngine;

namespace Editor.ExcelTool
{
    /// <summary>
    /// 常用类型解析器集合
    /// 
    /// 分隔符使用规范：
    /// - 逗号 (,)  : 用于同级元素分隔（数组、列表、坐标、颜色等）
    /// - 冒号 (:)  : 用于键值对（如 itemId:count）
    /// - 分号 (;)  : 用于不同记录/组的分隔（如多个奖励组）
    /// - 竖线 (|)  : 用于复杂结构的字段分隔
    /// - 减号/波浪 (-/~) : 用于范围表示（如 100-200）
    /// 
    /// 示例：
    /// - 坐标: 100,200,300
    /// - 颜色: 255,0,0 或 #FF0000
    /// - 键值对: 1001:10
    /// - 多个奖励: 1001:10;1002:5;1003:1
    /// - 范围: 100-200
    /// </summary>
    /// <summary>
    /// 分隔符解析器基类
    /// 用于解析类似 "1001:10" 或 "100,200,300" 这样的格式
    /// </summary>
    public abstract class DelimiterParserBase : ICustomTypeParser
    {
        protected char[] Delimiters { get; set; }

        protected DelimiterParserBase(params char[] delimiters)
        {
            Delimiters = delimiters ?? new[] { ':', ',' };
        }

        public abstract object Parse(string value, Type targetType);
        public abstract bool CanParse(Type targetType);

        /// <summary>
        /// 分割字符串
        /// </summary>
        protected string[] Split(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return Array.Empty<string>();
            }

            return value.Split(Delimiters, StringSplitOptions.RemoveEmptyEntries)
                        .Select(s => s.Trim())
                        .ToArray();
        }
    }

    /// <summary>
    /// 键值对解析器
    /// 格式：key:value 或 key=value
    /// 示例：1001:10 表示物品ID为1001，数量为10
    /// </summary>
    /// <example>
    /// public class ItemReward
    /// {
    ///     public int ItemId { get; set; }
    ///     public int Count { get; set; }
    ///     
    ///     public static ItemReward Parse(string value)
    ///     {
    ///         var parts = value.Split(':');
    ///         return new ItemReward 
    ///         { 
    ///             ItemId = int.Parse(parts[0]), 
    ///             Count = int.Parse(parts[1]) 
    ///         };
    ///     }
    /// }
    /// </example>
    public class KeyValueParser : DelimiterParserBase
    {
        public KeyValueParser() : base(':', '=') { }

        public override object Parse(string value, Type targetType)
        {
            var parts = Split(value);
            
            if (parts.Length < 2)
            {
                throw new FormatException($"键值对格式错误: {value}，期望格式: key:value 或 key=value");
            }

            // 尝试调用静态 Parse 方法
            var parseMethod = targetType.GetMethod("Parse", 
                System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static,
                null, new[] { typeof(string) }, null);

            if (parseMethod != null)
            {
                return parseMethod.Invoke(null, new object[] { value });
            }

            // 尝试使用构造函数（假设有两个参数的构造函数）
            var constructor = targetType.GetConstructors()
                .FirstOrDefault(c => c.GetParameters().Length == 2);

            if (constructor != null)
            {
                var parameters = constructor.GetParameters();
                var arg1 = Convert.ChangeType(parts[0], parameters[0].ParameterType);
                var arg2 = Convert.ChangeType(parts[1], parameters[1].ParameterType);
                return constructor.Invoke(new[] { arg1, arg2 });
            }

            throw new NotSupportedException($"类型 {targetType.Name} 不支持键值对解析");
        }

        public override bool CanParse(Type targetType)
        {
            return targetType.IsClass && !targetType.IsAbstract;
        }
    }

    /// <summary>
    /// 多值列表解析器
    /// 格式：value1;value2;value3（使用分号分隔不同记录）
    /// 也支持逗号作为备选分隔符
    /// 示例：1001;1002;1003 表示多个物品ID
    /// </summary>
    public class MultiValueParser : DelimiterParserBase
    {
        public MultiValueParser() : base(';', ',', '|') { }

        public override object Parse(string value, Type targetType)
        {
            var parts = Split(value);

            // 尝试调用静态 Parse 方法
            var parseMethod = targetType.GetMethod("Parse",
                System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static,
                null, new[] { typeof(string) }, null);

            if (parseMethod != null)
            {
                return parseMethod.Invoke(null, new object[] { value });
            }

            throw new NotSupportedException($"类型 {targetType.Name} 不支持多值解析");
        }

        public override bool CanParse(Type targetType)
        {
            return targetType.IsClass && !targetType.IsAbstract;
        }
    }

    /// <summary>
    /// Vector2 解析器
    /// 格式：x,y（使用逗号分隔）
    /// 示例：100,200
    /// </summary>
    public class Vector2Parser : ICustomTypeParser
    {
        public object Parse(string value, Type targetType)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return Vector2.zero;
            }

            var parts = value.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries);
            
            if (parts.Length < 2)
            {
                throw new FormatException($"Vector2 格式错误: {value}，期望格式: x,y");
            }

            float x = float.Parse(parts[0].Trim());
            float y = float.Parse(parts[1].Trim());

            return new Vector2(x, y);
        }

        public bool CanParse(Type targetType)
        {
            return targetType == typeof(Vector2);
        }
    }

    /// <summary>
    /// Vector3 解析器
    /// 格式：x,y,z（使用逗号分隔）
    /// 示例：100,200,300
    /// </summary>
    public class Vector3Parser : ICustomTypeParser
    {
        public object Parse(string value, Type targetType)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return Vector3.zero;
            }

            var parts = value.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries);
            
            if (parts.Length < 3)
            {
                throw new FormatException($"Vector3 格式错误: {value}，期望格式: x,y,z");
            }

            float x = float.Parse(parts[0].Trim());
            float y = float.Parse(parts[1].Trim());
            float z = float.Parse(parts[2].Trim());

            return new Vector3(x, y, z);
        }

        public bool CanParse(Type targetType)
        {
            return targetType == typeof(Vector3);
        }
    }

    /// <summary>
    /// Color 解析器
    /// 支持多种格式：
    /// 1. 十六进制: #RRGGBB 或 #RRGGBBAA
    /// 2. RGB: r,g,b 或 r,g,b,a (0-255)
    /// 3. 归一化: r,g,b 或 r,g,b,a (0.0-1.0)
    /// 注意：使用逗号分隔，不使用分号（分号用于记录分隔）
    /// </summary>
    public class ColorParser : ICustomTypeParser
    {
        public object Parse(string value, Type targetType)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return Color.white;
            }

            value = value.Trim();

            // 十六进制格式
            if (value.StartsWith("#"))
            {
                if (ColorUtility.TryParseHtmlString(value, out Color color))
                {
                    return color;
                }
                throw new FormatException($"Color 十六进制格式错误: {value}");
            }

            // RGB/RGBA 格式（只使用逗号分隔）
            var parts = value.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries);
            
            if (parts.Length < 3)
            {
                throw new FormatException($"Color 格式错误: {value}，期望格式: r,g,b 或 #RRGGBB");
            }

            float r = float.Parse(parts[0].Trim());
            float g = float.Parse(parts[1].Trim());
            float b = float.Parse(parts[2].Trim());
            float a = parts.Length > 3 ? float.Parse(parts[3].Trim()) : 1f;

            // 如果值大于1，认为是0-255范围，需要归一化
            if (r > 1f || g > 1f || b > 1f)
            {
                r /= 255f;
                g /= 255f;
                b /= 255f;
                if (a > 1f) a /= 255f;
            }

            return new Color(r, g, b, a);
        }

        public bool CanParse(Type targetType)
        {
            return targetType == typeof(Color) || targetType == typeof(Color32);
        }
    }
}
