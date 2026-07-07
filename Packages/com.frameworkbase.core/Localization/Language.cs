using System;
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
        /// 将语言枚举转换为配表语言列名。
        /// </summary>
        /// <param name="languageType">语言枚举。</param>
        /// <returns>配表语言列名。</returns>
        public static string ToCode(LanguageType languageType)
        {
            switch (languageType)
            {
                case LanguageType.EnUs:
                    return "en_us";
                case LanguageType.ZhCn:
                default:
                    return DefaultLanguage;
            }
        }

        /// <summary>
        /// 将语言代码转换为语言枚举。
        /// </summary>
        /// <param name="language">语言代码，支持 zh-CN / zh_cn 等写法。</param>
        /// <returns>语言枚举；无法识别时返回 ZhCn。</returns>
        public static LanguageType ToType(string language)
        {
            switch (NormalizeLanguage(language))
            {
                case "en_us":
                    return LanguageType.EnUs;
                case "zh_cn":
                default:
                    return LanguageType.ZhCn;
            }
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
