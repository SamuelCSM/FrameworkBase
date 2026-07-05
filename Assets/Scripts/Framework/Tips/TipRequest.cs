using System;

namespace Framework
{
    /// <summary>
    /// 轻提示请求数据，由业务层描述“提示什么”，展示层负责“如何显示”。
    /// </summary>
    [Serializable]
    public sealed class TipRequest
    {
        /// <summary>请求唯一编号，由 <see cref="TipManager"/> 入队时分配。</summary>
        public long RequestId { get; internal set; }

        /// <summary>提示文本或多语言 key。</summary>
        public string TextOrKey { get; set; }

        /// <summary>是否将 <see cref="TextOrKey"/> 作为多语言 key 处理。</summary>
        public bool IsLanguageKey { get; set; }

        /// <summary>多语言格式化参数，仅在 <see cref="IsLanguageKey"/> 为 true 时使用。</summary>
        public object[] FormatArgs { get; set; }

        /// <summary>提示视觉类型。</summary>
        public TipStyle Style { get; set; } = TipStyle.Normal;

        /// <summary>调度优先级。</summary>
        public TipPriority Priority { get; set; } = TipPriority.Normal;

        /// <summary>展示通道。</summary>
        public TipChannel Channel { get; set; } = TipChannel.Toast;

        /// <summary>停留时长，单位秒；小于等于 0 时由管理器按类型填充默认值。</summary>
        public float Duration { get; set; }

        /// <summary>去重键；为空时由文本、类型和通道自动生成。</summary>
        public string DedupeKey { get; set; }

        /// <summary>去重窗口时长，单位秒；小于等于 0 时使用管理器默认值。</summary>
        public float DedupeSeconds { get; set; }

        /// <summary>创建时间 UTC，用于日志和问题排查。</summary>
        public DateTime CreatedUtc { get; internal set; }
    }
}
