using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;
using Framework.Data;
using Framework.Table;
using TMPro;
using UnityEditor;
using UnityEngine;
using UnityEngine.TextCore.LowLevel;

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
        /// <summary>
        /// language 表候选库（按序尝试）：ADR-006 分片后 language 表独立在 language.db；
        /// 保留 config.db 回退以兼容未分片导出的老项目。
        /// </summary>
        private static readonly string[] DbRelativePaths = { "RefData/language.db", "RefData/config.db" };

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
        /// CI 门禁入口（batchmode 安全，无弹窗）：检查<b>玩家实际会用到的主字体链</b>对 language 表字符的覆盖。
        /// <para>
        /// 只评估 TMP Settings 的默认字体（<see cref="TMP_Settings.defaultFontAsset"/>）及其运行时回退链
        /// ——字体自身 fallback 表 + TMP Settings 全局 fallback 列表（递归去重）。这与运行时 TMP 解析字形的
        /// 路径一致：只有主字体链会渲染游戏文案，纯 fallback 用途的字体资产不必各自独立覆盖全字集，
        /// 否则「CJK 回退字体缺拉丁字母」这类伪缺字会误阻断门禁。
        /// </para>
        /// <para>
        /// 动态字体（<see cref="AtlasPopulationMode.Dynamic"/>）按<b>源字体文件</b>的字形覆盖判定，而非只看已烘焙的
        /// 静态图集——动态字体运行时按需从源文件补字形，只数静态表会把「运行时能出、图集还没烘」误判成缺字。
        /// </para>
        /// </summary>
        /// <param name="report">人类可读报告（主字体链与缺字摘要）。</param>
        /// <param name="fontsWithMissing">主字体链存在缺字时为 1，否则 0（沿用「>0 即阻断」的门禁语义）。</param>
        /// <returns>
        /// true = 已执行检查（report 为结果）；false = 前置不满足而跳过
        /// （language 库不存在 / 未配置默认字体），此时 report 为跳过原因，调用方应按「不阻断」处理。
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

            TMP_FontAsset primary = TMP_Settings.defaultFontAsset;
            if (primary == null)
            {
                report = "TMP Settings 未配置默认字体，跳过字体覆盖检查";
                return false;
            }

            // 运行时字形解析链：主字体 → 自身 fallback 表 → TMP Settings 全局 fallback（递归去重）。
            List<TMP_FontAsset> chain = BuildRuntimeFontChain(primary);
            List<char> missing = FindMissingInChain(chain, characters);

            var sb = new StringBuilder();
            sb.AppendLine($"[FontCoverage] language 表 {rowCount} 行，去重字符 {characters.Count} 个；" +
                          $"主字体链（{chain.Count} 个）：{string.Join(" → ", chain.ConvertAll(f => f.name))}");
            if (missing.Count == 0)
            {
                sb.AppendLine($"  ✓ 主字体链全覆盖 language 表字符（含全局 fallback 与动态字体源文件）");
            }
            else
            {
                fontsWithMissing = 1;
                sb.Append($"  ✗ 主字体链缺字 {missing.Count} 个（真机将显示豆腐块，需扩字库或补 fallback）：");
                int shown = Mathf.Min(missing.Count, MaxReportChars);
                for (int i = 0; i < shown; i++)
                    sb.Append(' ').Append(missing[i]).Append("(U+").Append(((int)missing[i]).ToString("X4")).Append(')');
                if (missing.Count > shown)
                    sb.Append($" …… 其余 {missing.Count - shown} 个从略");
            }

            report = sb.ToString().TrimEnd();
            return true;
        }

        /// <summary>
        /// 展开主字体的运行时回退链：BFS 遍历「自身 fallback 表 + TMP Settings 全局 fallback」，递归去重。
        /// 顺序即运行时字形查找顺序，主字体在首位。
        /// </summary>
        private static List<TMP_FontAsset> BuildRuntimeFontChain(TMP_FontAsset primary)
        {
            var ordered = new List<TMP_FontAsset>();
            var visited = new HashSet<TMP_FontAsset>();
            var queue = new Queue<TMP_FontAsset>();
            queue.Enqueue(primary);

            while (queue.Count > 0)
            {
                TMP_FontAsset font = queue.Dequeue();
                if (font == null || !visited.Add(font))
                    continue;
                ordered.Add(font);

                if (font.fallbackFontAssetTable != null)
                {
                    foreach (TMP_FontAsset fb in font.fallbackFontAssetTable)
                        queue.Enqueue(fb);
                }

                // 全局 fallback 只在主字体处并入一次（对整条链生效，无需每个字体重复并入）。
                if (font == primary && TMP_Settings.fallbackFontAssets != null)
                {
                    foreach (TMP_FontAsset fb in TMP_Settings.fallbackFontAssets)
                        queue.Enqueue(fb);
                }
            }
            return ordered;
        }

        /// <summary>求主字体链对字符集的缺字清单（升序；跳过空白/控制符；动态字体计源文件字形）。</summary>
        private static List<char> FindMissingInChain(List<TMP_FontAsset> chain, HashSet<char> characters)
        {
            var missing = new List<char>();
            foreach (char c in characters)
            {
                if (char.IsWhiteSpace(c) || char.IsControl(c))
                    continue;
                if (!ChainCoversChar(chain, c))
                    missing.Add(c);
            }
            missing.Sort();
            return missing;
        }

        /// <summary>字符是否被链上任一字体覆盖：静态图集命中，或动态字体源文件含该字形。</summary>
        private static bool ChainCoversChar(List<TMP_FontAsset> chain, char c)
        {
            foreach (TMP_FontAsset font in chain)
            {
                if (font.characterLookupTable != null && font.characterLookupTable.ContainsKey(c))
                    return true;
                if (font.atlasPopulationMode == AtlasPopulationMode.Dynamic && SourceFontHasGlyph(font, c))
                    return true;
            }
            return false;
        }

        /// <summary>动态字体的源字体文件是否含指定字符的字形（只读查询，不改图集）。</summary>
        private static bool SourceFontHasGlyph(TMP_FontAsset font, char c)
        {
            Font source = font.sourceFontFile;
            if (source == null)
                return false;
            // 字形索引查询与字号无关；LoadFontFace 失败（源文件缺失等）时保守视为不覆盖。
            if (FontEngine.LoadFontFace(source, 90, 0) != FontEngineError.Success)
                return false;
            return FontEngine.TryGetGlyphIndex((uint)c, out uint glyphIndex) && glyphIndex != 0;
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

        /// <summary>
        /// 读 language 表全部行、聚合所有语言列（LanguageRef 的 string 字段，Key 除外）的字符。
        /// 按 <see cref="DbRelativePaths"/> 顺序找第一个含 language 表的库（分片优先，回退主库）。
        /// </summary>
        /// <param name="rowCount">language 表行数。</param>
        /// <param name="silent">无可用库时是否静默返回 null（batchmode/CI 传 true，避免弹窗阻塞）。</param>
        private static HashSet<char> CollectLanguageTableCharacters(out int rowCount, bool silent = false)
        {
            rowCount = 0;
            foreach (string relativePath in DbRelativePaths)
            {
                string dbPath = Path.Combine(Application.streamingAssetsPath, relativePath);
                if (!File.Exists(dbPath))
                    continue;

                using (var db = new SQLiteHelper(dbPath))
                {
                    // 库存在不等于含 language 表：项目可能只导出了业务配置表。
                    // 无 language 表时尝试下一候选库，全部不含才按「跳过」处理——
                    // 字体覆盖检查本就依赖本地化数据，无数据即无从检查。
                    int hasLanguageTable = db.ExecuteScalar<int>(
                        "SELECT COUNT(1) FROM sqlite_master WHERE type='table' AND name='language'");
                    if (hasLanguageTable == 0)
                        continue;

                    List<LanguageRef> rows = db.QueryConfigTable<LanguageRef>("language");
                    rowCount = rows.Count;

                    var characters = new HashSet<char>();
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

                    return characters;
                }
            }

            if (!silent)
                EditorUtility.DisplayDialog("字体覆盖检查",
                    "未找到含 language 表的配置库（RefData/language.db 或 RefData/config.db）。\n请先用 ExcelTool 导出配置。", "确定");
            return null;
        }
    }
}
