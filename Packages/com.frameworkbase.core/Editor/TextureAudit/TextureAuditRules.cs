using System.Collections.Generic;

namespace Framework.Editor
{
    public enum TextureAuditSeverity
    {
        Warning,
        Error,
    }

    /// <summary>单条纹理审计问题。</summary>
    public sealed class TextureAuditIssue
    {
        public TextureAuditSeverity Severity;
        public string Rule;
        public string Message;

        public override string ToString() => $"[{Severity}] {Rule}: {Message}";
    }

    /// <summary>
    /// 单张纹理的审计画像（纯数据，可单测）。由 <see cref="TextureAuditCollector"/> 从导入器采集。
    /// </summary>
    public sealed class TextureAuditEntry
    {
        public string AssetPath;

        /// <summary>导入后（实际入包）的尺寸——内存与包体成本以此为准，非源文件尺寸。</summary>
        public int Width;
        public int Height;

        public bool ReadWriteEnabled;
        public bool Uncompressed;
        public bool MipmapsEnabled;
        public bool IsSprite;
    }

    /// <summary>审计阈值与豁免清单。默认值面向移动端；项目按需调整后传入。</summary>
    public sealed class TextureAuditThresholds
    {
        /// <summary>单边超过该值判 Error（4096 在低端机上单张纹理就吃掉 64MB 级显存）。</summary>
        public int ErrorDimension = 4096;

        /// <summary>单边超过该值判 Warning（图集之外的散图一般不该到 2048）。</summary>
        public int WarnDimension = 2048;

        /// <summary>
        /// Read/Write 豁免路径前缀（正斜杠路径）。极少数需要 CPU 采样的纹理（如程序化 mesh 贴图）
        /// 在此登记，豁免是显式的、进代码评审的。
        /// </summary>
        public List<string> ReadWriteAllowlistPrefixes = new List<string>();
    }

    /// <summary>
    /// 纹理审计规则（纯逻辑，可单测）。与 Addressables 校验器同款分层：
    /// 采集（Unity API）与规则（纯逻辑）分离，规则可离线跑单测。
    ///
    /// 规则依据（大厂资源门禁的常规四项）：
    /// R1 Read/Write Enabled —— CPU 侧常驻一份拷贝，该纹理内存直接翻倍，Error；
    /// R2 尺寸超限 —— 单边 &gt;4096 Error / &gt;2048 Warning，超大图属集成事故要当场拦；
    /// R3 未压缩 —— RGBA32 比 ASTC/ETC2 大 4~8 倍，Warning（个别 UI 精度需求属例外，故不阻断）；
    /// R4 Sprite 开 Mipmap —— UI 不走 3D 透视用不到 mip 链，白吃 1/3 显存，Warning。
    /// </summary>
    public static class TextureAuditRules
    {
        public static List<TextureAuditIssue> Validate(
            IReadOnlyList<TextureAuditEntry> entries,
            TextureAuditThresholds thresholds)
        {
            var issues = new List<TextureAuditIssue>();

            foreach (TextureAuditEntry entry in entries)
            {
                int maxSide = entry.Width > entry.Height ? entry.Width : entry.Height;

                if (entry.ReadWriteEnabled && !IsAllowlisted(entry.AssetPath, thresholds.ReadWriteAllowlistPrefixes))
                {
                    Add(issues, TextureAuditSeverity.Error, "ReadWriteEnabled",
                        $"{entry.AssetPath} 开启了 Read/Write——CPU 侧常驻拷贝使该纹理内存翻倍。" +
                        "确需 CPU 采样的在审计豁免清单登记。");
                }

                if (maxSide > thresholds.ErrorDimension)
                {
                    Add(issues, TextureAuditSeverity.Error, "OversizeTexture",
                        $"{entry.AssetPath} 导入尺寸 {entry.Width}x{entry.Height} 超过 {thresholds.ErrorDimension} 上限。");
                }
                else if (maxSide > thresholds.WarnDimension)
                {
                    Add(issues, TextureAuditSeverity.Warning, "LargeTexture",
                        $"{entry.AssetPath} 导入尺寸 {entry.Width}x{entry.Height} 超过 {thresholds.WarnDimension}，确认是否必要。");
                }

                if (entry.Uncompressed)
                {
                    Add(issues, TextureAuditSeverity.Warning, "UncompressedTexture",
                        $"{entry.AssetPath} 未压缩（RGBA32 级），体积与显存是压缩格式的 4~8 倍。");
                }

                if (entry.IsSprite && entry.MipmapsEnabled)
                {
                    Add(issues, TextureAuditSeverity.Warning, "SpriteWithMipmaps",
                        $"{entry.AssetPath} 是 Sprite 且开启 Mipmap——UI 用不到 mip 链，白吃 1/3 显存。");
                }
            }

            return issues;
        }

        private static bool IsAllowlisted(string assetPath, List<string> prefixes)
        {
            if (prefixes == null)
                return false;

            foreach (string prefix in prefixes)
            {
                if (!string.IsNullOrEmpty(prefix) && assetPath.StartsWith(prefix))
                    return true;
            }

            return false;
        }

        private static void Add(
            List<TextureAuditIssue> issues, TextureAuditSeverity severity, string rule, string message)
        {
            issues.Add(new TextureAuditIssue { Severity = severity, Rule = rule, Message = message });
        }
    }
}
