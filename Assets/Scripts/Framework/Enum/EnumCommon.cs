namespace Framework
{
    /// <summary>
    /// 登录类型
    /// </summary>
    public enum ELoadingType
    {
        None,
        Login, // 登录
        Scene, // 场景
    }

    /// <summary>
    /// 项目支持的显示语言类型。
    /// 枚举用于代码侧安全选择语言，实际读取配表时会映射为 zh_cn / en_us 等列名。
    /// </summary>
    public enum LanguageType
    {
        /// <summary>简体中文，对应 language 表的 zh_cn 列。</summary>
        ZhCn,

        /// <summary>英语，对应 language 表的 en_us 列。</summary>
        EnUs,
    }
}
