using System;

namespace Framework
{
    /// <summary>
    /// CLDR 复数类别（Unicode 通用本地化数据仓库标准命名）。
    /// 一条文案按数量落入其中一类，取对应变体（如 en 的 one/other、ru 的 one/few/many/other、ar 全 6 类）。
    /// </summary>
    public enum PluralCategory
    {
        /// <summary>数量 0 的专用形态（阿拉伯语等）。</summary>
        Zero,

        /// <summary>单数形态（英语 1 item）。</summary>
        One,

        /// <summary>双数形态（阿拉伯语等）。</summary>
        Two,

        /// <summary>少量形态（斯拉夫语族 2~4 等）。</summary>
        Few,

        /// <summary>多量形态（斯拉夫语族 5+ 等）。</summary>
        Many,

        /// <summary>兜底/复数默认形态（无复数变化语言恒为此类）。</summary>
        Other,
    }

    /// <summary>
    /// 复数规则分类器（框架层纯逻辑，零 Unity 依赖，可直接单测）。
    /// 内置代表性规则家族的 CLDR 基数（cardinal）规则；未登记语言一律回退 <see cref="PluralCategory.Other"/>，
    /// 保证"接了但没覆盖的语言"退化为单一变体而非崩溃。新增语言只需在 <see cref="Select"/> 的分派表补一条。
    /// </summary>
    /// <remarks>
    /// <para>这是"最小集"：覆盖 6 大规则家族——东亚(Other only)、日耳曼/多数罗曼语(one/other)、
    /// 法语(0,1→one)、俄乌东斯拉夫(one/few/many/other)、波兰西斯拉夫、阿拉伯(全 6 类)。
    /// 追求"规则家族正确"而非"语言全覆盖"；未列语言安全退化，不会给出错误的复数形态。</para>
    /// <para>操作数按 CLDR 定义：<c>n</c>=绝对值、<c>i</c>=整数部分、<c>v</c>=可见小数位数。
    /// 游戏内数量绝大多数为整数（v=0），带小数时按 CLDR 通常落 other。</para>
    /// </remarks>
    public static class PluralRules
    {
        /// <summary>
        /// 判定数量在指定语言下的复数类别。
        /// </summary>
        /// <param name="language">语言代码，支持 en / en-US / en_us 等写法，只取主语言子标签参与判定。</param>
        /// <param name="number">数量（负数按绝对值处理）。</param>
        /// <returns>CLDR 复数类别；未登记语言返回 <see cref="PluralCategory.Other"/>。</returns>
        public static PluralCategory Select(string language, double number)
        {
            double n = Math.Abs(number);
            long i = (long)n;
            int v = (n > i) ? 1 : 0; // 有小数部分即视作有可见小数位

            switch (BaseLanguage(language))
            {
                // —— 无复数变化：东亚及东南亚多数语言，恒为 other ——
                case "zh":
                case "ja":
                case "ko":
                case "th":
                case "vi":
                case "id":
                case "ms":
                case "lo":
                case "km":
                case "my":
                    return PluralCategory.Other;

                // —— 法语族：0 和 1 视作单数 ——
                case "fr":
                    return (i == 0 || i == 1) ? PluralCategory.One : PluralCategory.Other;

                // —— 东斯拉夫（俄/乌）：one/few/many/other ——
                case "ru":
                case "uk":
                    return SlavicEast(i, v);

                // —— 波兰（西斯拉夫）：one/few/many/other，规则与东斯拉夫略异 ——
                case "pl":
                    return Polish(i, v);

                // —— 阿拉伯：全 6 类 ——
                case "ar":
                    return Arabic(n, i);

                // —— 日耳曼语族 + 多数罗曼语 + 其它 one/other 语言 ——
                case "en":
                case "de":
                case "nl":
                case "sv":
                case "da":
                case "no":
                case "nb":
                case "nn":
                case "es":
                case "it":
                case "pt":
                case "ca":
                case "gl":
                case "fi":
                case "et":
                case "el":
                case "tr":
                case "hu":
                    return (i == 1 && v == 0) ? PluralCategory.One : PluralCategory.Other;

                // —— 未登记语言：安全退化为单一 other 变体 ——
                default:
                    return PluralCategory.Other;
            }
        }

        /// <summary>
        /// 判定数量在指定语言下的复数类别（整数便捷重载）。
        /// </summary>
        /// <param name="language">语言代码。</param>
        /// <param name="number">整数数量。</param>
        /// <returns>CLDR 复数类别。</returns>
        public static PluralCategory Select(string language, long number)
            => Select(language, (double)number);

        /// <summary>
        /// 复数类别 → language 表 key 后缀（小写 CLDR 名，如 <c>one</c> / <c>other</c>）。
        /// 约定文案 key 为 <c>{keyBase}_{后缀}</c>。
        /// </summary>
        /// <param name="category">复数类别。</param>
        /// <returns>小写后缀。</returns>
        public static string KeySuffix(this PluralCategory category)
        {
            switch (category)
            {
                case PluralCategory.Zero: return "zero";
                case PluralCategory.One: return "one";
                case PluralCategory.Two: return "two";
                case PluralCategory.Few: return "few";
                case PluralCategory.Many: return "many";
                default: return "other";
            }
        }

        /// <summary>东斯拉夫（ru/uk）规则。</summary>
        private static PluralCategory SlavicEast(long i, int v)
        {
            if (v != 0)
                return PluralCategory.Other;

            long mod10 = i % 10;
            long mod100 = i % 100;

            if (mod10 == 1 && mod100 != 11)
                return PluralCategory.One;
            if (mod10 >= 2 && mod10 <= 4 && !(mod100 >= 12 && mod100 <= 14))
                return PluralCategory.Few;
            return PluralCategory.Many; // mod10==0 || 5..9 || mod100 11..14
        }

        /// <summary>波兰（pl）规则。</summary>
        private static PluralCategory Polish(long i, int v)
        {
            if (i == 1 && v == 0)
                return PluralCategory.One;
            if (v != 0)
                return PluralCategory.Other;

            long mod10 = i % 10;
            long mod100 = i % 100;

            if (mod10 >= 2 && mod10 <= 4 && !(mod100 >= 12 && mod100 <= 14))
                return PluralCategory.Few;
            // many: i!=1 且 (mod10 0..1 或 mod10 5..9 或 mod100 12..14)
            if ((mod10 <= 1) || (mod10 >= 5 && mod10 <= 9) || (mod100 >= 12 && mod100 <= 14))
                return PluralCategory.Many;
            return PluralCategory.Other;
        }

        /// <summary>阿拉伯（ar）规则，覆盖全 6 类。</summary>
        private static PluralCategory Arabic(double n, long i)
        {
            if (n == 0)
                return PluralCategory.Zero;
            if (n == 1)
                return PluralCategory.One;
            if (n == 2)
                return PluralCategory.Two;

            long mod100 = i % 100;
            if (mod100 >= 3 && mod100 <= 10)
                return PluralCategory.Few;
            if (mod100 >= 11 && mod100 <= 99)
                return PluralCategory.Many;
            return PluralCategory.Other;
        }

        /// <summary>取主语言子标签：en-US / en_us → en。</summary>
        private static string BaseLanguage(string language)
        {
            if (string.IsNullOrEmpty(language))
                return string.Empty;

            string norm = language.Trim().Replace('-', '_').ToLowerInvariant();
            int idx = norm.IndexOf('_');
            return idx > 0 ? norm.Substring(0, idx) : norm;
        }
    }
}
