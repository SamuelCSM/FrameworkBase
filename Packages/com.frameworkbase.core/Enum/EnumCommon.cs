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
    /// 枚举用于代码侧安全选择语言，实际读取配表时会映射为 zh_cn / en_us 等列名（见 <see cref="Language.ToCode"/>）。
    /// 这里是"框架支持翻译到的语言全集"，具体某个项目开放哪几种由 app 决定；
    /// 未在 language 配表建对应列的语言取词时会回退到 <see cref="Language.DefaultLanguage"/>，不会崩。
    /// </summary>
    public enum LanguageType
    {
        /// <summary>简体中文，对应 language 表的 zh_cn 列。</summary>
        ZhCn,

        /// <summary>繁体中文，对应 language 表的 zh_tw 列。</summary>
        ZhTw,

        /// <summary>英语（美国），对应 language 表的 en_us 列。</summary>
        EnUs,

        /// <summary>日语，对应 language 表的 ja_jp 列。</summary>
        JaJp,

        /// <summary>韩语，对应 language 表的 ko_kr 列。</summary>
        KoKr,

        /// <summary>法语，对应 language 表的 fr_fr 列。</summary>
        FrFr,

        /// <summary>德语，对应 language 表的 de_de 列。</summary>
        DeDe,

        /// <summary>西班牙语，对应 language 表的 es_es 列。</summary>
        EsEs,

        /// <summary>葡萄牙语（巴西），对应 language 表的 pt_br 列。</summary>
        PtBr,

        /// <summary>俄语，对应 language 表的 ru_ru 列（复数 one/few/many/other）。</summary>
        RuRu,

        /// <summary>阿拉伯语，对应 language 表的 ar_sa 列（RTL，复数全 6 类）。</summary>
        ArSa,

        /// <summary>泰语，对应 language 表的 th_th 列。</summary>
        ThTh,

        /// <summary>越南语，对应 language 表的 vi_vn 列。</summary>
        ViVn,

        /// <summary>印尼语，对应 language 表的 id_id 列。</summary>
        IdId,

        /// <summary>土耳其语，对应 language 表的 tr_tr 列。</summary>
        TrTr,
    }
}
