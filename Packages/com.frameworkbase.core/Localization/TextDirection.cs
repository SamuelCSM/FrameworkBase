using System.Collections.Generic;

namespace Framework
{
    /// <summary>文字书写方向。</summary>
    public enum TextDirection
    {
        /// <summary>从左到右（拉丁/中日韩等）。</summary>
        LeftToRight,

        /// <summary>从右到左（阿拉伯/希伯来/波斯等）。</summary>
        RightToLeft,
    }

    /// <summary>
    /// 书写方向解析器（框架层纯逻辑，零 Unity 依赖，可直接单测）。
    /// 提供"语言 → 方向"与"动态字符串是否含强 RTL 字符"两类判定，供 UI 层决定整体镜像布局、
    /// 对齐方式，以及给用户名/聊天等动态文本自动定向。真正的字形整形（连字/双向重排）由 TextMeshPro 负责，
    /// 本类只做方向决策，不做整形。
    /// </summary>
    public static class TextDirectionResolver
    {
        /// <summary>RTL 语言的主子标签集合（最小集：覆盖主流 RTL 书写系统）。</summary>
        private static readonly HashSet<string> RtlLanguages = new HashSet<string>
        {
            "ar",  // 阿拉伯语
            "he",  // 希伯来语（新 ISO 码）
            "iw",  // 希伯来语（旧 ISO 码，历史遗留）
            "fa",  // 波斯语
            "ur",  // 乌尔都语
            "ps",  // 普什图语
            "sd",  // 信德语
            "ug",  // 维吾尔语
            "yi",  // 意第绪语
            "dv",  // 迪维希语
            "ckb", // 中库尔德语（索拉尼）
        };

        /// <summary>
        /// 判定语言的书写方向。
        /// </summary>
        /// <param name="language">语言代码，支持 ar / ar-SA / ar_sa 等写法。</param>
        /// <returns>书写方向。</returns>
        public static TextDirection Of(string language)
            => IsRightToLeft(language) ? TextDirection.RightToLeft : TextDirection.LeftToRight;

        /// <summary>
        /// 语言是否从右到左书写。
        /// </summary>
        /// <param name="language">语言代码。</param>
        /// <returns>RTL 语言返回 true。</returns>
        public static bool IsRightToLeft(string language)
            => RtlLanguages.Contains(BaseLanguage(language));

        /// <summary>
        /// 字符串是否含"强 RTL"字符（阿拉伯/希伯来等 Unicode 区段）。
        /// 用于给动态文本（用户名、聊天、搜索词）自动判定对齐方向——即便 UI 语言是 LTR，
        /// 一段阿拉伯语用户名也应右对齐。
        /// </summary>
        /// <param name="text">待检测文本。</param>
        /// <returns>含强 RTL 字符返回 true。</returns>
        public static bool ContainsRightToLeft(string text)
        {
            if (string.IsNullOrEmpty(text))
                return false;

            foreach (char c in text)
            {
                if (IsStrongRtlChar(c))
                    return true;
            }

            return false;
        }

        /// <summary>判断单个字符是否落在强 RTL Unicode 区段。</summary>
        private static bool IsStrongRtlChar(char c)
        {
            // 希伯来 0x0590–0x05FF；阿拉伯 0x0600–0x06FF；阿拉伯补充 0x0750–0x077F；
            // 塔纳字母 0x0780–0x07BF；阿拉伯扩展-A 0x08A0–0x08FF；
            // 阿拉伯表现形式-A 0xFB50–0xFDFF；表现形式-B 0xFE70–0xFEFF。
            return (c >= 0x0590 && c <= 0x05FF)
                   || (c >= 0x0600 && c <= 0x06FF)
                   || (c >= 0x0750 && c <= 0x077F)
                   || (c >= 0x0780 && c <= 0x07BF)
                   || (c >= 0x08A0 && c <= 0x08FF)
                   || (c >= 0xFB50 && c <= 0xFDFF)
                   || (c >= 0xFE70 && c <= 0xFEFF);
        }

        /// <summary>取主语言子标签：ar-SA / ar_sa → ar。</summary>
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
