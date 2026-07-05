using System;

namespace Framework.Data
{
    /// <summary>
    /// 范围特性 - 用于数值范围校验
    /// 注意：此特性主要用于Excel导出工具的数据校验，ConfigBase本身不执行此校验
    /// </summary>
    [AttributeUsage(AttributeTargets.Property)]
    public class RangeAttribute : Attribute
    {
        /// <summary>
        /// 最小值
        /// </summary>
        public double Min { get; }

        /// <summary>
        /// 最大值
        /// </summary>
        public double Max { get; }

        /// <summary>
        /// 构造函数
        /// </summary>
        /// <param name="min">最小值</param>
        /// <param name="max">最大值</param>
        public RangeAttribute(double min, double max)
        {
            Min = min;
            Max = max;
        }
    }

    /// <summary>
    /// 外键特性 - 用于外键引用校验
    /// 注意：此特性主要用于Excel导出工具的数据校验，ConfigBase本身不执行此校验
    /// </summary>
    [AttributeUsage(AttributeTargets.Property)]
    public class ForeignKeyAttribute : Attribute
    {
        /// <summary>
        /// 引用的配置表类型
        /// </summary>
        public Type ReferenceType { get; }

        /// <summary>
        /// 构造函数
        /// </summary>
        /// <param name="referenceType">引用的配置表类型</param>
        public ForeignKeyAttribute(Type referenceType)
        {
            ReferenceType = referenceType;
        }
    }
}
