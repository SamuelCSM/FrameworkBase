using System;

namespace Framework.Data
{
    /// <summary>
    /// 标记生成的数据类为单例型 general 配置。
    /// </summary>
    [AttributeUsage(AttributeTargets.Class, Inherited = false)]
    public sealed class GeneralConfigAttribute : Attribute
    {
    }
}
