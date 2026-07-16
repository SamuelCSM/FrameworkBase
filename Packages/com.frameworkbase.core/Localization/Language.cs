using System;
using System.Collections.Generic;
using System.Reflection;
using Framework.Core;
using Framework.Data;
using Framework.Save;
using Framework.Table;

namespace Framework
{
    /// <summary>
    /// 多语言访问入口。
    /// 负责读取 language 配表，并按当前语言返回对应文案；查找失败时返回原始 key，保证启动流程不中断。
    /// </summary>
    public static class Language
    {
        /// <summary>程序代码主动控制的多语言 key 前缀，例如 Loading 状态、错误提示、动态拼接文案。</summary>
        public const string CodePrefix = "#1";

        /// <summary>UI 静态文本自动翻译 key 前缀，主要给 TextMeshProEx 在 Prefab 上直接使用。</summary>
        public const string AutoTextPrefix = "#2";

        /// <summary>默认语言列名。与生成后的 LanguageRef.Zh_cn 字段对应。</summary>
        public const string DefaultLanguage = "zh_cn";

        /// <summary>当前语言，来自 PlayerPrefs；读取时统一规范成小写下划线格式，例如 zh_cn / en_us。</summary>
        public static string CurrentLanguage
            => NormalizeLanguage(SaveManager.Instance.GetPref(PlayerSettings.Language, DefaultLanguage));

        /// <summary>当前语言枚举。无法识别存档中的语言字符串时回退到简体中文。</summary>
        public static LanguageType CurrentLanguageType => ToType(CurrentLanguage);

        /// <summary>当前语言的书写方向（供 UI 层决定整体镜像布局与对齐）。</summary>
        public static TextDirection CurrentDirection => TextDirectionResolver.Of(CurrentLanguage);

        /// <summary>当前语言是否从右到左书写。</summary>
        public static bool IsCurrentRightToLeft => TextDirectionResolver.IsRightToLeft(CurrentLanguage);

        /// <summary>
        /// 切换当前语言并广播刷新事件。
        /// </summary>
        /// <param name="languageType">目标语言枚举。</param>
        public static void SetLanguage(LanguageType languageType)
        {
            SetLanguage(ToCode(languageType));
        }

        /// <summary>
        /// 切换当前语言并广播刷新事件。
        /// </summary>
        /// <param name="language">目标语言代码，支持 zh-CN / zh_cn 等写法。</param>
        public static void SetLanguage(string language)
        {
            string normalized = NormalizeLanguage(language);
            if (string.IsNullOrEmpty(normalized))
                normalized = DefaultLanguage;

            if (CurrentLanguage == normalized)
                return;

            SaveManager.Instance.SetPref(PlayerSettings.Language, normalized);
            Refresh();
        }

        /// <summary>
        /// 主动广播多语言刷新事件。
        /// 用于配置库加载完成、热更配置替换完成、手动切语言后刷新所有 TextMeshProEx。
        /// </summary>
        public static void Refresh()
        {
            GameEntry.Event?.Publish(GameMessage.LanguageChanged, CurrentLanguage);
        }

        /// <summary>
        /// 根据 key 获取当前语言文案。
        /// </summary>
        /// <param name="key">language 表中的 Key。</param>
        /// <returns>当前语言文案；找不到时返回原 key。</returns>
        public static string Get(string key)
        {
            return TryGet(key, out string value) ? value : (key ?? string.Empty);
        }

        /// <summary>
        /// 根据 key 获取当前语言文案，并按 string.Format 规则填充参数。
        /// </summary>
        /// <param name="key">language 表中的 Key。</param>
        /// <param name="args">格式化参数，例如数量、玩家名、倒计时秒数。</param>
        /// <returns>格式化后的当前语言文案；找不到或格式错误时返回安全兜底文本。</returns>
        public static string Get(string key, params object[] args)
        {
            string value = Get(key);
            if (args == null || args.Length == 0)
                return value;

            try
            {
                return string.Format(value, args);
            }
            catch (FormatException)
            {
                return value;
            }
        }

        /// <summary>
        /// 取当前语言文案，缺 key 时回退到调用处内联的<b>源语言默认值</b>（而非把 key 吐给玩家）。
        /// <para>
        /// 用于框架自身在 language 配表尚未加载 / 尚未补该行时仍需出字的场景（启动 Loading、登录状态提示）：
        /// 文案照常经本地化通道，配表补上 <paramref name="key"/> 行即自动翻译；缺行时用 <paramref name="fallback"/> 兜底。
        /// 与直接写死字符串的本质区别是——它<b>可翻译</b>：接入方补一行配表即可切语言，无需改框架源码，
        /// 也不会像 <see cref="Get(string)"/> 那样在缺 key 时把 <paramref name="key"/> 原样显示给玩家。
        /// </para>
        /// </summary>
        /// <param name="key">language 表中的 Key（约定 <see cref="CodePrefix"/> 前缀）。</param>
        /// <param name="fallback">缺 key（含配表未加载）时的源语言默认值，由调用处内联。</param>
        /// <returns>命中配表则返回其文案；否则返回 <paramref name="fallback"/>。</returns>
        public static string GetOrDefault(string key, string fallback)
        {
            if (TryGet(key, out string value))
                return value;

#if UNITY_EDITOR || DEVELOPMENT_BUILD
            // 已走本地化通道、只是配表还没这条 key：兜底值同样过一遍伪本地化，
            // 让「已接入本地化（有 ⟦⟧ 界标）但缺翻译」与「根本没走 Language（无界标）」在屏幕上仍可区分。
            if (PseudoLocalizer.Enabled)
                return PseudoLocalizer.Transform(fallback ?? string.Empty);
#endif
            return fallback ?? string.Empty;
        }

        /// <summary>
        /// 按数量取当前语言的复数文案。
        /// 依当前语言的 CLDR 复数规则选变体 key <c>{keyBase}_{类别}</c>（如 <c>_one</c> / <c>_other</c>），
        /// 命中即用，缺该变体时回退 <c>{keyBase}_other</c>，仍缺则返回 <paramref name="keyBase"/> 兜底不吐空。
        /// </summary>
        /// <param name="keyBase">复数文案 key 前缀，各变体形如 <c>{keyBase}_one</c> / <c>{keyBase}_other</c>。</param>
        /// <param name="count">数量，参与复数判定并作为默认格式化参数 <c>{0}</c>。</param>
        /// <returns>选中变体的格式化文案。</returns>
        public static string GetPlural(string keyBase, double count)
        {
            return GetPlural(keyBase, count, null);
        }

        /// <summary>
        /// 按数量取当前语言的复数文案，并自定义格式化参数。
        /// </summary>
        /// <param name="keyBase">复数文案 key 前缀。</param>
        /// <param name="count">数量，参与复数判定；未提供 <paramref name="args"/> 时作为默认 <c>{0}</c>。</param>
        /// <param name="args">格式化参数；为空时默认以 <paramref name="count"/> 填充 <c>{0}</c>。</param>
        /// <returns>选中变体的格式化文案。</returns>
        public static string GetPlural(string keyBase, double count, params object[] args)
        {
            if (string.IsNullOrEmpty(keyBase))
                return string.Empty;

            PluralCategory category = PluralRules.Select(CurrentLanguage, count);
            string suffix = category.KeySuffix();

            bool found = TryGet($"{keyBase}_{suffix}", out string text);
            if (!found && suffix != "other")
                found = TryGet($"{keyBase}_other", out text);
            if (!found)
                return keyBase; // 变体全缺时返回 keyBase，避免把残缺 key 吐给玩家

            object[] formatArgs = (args != null && args.Length > 0) ? args : new object[] { count };
            try
            {
                return string.Format(text, formatArgs);
            }
            catch (FormatException)
            {
                return text;
            }
        }

        /// <summary>
        /// 尝试根据 key 获取当前语言文案。
        /// </summary>
        /// <param name="key">language 表中的 Key。</param>
        /// <param name="value">查找到的文案；失败时为原 key 或空字符串。</param>
        /// <returns>找到可用文案返回 true，否则返回 false。</returns>
        public static bool TryGet(string key, out string value)
        {
            value = key ?? string.Empty;
            if (string.IsNullOrEmpty(key))
                return false;

            try
            {
                var table = GameEntry.RefData?.GetConfig<LanguageRefTable>();
                var row = table?.GetByKey(key);
                if (row == null)
                    return false;

                string localized = GetValue(row, CurrentLanguage);
                if (string.IsNullOrEmpty(localized) && CurrentLanguage != DefaultLanguage)
                    localized = GetValue(row, DefaultLanguage);

                if (string.IsNullOrEmpty(localized))
                    return false;

#if UNITY_EDITOR || DEVELOPMENT_BUILD
                // 伪本地化（开发期）：变形所有经 Language 取出的文案，
                // 没被 ⟦⟧ 界标包住的屏幕文本 = 写死没走本地化，一眼识别
                if (PseudoLocalizer.Enabled)
                    localized = PseudoLocalizer.Transform(localized);
#endif

                value = localized;
                return true;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// 解析 UI 自动翻译文本。
        /// 只有 #2 前缀会自动翻译，普通字符串保持原样，避免误翻玩家名、数字、版本号等动态内容。
        /// </summary>
        /// <param name="text">待解析文本。</param>
        /// <returns>解析后的文本。</returns>
        public static string ResolveAutoText(string text)
        {
            return IsAutoTextKey(text) ? Get(text) : (text ?? string.Empty);
        }

        /// <summary>
        /// 判断文本是否是任意多语言 key。
        /// </summary>
        /// <param name="text">待判断文本。</param>
        /// <returns>以 #1 或 #2 开头返回 true。</returns>
        public static bool IsLanguageKey(string text)
        {
            return !string.IsNullOrEmpty(text)
                   && (text.StartsWith(CodePrefix, StringComparison.Ordinal)
                       || text.StartsWith(AutoTextPrefix, StringComparison.Ordinal));
        }

        /// <summary>
        /// 判断文本是否是 UI 自动翻译 key。
        /// </summary>
        /// <param name="text">待判断文本。</param>
        /// <returns>以 #2 开头返回 true。</returns>
        public static bool IsAutoTextKey(string text)
        {
            return !string.IsNullOrEmpty(text)
                   && text.StartsWith(AutoTextPrefix, StringComparison.Ordinal);
        }

        /// <summary>
        /// 规范化语言代码，统一成小写下划线格式，便于映射到 LanguageRef 字段。
        /// </summary>
        /// <param name="language">原始语言代码。</param>
        /// <returns>规范化语言代码，例如 en-US -> en_us。</returns>
        public static string NormalizeLanguage(string language)
        {
            return string.IsNullOrWhiteSpace(language)
                ? DefaultLanguage
                : language.Trim().Replace('-', '_').ToLowerInvariant();
        }

        /// <summary>
        /// 语言枚举 ↔ 配表列名的单一映射源。
        /// 两个方向（<see cref="ToCode"/> / <see cref="ToType"/>）都从这里派生，新增语言只改这一处，杜绝漂移。
        /// </summary>
        private static readonly Dictionary<LanguageType, string> CodeByType = new Dictionary<LanguageType, string>
        {
            { LanguageType.ZhCn, "zh_cn" },
            { LanguageType.ZhTw, "zh_tw" },
            { LanguageType.EnUs, "en_us" },
            { LanguageType.JaJp, "ja_jp" },
            { LanguageType.KoKr, "ko_kr" },
            { LanguageType.FrFr, "fr_fr" },
            { LanguageType.DeDe, "de_de" },
            { LanguageType.EsEs, "es_es" },
            { LanguageType.PtBr, "pt_br" },
            { LanguageType.RuRu, "ru_ru" },
            { LanguageType.ArSa, "ar_sa" },
            { LanguageType.ThTh, "th_th" },
            { LanguageType.ViVn, "vi_vn" },
            { LanguageType.IdId, "id_id" },
            { LanguageType.TrTr, "tr_tr" },
        };

        /// <summary>配表列名 → 语言枚举（由 <see cref="CodeByType"/> 反向构建）。</summary>
        private static readonly Dictionary<string, LanguageType> TypeByCode = BuildTypeByCode();

        /// <summary>
        /// 将语言枚举转换为配表语言列名。
        /// </summary>
        /// <param name="languageType">语言枚举。</param>
        /// <returns>配表语言列名；未登记时回退默认语言。</returns>
        public static string ToCode(LanguageType languageType)
        {
            return CodeByType.TryGetValue(languageType, out string code) ? code : DefaultLanguage;
        }

        /// <summary>
        /// 将语言代码转换为语言枚举。
        /// </summary>
        /// <param name="language">语言代码，支持 zh-CN / zh_cn 等写法。</param>
        /// <returns>语言枚举；无法识别时返回 ZhCn。</returns>
        public static LanguageType ToType(string language)
        {
            return TypeByCode.TryGetValue(NormalizeLanguage(language), out LanguageType type)
                ? type
                : LanguageType.ZhCn;
        }

        /// <summary>反向构建"列名 → 枚举"表。</summary>
        private static Dictionary<string, LanguageType> BuildTypeByCode()
        {
            var map = new Dictionary<string, LanguageType>(CodeByType.Count);
            foreach (var pair in CodeByType)
                map[pair.Value] = pair.Key;
            return map;
        }

        /// <summary>
        /// 从 LanguageRef 行数据中读取指定语言列。
        /// </summary>
        /// <param name="row">language 表中的一行数据。</param>
        /// <param name="language">规范化语言代码。</param>
        /// <returns>指定语言列的文本；列不存在或为空时返回空字符串。</returns>
        private static string GetValue(LanguageRef row, string language)
        {
            if (row == null)
                return string.Empty;

            string propertyName = ToPropertyName(NormalizeLanguage(language));
            var property = typeof(LanguageRef).GetProperty(
                propertyName,
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.IgnoreCase);

            return property?.GetValue(row) as string;
        }

        /// <summary>
        /// 将语言代码转换为生成类属性名。
        /// </summary>
        /// <param name="language">规范化语言代码，例如 zh_cn。</param>
        /// <returns>生成类属性名，例如 Zh_cn。</returns>
        private static string ToPropertyName(string language)
        {
            if (string.IsNullOrEmpty(language))
                return string.Empty;

            string[] parts = language.Split('_');
            for (int i = 0; i < parts.Length; i++)
            {
                if (string.IsNullOrEmpty(parts[i]))
                    continue;

                parts[i] = char.ToUpperInvariant(parts[i][0]) + parts[i].Substring(1);
            }

            return string.Join("_", parts);
        }
    }
}
