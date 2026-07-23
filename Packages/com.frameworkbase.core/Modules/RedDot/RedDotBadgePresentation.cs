namespace Framework
{
    /// <summary>红点徽标一次刷新的纯展示结果：是否显示、显示什么文本。</summary>
    public readonly struct RedDotBadgeDisplay
    {
        public RedDotBadgeDisplay(bool visible, string text)
        {
            Visible = visible;
            Text = text;
        }

        /// <summary>徽标根是否应激活。</summary>
        public bool Visible { get; }

        /// <summary>徽标文本；Dot 与无文本样式为空字符串。</summary>
        public string Text { get; }
    }

    /// <summary>
    /// 红点徽标展示解析：把"最终计数 + 展示样式 + 封顶"映射为显隐与文本。纯函数、无 Unity 依赖，
    /// 便于单测。展示样式属于 UI 表现，不进入红点逻辑配置——同一个红点 ID 可被不同入口按不同样式呈现。
    /// </summary>
    public static class RedDotBadgePresentation
    {
        /// <summary>New 样式文本。</summary>
        public const string NewText = "NEW";

        /// <summary>Exclamation 样式文本。</summary>
        public const string ExclamationText = "!";

        /// <summary>
        /// 计数不大于 0 一律隐藏；否则按样式给出文本：
        /// Number 显示计数（超过封顶显示"上限+"），New/Exclamation 给出固定提示文本，Dot 只显隐无文本。
        /// </summary>
        public static RedDotBadgeDisplay Resolve(int count, RedDotBadge.DisplayMode mode, int maxDisplayCount)
        {
            if (count <= 0) return new RedDotBadgeDisplay(false, string.Empty);

            switch (mode)
            {
                case RedDotBadge.DisplayMode.Number:
                    string text = maxDisplayCount > 0 && count > maxDisplayCount
                        ? maxDisplayCount + "+"
                        : count.ToString();
                    return new RedDotBadgeDisplay(true, text);

                case RedDotBadge.DisplayMode.New:
                    return new RedDotBadgeDisplay(true, NewText);

                case RedDotBadge.DisplayMode.Exclamation:
                    return new RedDotBadgeDisplay(true, ExclamationText);

                case RedDotBadge.DisplayMode.DotOnly:
                default:
                    return new RedDotBadgeDisplay(true, string.Empty);
            }
        }

        /// <summary>
        /// 某美术根变体是否应激活：仅当徽标可见且该变体样式与当前展示样式一致。用于按样式在多个
        /// 图标变体（如小红点 / NEW 角标 / 感叹号图标）之间切换，其余变体一律关闭。
        /// </summary>
        public static bool ShouldShowVariant(
            bool visible, RedDotBadge.DisplayMode variantStyle, RedDotBadge.DisplayMode currentStyle)
            => visible && variantStyle == currentStyle;
    }
}
