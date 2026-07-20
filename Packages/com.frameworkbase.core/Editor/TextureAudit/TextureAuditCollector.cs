using System.Collections.Generic;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace Framework.Editor
{
    /// <summary>
    /// 纹理审计采集器：扫描 Assets/ 下全部纹理导入设置，交给 <see cref="TextureAuditRules"/> 出问题清单。
    /// 只扫 Assets/（Packages 不可变，第三方包的导入设置不归本工程管）。
    /// 触发方式：菜单 Framework → Audit Textures、CI 资源门禁 <see cref="CiGate"/>。
    /// </summary>
    public static class TextureAuditCollector
    {
        [MenuItem("Framework/Audit Textures")]
        public static void RunAndReport()
        {
            ValidateForCi(out string report, out int errorCount);
            if (errorCount > 0)
                Debug.LogError(report);
            else
                Debug.Log(report);
        }

        /// <summary>
        /// CI 入口：返回是否通过（无 Error 级问题），report 含逐条问题与统计。
        /// </summary>
        public static bool ValidateForCi(out string report, out int errorCount)
        {
            List<TextureAuditEntry> entries = CollectProjectTextures();
            List<TextureAuditIssue> issues = TextureAuditRules.Validate(entries, DefaultThresholds());

            errorCount = 0;
            var sb = new StringBuilder();
            sb.AppendLine($"[TextureAudit] 扫描 {entries.Count} 张纹理，问题 {issues.Count} 条。");

            foreach (TextureAuditIssue issue in issues)
            {
                if (issue.Severity == TextureAuditSeverity.Error)
                    errorCount++;
                sb.AppendLine("  " + issue);
            }

            report = sb.ToString().TrimEnd();
            return errorCount == 0;
        }

        /// <summary>项目级阈值调整与 Read/Write 豁免登记处（改这里，豁免随代码进评审）。</summary>
        public static TextureAuditThresholds DefaultThresholds()
        {
            return new TextureAuditThresholds();
        }

        private static List<TextureAuditEntry> CollectProjectTextures()
        {
            var entries = new List<TextureAuditEntry>();

            foreach (string guid in AssetDatabase.FindAssets("t:Texture2D", new[] { "Assets" }))
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                if (!(AssetImporter.GetAtPath(path) is TextureImporter importer))
                    continue; // RenderTexture / 字体图集等非导入器纹理不在审计范围

                // 尺寸取导入结果（实际入包成本），源图更大但被 maxTextureSize 压下来的不算超限
                var texture = AssetDatabase.LoadAssetAtPath<Texture2D>(path);
                if (texture == null)
                    continue;

                entries.Add(new TextureAuditEntry
                {
                    AssetPath = path,
                    Width = texture.width,
                    Height = texture.height,
                    ReadWriteEnabled = importer.isReadable,
                    Uncompressed = importer.textureCompression == TextureImporterCompression.Uncompressed,
                    MipmapsEnabled = importer.mipmapEnabled,
                    IsSprite = importer.textureType == TextureImporterType.Sprite,
                });
            }

            return entries;
        }
    }
}
