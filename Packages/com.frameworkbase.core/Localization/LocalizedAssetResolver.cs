using System;
using System.Collections.Generic;

namespace Framework
{
    /// <summary>
    /// 本地化资源候选地址解析（纯逻辑）：按「当前语言 → 自定义回退链 → 默认语言 → 原始地址」
    /// 生成有序候选列表，运行时逐个探测存在性取首个命中（见 <see cref="LocalizedAssets"/>）。
    /// <para>
    /// 地址约定：本地化变体 = <c>{基础地址}@{语言代码}</c>（如 <c>UI/title_banner@en_us</c>），
    /// 与文案 key 的 #1/#2 前缀约定同理——约定优于逐条登记，Addressables 里按此命名即接入。
    /// 原始地址（不带 @ 后缀）作为最终兜底：未本地化的资源无需任何改动，行为与从前一致。
    /// </para>
    /// <para>
    /// 回退链示例：<c>SetFallbackChain("zh_tw", "zh_cn")</c> 表示繁中缺资源时先借简中再落默认语言。
    /// 是否借用邻近语言是产品决策，框架不预设任何链（默认只回退到 <see cref="Language.DefaultLanguage"/>）。
    /// 线程约定：仅主线程访问。
    /// </para>
    /// </summary>
    public static class LocalizedAssetResolver
    {
        /// <summary>本地化变体地址的语言后缀分隔符。</summary>
        public const char Marker = '@';

        /// <summary>语言 → 回退链（覆盖式登记，链内语言按序尝试）。</summary>
        private static readonly Dictionary<string, string[]> FallbackChains =
            new Dictionary<string, string[]>(StringComparer.Ordinal);

        /// <summary>拼接指定语言的变体地址（语言代码统一规范化，zh-CN 与 zh_cn 等价）。</summary>
        public static string Localize(string baseAddress, string language)
        {
            ValidateBaseAddress(baseAddress);
            return baseAddress + Marker + Language.NormalizeLanguage(language);
        }

        /// <summary>
        /// 登记某语言的回退链（覆盖式）。传空数组或 null 即清除该语言的链。
        /// 组合根 / 业务启动期登记一次；链中的语言代码自动规范化。
        /// </summary>
        public static void SetFallbackChain(string language, params string[] fallbacks)
        {
            string key = Language.NormalizeLanguage(language);
            if (fallbacks == null || fallbacks.Length == 0)
            {
                FallbackChains.Remove(key);
                return;
            }

            var normalized = new string[fallbacks.Length];
            for (int i = 0; i < fallbacks.Length; i++)
                normalized[i] = Language.NormalizeLanguage(fallbacks[i]);
            FallbackChains[key] = normalized;
        }

        /// <summary>清除全部回退链（测试隔离用）。</summary>
        public static void ClearFallbackChains()
        {
            FallbackChains.Clear();
        }

        /// <summary>
        /// 生成有序候选地址：当前语言变体 → 回退链变体 → 默认语言变体 → 原始地址。
        /// 去重保序（如当前语言就是默认语言时不重复）；末位恒为原始地址（兜底，调用方可不探测直接用）。
        /// </summary>
        public static IReadOnlyList<string> GetCandidates(string baseAddress, string language)
        {
            ValidateBaseAddress(baseAddress);
            string current = Language.NormalizeLanguage(language);

            var result = new List<string>(4);
            var seenLanguages = new HashSet<string>(StringComparer.Ordinal);

            AppendCandidate(result, seenLanguages, baseAddress, current);
            if (FallbackChains.TryGetValue(current, out string[] chain))
            {
                for (int i = 0; i < chain.Length; i++)
                    AppendCandidate(result, seenLanguages, baseAddress, chain[i]);
            }
            AppendCandidate(result, seenLanguages, baseAddress, Language.DefaultLanguage);

            result.Add(baseAddress);
            return result;
        }

        private static void AppendCandidate(
            List<string> result, HashSet<string> seenLanguages, string baseAddress, string language)
        {
            if (seenLanguages.Add(language))
                result.Add(baseAddress + Marker + language);
        }

        private static void ValidateBaseAddress(string baseAddress)
        {
            if (string.IsNullOrWhiteSpace(baseAddress))
                throw new ArgumentException("基础地址不能为空。", nameof(baseAddress));
            if (baseAddress.IndexOf(Marker) >= 0)
            {
                throw new ArgumentException(
                    $"基础地址 '{baseAddress}' 已含语言分隔符 '{Marker}'——请传不带语言后缀的原始地址。",
                    nameof(baseAddress));
            }
        }
    }
}
