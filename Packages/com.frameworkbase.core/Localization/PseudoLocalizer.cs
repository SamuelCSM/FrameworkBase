using System.Text;

namespace Framework
{
    /// <summary>
    /// 伪本地化（pseudo-localization）：不等真翻译就暴露本地化问题的开发期工具。
    ///
    /// 打开 <see cref="Enabled"/> 后，所有经 <see cref="Language"/> 取出的文案被变形为
    /// <c>⟦Ẃéĺćóḿé·~·~⟧</c> 风格：
    ///   · 拉丁字母替换为带重音变体——字体缺字/编码问题立刻可见；
    ///   · 按原文长度追加 ~30% 填充——德语/俄语等更长语言的 UI 截断提前暴露；
    ///   · 前后 ⟦ ⟧ 界标——没被界标包住的文本 = 写死没走 Language，一眼识别；
    ///   · <c>{0}</c> / <c>{1:N0}</c> 等格式占位符原样保留，string.Format 不受影响。
    ///
    /// 仅 Editor / Development Build 生效（Language 出口条件编译），正式包零开销。
    /// </summary>
    public static class PseudoLocalizer
    {
        /// <summary>伪本地化总开关（开发期用；切换后调 Language.Refresh() 刷新已显示文本）。</summary>
        public static bool Enabled;

        /// <summary>
        /// 变形一段文案（纯函数，可单测）。null/空串原样返回。
        /// </summary>
        public static string Transform(string text)
        {
            if (string.IsNullOrEmpty(text))
                return text;

            var sb = new StringBuilder(text.Length * 2);
            sb.Append('⟦');

            for (int i = 0; i < text.Length; i++)
            {
                char c = text[i];

                // 格式占位符 {…} 原样保留（变形花括号内的内容会让 string.Format 崩）
                if (c == '{')
                {
                    int close = text.IndexOf('}', i);
                    if (close > i)
                    {
                        sb.Append(text, i, close - i + 1);
                        i = close;
                        continue;
                    }
                }

                sb.Append(MapChar(c));
            }

            // 长度填充：模拟更长语言（德语/俄语约 +30%），提前暴露 UI 截断
            int padding = (text.Length + 2) / 3;
            for (int i = 0; i < padding; i++)
                sb.Append(i % 2 == 0 ? '·' : '~');

            sb.Append('⟧');
            return sb.ToString();
        }

        /// <summary>拉丁字母 → 带重音变体（其余字符原样，中文/数字/符号不动）。</summary>
        private static char MapChar(char c)
        {
            switch (c)
            {
                case 'a': return 'á';
                case 'e': return 'é';
                case 'i': return 'í';
                case 'o': return 'ó';
                case 'u': return 'ú';
                case 'c': return 'ç';
                case 'n': return 'ñ';
                case 'y': return 'ý';
                case 'A': return 'Á';
                case 'E': return 'É';
                case 'I': return 'Í';
                case 'O': return 'Ó';
                case 'U': return 'Ú';
                case 'C': return 'Ç';
                case 'N': return 'Ñ';
                case 'Y': return 'Ý';
                default: return c;
            }
        }
    }
}
