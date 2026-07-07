using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;
using Framework.Data;
using Framework.Table;
using TMPro;
using UnityEditor;
using UnityEngine;

namespace Framework.Editor
{
    /// <summary>
    /// 字体覆盖检查（缺字检测）：扫描 language 表全部语言列的全部字符，
    /// 逐个检查选中的 TMP 字体资产（含 fallback 回退链递归）是否覆盖，
    /// 输出缺字清单——上线前跑一遍，避免真机上出豆腐块才发现字库没扩。
    ///
    /// 用法：Project 里选中一个或多个 TMP_FontAsset →
    /// 菜单 Framework → Localization → Check Font Coverage (language 表)。
    /// 文案来源：StreamingAssets/RefData/config.db 的 language 表
    /// （先跑 ExcelTool 导出，保证 db 是最新文案）。
    /// </summary>
    public static class FontCoverageChecker
    {
        private const string DbRelativePath = "RefData/config.db";
        private const int MaxReportChars = 200;

        [MenuItem("Framework/Localization/Check Font Coverage (language 表)")]
        public static void CheckSelectedFonts()
        {
            var fonts = new List<TMP_FontAsset>();
            foreach (Object obj in Selection.objects)
            {
                if (obj is TMP_FontAsset font)
                    fonts.Add(font);
            }

            if (fonts.Count == 0)
            {
                EditorUtility.DisplayDialog("字体覆盖检查",
                    "请先在 Project 窗口选中一个或多个 TMP_FontAsset 再执行。", "确定");
                return;
            }

            HashSet<char> characters = CollectLanguageTableCharacters(out int rowCount);
            if (characters == null)
                return;

            Debug.Log($"[FontCoverage] language 表 {rowCount} 行，去重字符 {characters.Count} 个，" +
                      $"检查字体 {fonts.Count} 个（含各自 fallback 链）");

            foreach (TMP_FontAsset font in fonts)
                CheckFont(font, characters);
        }

        /// <summary>检查单个字体（含 fallback 链）对字符集的覆盖，输出缺字报告。</summary>
        private static void CheckFont(TMP_FontAsset font, HashSet<char> characters)
        {
            var missing = new List<char>();
            foreach (char c in characters)
            {
                if (char.IsWhiteSpace(c) || char.IsControl(c))
                    continue;
                if (!HasCharacterWithFallbacks(font, c, new HashSet<TMP_FontAsset>()))
                    missing.Add(c);
            }

            if (missing.Count == 0)
            {
                Debug.Log($"[FontCoverage] ✓ {font.name}: 全覆盖（含 fallback 链）");
                return;
            }

            missing.Sort();
            var sb = new StringBuilder();
            sb.Append($"[FontCoverage] ✗ {font.name}: 缺字 {missing.Count} 个");
            sb.AppendLine("（真机将显示豆腐块，需扩字库或补 fallback 字体）：");
            int shown = Mathf.Min(missing.Count, MaxReportChars);
            for (int i = 0; i < shown; i++)
            {
                sb.Append(missing[i]).Append("(U+").Append(((int)missing[i]).ToString("X4")).Append(") ");
                if ((i + 1) % 10 == 0)
                    sb.AppendLine();
            }
            if (missing.Count > shown)
                sb.Append($"…… 其余 {missing.Count - shown} 个从略");
            Debug.LogError(sb.ToString());
        }

        /// <summary>递归查字体及其 fallback 链是否含指定字符（visited 防 fallback 环）。</summary>
        private static bool HasCharacterWithFallbacks(TMP_FontAsset font, char c, HashSet<TMP_FontAsset> visited)
        {
            if (font == null || !visited.Add(font))
                return false;

            if (font.characterLookupTable != null && font.characterLookupTable.ContainsKey(c))
                return true;

            if (font.fallbackFontAssetTable != null)
            {
                foreach (TMP_FontAsset fallback in font.fallbackFontAssetTable)
                {
                    if (HasCharacterWithFallbacks(fallback, c, visited))
                        return true;
                }
            }
            return false;
        }

        /// <summary>读 language 表全部行、聚合所有语言列（LanguageRef 的 string 字段，Key 除外）的字符。</summary>
        private static HashSet<char> CollectLanguageTableCharacters(out int rowCount)
        {
            rowCount = 0;
            string dbPath = Path.Combine(Application.streamingAssetsPath, DbRelativePath);
            if (!File.Exists(dbPath))
            {
                EditorUtility.DisplayDialog("字体覆盖检查",
                    $"未找到配置库：{dbPath}\n请先用 ExcelTool 导出配置。", "确定");
                return null;
            }

            var characters = new HashSet<char>();
            using (var db = new SQLiteHelper(dbPath))
            {
                List<LanguageRef> rows = db.QueryConfigTable<LanguageRef>("language");
                rowCount = rows.Count;

                // 语言列随项目扩展（Zh_cn/En_us/…），按反射遍历全部 string 属性，Key 列除外
                PropertyInfo[] props = typeof(LanguageRef).GetProperties(BindingFlags.Public | BindingFlags.Instance);
                foreach (LanguageRef row in rows)
                {
                    foreach (PropertyInfo prop in props)
                    {
                        if (prop.PropertyType != typeof(string) || prop.Name == "Key" || !prop.CanRead)
                            continue;
                        var text = (string)prop.GetValue(row);
                        if (string.IsNullOrEmpty(text))
                            continue;
                        foreach (char c in text)
                            characters.Add(c);
                    }
                }
            }
            return characters;
        }
    }
}
