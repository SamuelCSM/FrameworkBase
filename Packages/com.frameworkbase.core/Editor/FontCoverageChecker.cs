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

        /// <summary>
        /// CI 门禁入口（batchmode 安全，无弹窗）：扫描工程全部 TMP_FontAsset 对 language 表字符的覆盖。
        /// </summary>
        /// <param name="report">人类可读报告（逐字体覆盖/缺字摘要）。</param>
        /// <param name="fontsWithMissing">存在缺字的字体数量。</param>
        /// <returns>
        /// true = 已执行检查（report 为结果）；false = 前置不满足而跳过
        /// （config.db 不存在 / 工程无 TMP 字体），此时 report 为跳过原因，调用方应按「不阻断」处理。
        /// </returns>
        public static bool CheckFontsForCi(out string report, out int fontsWithMissing)
        {
            fontsWithMissing = 0;

            HashSet<char> characters = CollectLanguageTableCharacters(out int rowCount, silent: true);
            if (characters == null)
            {
                report = "language 配置库不存在，跳过字体覆盖检查（先跑 ExcelTool 导出 config.db）";
                return false;
            }

            string[] guids = AssetDatabase.FindAssets("t:TMP_FontAsset");
            if (guids.Length == 0)
            {
                report = "工程内无 TMP_FontAsset，跳过字体覆盖检查";
                return false;
            }

            var sb = new StringBuilder();
            sb.AppendLine($"[FontCoverage] language 表 {rowCount} 行，去重字符 {characters.Count} 个，检查字体 {guids.Length} 个：");
            foreach (string guid in guids)
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                var font = AssetDatabase.LoadAssetAtPath<TMP_FontAsset>(path);
                if (font == null)
                    continue;

                List<char> missing = FindMissing(font, characters);
                if (missing.Count == 0)
                {
                    sb.AppendLine($"  ✓ {font.name}: 全覆盖（含 fallback 链）");
                }
                else
                {
                    fontsWithMissing++;
                    sb.AppendLine($"  ✗ {font.name}: 缺字 {missing.Count} 个（真机将显示豆腐块）");
                }
            }

            report = sb.ToString().TrimEnd();
            return true;
        }

        /// <summary>检查单个字体（含 fallback 链）对字符集的覆盖，输出缺字报告（MenuItem 路径，直接落 Console）。</summary>
        private static void CheckFont(TMP_FontAsset font, HashSet<char> characters)
        {
            List<char> missing = FindMissing(font, characters);

            if (missing.Count == 0)
            {
                Debug.Log($"[FontCoverage] ✓ {font.name}: 全覆盖（含 fallback 链）");
                return;
            }

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

        /// <summary>求单个字体（含 fallback 链）对字符集的缺字清单（升序；跳过空白/控制符）。</summary>
        private static List<char> FindMissing(TMP_FontAsset font, HashSet<char> characters)
        {
            var missing = new List<char>();
            foreach (char c in characters)
            {
                if (char.IsWhiteSpace(c) || char.IsControl(c))
                    continue;
                if (!HasCharacterWithFallbacks(font, c, new HashSet<TMP_FontAsset>()))
                    missing.Add(c);
            }
            missing.Sort();
            return missing;
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
        /// <param name="rowCount">language 表行数。</param>
        /// <param name="silent">db 不存在时是否静默返回 null（batchmode/CI 传 true，避免弹窗阻塞）。</param>
        private static HashSet<char> CollectLanguageTableCharacters(out int rowCount, bool silent = false)
        {
            rowCount = 0;
            string dbPath = Path.Combine(Application.streamingAssetsPath, DbRelativePath);
            if (!File.Exists(dbPath))
            {
                if (!silent)
                    EditorUtility.DisplayDialog("字体覆盖检查",
                        $"未找到配置库：{dbPath}\n请先用 ExcelTool 导出配置。", "确定");
                return null;
            }

            var characters = new HashSet<char>();
            using (var db = new SQLiteHelper(dbPath))
            {
                // config.db 存在不等于含 language 表：项目可能只导出了业务配置表。
                // 无 language 表时按「跳过」处理（与 db 不存在同义），而非让 QueryConfigTable 抛
                // "no such table: language" 崩掉整个门禁——字体覆盖检查本就依赖本地化数据，无数据即无从检查。
                int hasLanguageTable = db.ExecuteScalar<int>(
                    "SELECT COUNT(1) FROM sqlite_master WHERE type='table' AND name='language'");
                if (hasLanguageTable == 0)
                {
                    if (!silent)
                        EditorUtility.DisplayDialog("字体覆盖检查",
                            $"配置库存在但无 language 表：{dbPath}\n（项目尚未导出本地化表，跳过检查）", "确定");
                    return null;
                }

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
