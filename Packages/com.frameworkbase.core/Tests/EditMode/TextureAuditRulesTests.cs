using System.Collections.Generic;
using Framework.Editor;
using NUnit.Framework;

namespace Framework.Tests
{
    /// <summary>
    /// 纹理审计规则单测：Read/Write 翻倍内存、尺寸超限、未压缩、Sprite 开 Mipmap 四条规则
    /// 与豁免清单语义。规则漏判会让问题纹理带病入包，误判会把门禁变成狼来了。
    /// </summary>
    public class TextureAuditRulesTests
    {
        private static TextureAuditEntry Clean(string path = "Assets/UI/icon.png")
        {
            return new TextureAuditEntry
            {
                AssetPath = path,
                Width = 512,
                Height = 512,
                ReadWriteEnabled = false,
                Uncompressed = false,
                MipmapsEnabled = false,
                IsSprite = true,
            };
        }

        private static List<TextureAuditIssue> Run(params TextureAuditEntry[] entries)
        {
            return TextureAuditRules.Validate(entries, new TextureAuditThresholds());
        }

        [Test]
        public void 干净纹理_零问题()
        {
            Assert.IsEmpty(Run(Clean()));
        }

        [Test]
        public void ReadWrite开启_Error()
        {
            var entry = Clean();
            entry.ReadWriteEnabled = true;

            var issues = Run(entry);
            Assert.AreEqual(1, issues.Count);
            Assert.AreEqual(TextureAuditSeverity.Error, issues[0].Severity);
            Assert.AreEqual("ReadWriteEnabled", issues[0].Rule);
        }

        [Test]
        public void ReadWrite豁免清单_按路径前缀放行()
        {
            var entry = Clean("Assets/Procedural/heightmap.png");
            entry.ReadWriteEnabled = true;

            var thresholds = new TextureAuditThresholds();
            thresholds.ReadWriteAllowlistPrefixes.Add("Assets/Procedural/");

            Assert.IsEmpty(TextureAuditRules.Validate(new[] { entry }, thresholds),
                "豁免前缀命中的 Read/Write 不应报");

            var other = Clean("Assets/UI/big.png");
            other.ReadWriteEnabled = true;
            Assert.AreEqual(1, TextureAuditRules.Validate(new[] { other }, thresholds).Count,
                "前缀不命中的照报");
        }

        [Test]
        public void 尺寸超限_两档判定()
        {
            var warn = Clean();
            warn.Width = 4096; // 恰好在 Error 线上（>4096 才 Error），落 Warning 档
            warn.Height = 128;

            var error = Clean();
            error.Width = 8192;

            var issuesWarn = Run(warn);
            Assert.AreEqual(1, issuesWarn.Count);
            Assert.AreEqual(TextureAuditSeverity.Warning, issuesWarn[0].Severity);
            Assert.AreEqual("LargeTexture", issuesWarn[0].Rule);

            var issuesError = Run(error);
            Assert.AreEqual(TextureAuditSeverity.Error, issuesError[0].Severity);
            Assert.AreEqual("OversizeTexture", issuesError[0].Rule, "超限只报一档，不重复计数");
            Assert.AreEqual(1, issuesError.Count);
        }

        [Test]
        public void 边界值_不误报()
        {
            var entry = Clean();
            entry.Width = 2048;
            entry.Height = 2048;

            Assert.IsEmpty(Run(entry), "2048 恰好在告警线上（>2048 才报）不应误报");
        }

        [Test]
        public void 未压缩_Warning()
        {
            var entry = Clean();
            entry.Uncompressed = true;

            var issues = Run(entry);
            Assert.AreEqual(1, issues.Count);
            Assert.AreEqual(TextureAuditSeverity.Warning, issues[0].Severity);
            Assert.AreEqual("UncompressedTexture", issues[0].Rule);
        }

        [Test]
        public void Sprite开Mipmap_Warning_非Sprite不报()
        {
            var sprite = Clean();
            sprite.MipmapsEnabled = true;

            var issues = Run(sprite);
            Assert.AreEqual(1, issues.Count);
            Assert.AreEqual("SpriteWithMipmaps", issues[0].Rule);

            var model = Clean();
            model.IsSprite = false;
            model.MipmapsEnabled = true;
            Assert.IsEmpty(Run(model), "3D 用途纹理开 Mipmap 是正当的");
        }

        [Test]
        public void 多纹理多问题_全部聚合()
        {
            var a = Clean();
            a.ReadWriteEnabled = true;
            a.Uncompressed = true;

            var b = Clean();
            b.Width = 8192;

            var issues = Run(a, b);
            Assert.AreEqual(3, issues.Count, "同一纹理多问题逐条报，多纹理聚合到一张清单");
        }
    }
}
