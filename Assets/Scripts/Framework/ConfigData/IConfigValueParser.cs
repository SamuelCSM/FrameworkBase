using System;

namespace Framework.Data
{
    /// <summary>
    /// 配置值解析器接口，用于把配置单元格文本转换为业务强类型。
    /// </summary>
    public interface IConfigValueParser
    {
        /// <summary>
        /// 判断当前解析器是否支持目标类型。
        /// </summary>
        /// <param name="targetType">需要解析的目标类型。</param>
        /// <returns>支持时返回 true。</returns>
        bool CanParse(Type targetType);

        /// <summary>
        /// 将配置文本解析为目标类型实例。
        /// </summary>
        /// <param name="value">配置单元格原始文本。</param>
        /// <param name="targetType">需要解析的目标类型。</param>
        /// <returns>解析后的强类型值。</returns>
        object Parse(string value, Type targetType);
    }

    /// <summary>
    /// 指定配置值类型或字段使用的自定义解析器。
    /// </summary>
    [AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct | AttributeTargets.Property | AttributeTargets.Field)]
    public sealed class ConfigValueParserAttribute : Attribute
    {
        /// <summary>
        /// 解析器类型，必须实现 <see cref="IConfigValueParser"/>。
        /// </summary>
        public Type ParserType { get; }

        /// <summary>
        /// 创建配置值解析器特性。
        /// </summary>
        /// <param name="parserType">解析器类型。</param>
        public ConfigValueParserAttribute(Type parserType)
        {
            if (parserType == null)
            {
                throw new ArgumentNullException(nameof(parserType));
            }

            if (!typeof(IConfigValueParser).IsAssignableFrom(parserType))
            {
                throw new ArgumentException($"配置值解析器 {parserType.FullName} 必须实现 {nameof(IConfigValueParser)}。");
            }

            ParserType = parserType;
        }
    }
}
