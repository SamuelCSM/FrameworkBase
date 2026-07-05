using System;

namespace Editor.ExcelTool
{
    /// <summary>
    /// 自定义类型解析器接口
    /// 用于将 Excel 单元格字符串解析为自定义类型
    /// </summary>
    public interface ICustomTypeParser
    {
        /// <summary>
        /// 解析字符串为目标类型
        /// </summary>
        /// <param name="value">Excel 单元格的字符串值</param>
        /// <param name="targetType">目标类型</param>
        /// <returns>解析后的对象</returns>
        object Parse(string value, Type targetType);

        /// <summary>
        /// 检查是否可以解析指定类型
        /// </summary>
        /// <param name="targetType">目标类型</param>
        /// <returns>如果可以解析返回 true</returns>
        bool CanParse(Type targetType);
    }

    /// <summary>
    /// 自定义类型解析器特性
    /// 用于标记自定义类型使用哪个解析器
    /// </summary>
    /// <example>
    /// [CustomTypeParser(typeof(RewardParser))]
    /// public class Reward
    /// {
    ///     public int ItemId { get; set; }
    ///     public int Count { get; set; }
    /// }
    /// </example>
    [AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct)]
    public class CustomTypeParserAttribute : Attribute
    {
        /// <summary>
        /// 解析器类型（必须实现 ICustomTypeParser 接口）
        /// </summary>
        public Type ParserType { get; }

        /// <summary>
        /// 构造函数
        /// </summary>
        /// <param name="parserType">解析器类型</param>
        public CustomTypeParserAttribute(Type parserType)
        {
            if (!typeof(ICustomTypeParser).IsAssignableFrom(parserType))
            {
                throw new ArgumentException($"解析器类型 {parserType.Name} 必须实现 ICustomTypeParser 接口");
            }

            ParserType = parserType;
        }
    }
}
