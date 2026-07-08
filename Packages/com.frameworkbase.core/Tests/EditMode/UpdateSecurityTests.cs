using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;
using Framework.HotUpdate;
using NUnit.Framework;

namespace Framework.Tests
{
    /// <summary>
    /// UpdateSecurity 热更安全策略单元测试：URL 准入、补丁清单准入、清单签名往返。
    /// </summary>
    public class UpdateSecurityTests
    {
        // ── URL 准入 ─────────────────────────────────────────────────────────

        [Test]
        public void 生产环境_明文HTTP_拒绝()
        {
            bool ok = UpdateSecurity.ValidateUpdateServerUrl("http://cdn.example.com/Updates", "prod", out string reason);
            Assert.IsFalse(ok);
            Assert.IsNotNull(reason);
        }

        [Test]
        public void 生产环境_HTTPS_放行()
        {
            bool ok = UpdateSecurity.ValidateUpdateServerUrl("https://cdn.example.com/Updates", "prod", out string reason);
            Assert.IsTrue(ok);
            Assert.IsNull(reason);
        }

        [Test]
        public void 开发环境_明文HTTP_放行()
        {
            Assert.IsTrue(UpdateSecurity.ValidateUpdateServerUrl("http://127.0.0.1:80/Updates", "dev", out _));
            Assert.IsTrue(UpdateSecurity.ValidateUpdateServerUrl("http://127.0.0.1:80/Updates", "staging", out _));
        }

        [Test]
        public void 空URL_视为跳过热更_放行()
        {
            Assert.IsTrue(UpdateSecurity.ValidateUpdateServerUrl(null, "prod", out _));
            Assert.IsTrue(UpdateSecurity.ValidateUpdateServerUrl(string.Empty, "prod", out _));
        }

        [Test]
        public void 非法URL与非HTTP协议_拒绝()
        {
            Assert.IsFalse(UpdateSecurity.ValidateUpdateServerUrl("not a url", "dev", out _));
            Assert.IsFalse(UpdateSecurity.ValidateUpdateServerUrl("ftp://cdn.example.com/Updates", "dev", out _));
        }

        [Test]
        public void 生产环境判定_大小写与空白不敏感()
        {
            Assert.IsTrue(UpdateSecurity.IsProductionEnv("prod"));
            Assert.IsTrue(UpdateSecurity.IsProductionEnv(" PROD "));
            Assert.IsFalse(UpdateSecurity.IsProductionEnv("dev"));
            Assert.IsFalse(UpdateSecurity.IsProductionEnv(null));
        }

        // ── 补丁清单准入 ─────────────────────────────────────────────────────

        [Test]
        public void 补丁缺MD5_拒绝()
        {
            var patch = new PatchFile { FileName = "HotUpdate.dll.bytes", Url = "https://cdn/x", Size = 1, MD5 = "" };
            Assert.IsFalse(UpdateSecurity.ValidateCodePatchFile(patch, out string reason));
            Assert.IsNotNull(reason);
        }

        [Test]
        public void 补丁缺URL或文件名_拒绝()
        {
            Assert.IsFalse(UpdateSecurity.ValidateCodePatchFile(
                new PatchFile { FileName = "", Url = "https://cdn/x", MD5 = "abc" }, out _));
            Assert.IsFalse(UpdateSecurity.ValidateCodePatchFile(
                new PatchFile { FileName = "a.dll.bytes", Url = "", MD5 = "abc" }, out _));
            Assert.IsFalse(UpdateSecurity.ValidateCodePatchFile(null, out _));
        }

        [Test]
        public void 补丁字段齐全_放行()
        {
            var patch = new PatchFile
            {
                FileName = "HotUpdate.dll.bytes",
                Url = "https://cdn.example.com/Updates/HotUpdate.dll.bytes",
                Size = 1024,
                MD5 = "d41d8cd98f00b204e9800998ecf8427e"
            };
            Assert.IsTrue(UpdateSecurity.ValidateCodePatchFile(patch, out string reason));
            Assert.IsNull(reason);
        }

        // ── 清单签名往返 ─────────────────────────────────────────────────────

        [Test]
        public void 签名后验签_通过()
        {
            CreateKeyPair(out string privateKeyXml, out string publicKeyXml);
            byte[] manifest = Encoding.UTF8.GetBytes("{\"AppVersion\":\"1.0\",\"CodeVersion\":2}");

            string signature = UpdateSecurity.SignManifest(manifest, privateKeyXml);

            Assert.IsTrue(UpdateSecurity.VerifyManifestSignature(manifest, signature, publicKeyXml));
        }

        [Test]
        public void 清单被篡改_验签失败()
        {
            CreateKeyPair(out string privateKeyXml, out string publicKeyXml);
            byte[] manifest = Encoding.UTF8.GetBytes("{\"AppVersion\":\"1.0\",\"CodeVersion\":2}");
            string signature = UpdateSecurity.SignManifest(manifest, privateKeyXml);

            byte[] tampered = Encoding.UTF8.GetBytes("{\"AppVersion\":\"1.0\",\"CodeVersion\":99}");

            Assert.IsFalse(UpdateSecurity.VerifyManifestSignature(tampered, signature, publicKeyXml));
        }

        [Test]
        public void 用错误公钥_验签失败()
        {
            CreateKeyPair(out string privateKeyXml, out _);
            CreateKeyPair(out _, out string otherPublicKeyXml);
            byte[] manifest = Encoding.UTF8.GetBytes("{\"AppVersion\":\"1.0\"}");
            string signature = UpdateSecurity.SignManifest(manifest, privateKeyXml);

            Assert.IsFalse(UpdateSecurity.VerifyManifestSignature(manifest, signature, otherPublicKeyXml));
        }

        [Test]
        public void 签名或公钥为空_验签失败不抛出()
        {
            byte[] manifest = Encoding.UTF8.GetBytes("{}");
            Assert.IsFalse(UpdateSecurity.VerifyManifestSignature(manifest, null, "<RSAKeyValue/>"));
            Assert.IsFalse(UpdateSecurity.VerifyManifestSignature(manifest, "AAAA", null));
            Assert.IsFalse(UpdateSecurity.VerifyManifestSignature(null, "AAAA", "<RSAKeyValue/>"));
            UnityEngine.TestTools.LogAssert.Expect(
                UnityEngine.LogType.Error,
                new System.Text.RegularExpressions.Regex(@"\[UpdateSecurity\] 清单签名校验异常"));
            Assert.IsFalse(UpdateSecurity.VerifyManifestSignature(manifest, "不是Base64!!", "<RSAKeyValue/>"));
        }

        // ── VersionManager 补丁解析（安全收敛后行为）─────────────────────────

        [Test]
        public void 清单无PatchFiles_拒绝代码热更()
        {
            var server = new UpdateInfo { CodeVersion = 2, PatchFiles = new List<PatchFile>() };

            UnityEngine.TestTools.LogAssert.Expect(
                UnityEngine.LogType.Error,
                new System.Text.RegularExpressions.Regex(@"\[VersionManager\] CodeVersion 已变更但服务端清单未提供 PatchFiles"));
            bool ok = VersionManager.TryResolveCodePatchFiles(server, "https://cdn.example.com/Updates", out var patches);

            Assert.IsFalse(ok);
            Assert.IsNull(patches);
        }

        [Test]
        public void 清单补丁缺MD5_拒绝代码热更()
        {
            var server = new UpdateInfo
            {
                CodeVersion = 2,
                PatchFiles = new List<PatchFile>
                {
                    new PatchFile { FileName = "HotUpdate.dll.bytes", Url = "https://cdn/x", Size = 1, MD5 = "" }
                }
            };

            UnityEngine.TestTools.LogAssert.Expect(
                UnityEngine.LogType.Error,
                new System.Text.RegularExpressions.Regex(@"\[VersionManager\] 补丁清单未通过安全准入"));
            Assert.IsFalse(VersionManager.TryResolveCodePatchFiles(server, "https://cdn.example.com/Updates", out _));
        }

        [Test]
        public void 清单补丁带全量哈希_放行()
        {
            var server = new UpdateInfo
            {
                CodeVersion = 2,
                PatchFiles = new List<PatchFile>
                {
                    new PatchFile { FileName = "GameProtocol.dll.bytes", Url = "https://cdn/a", Size = 10, MD5 = "aa" },
                    new PatchFile { FileName = "HotUpdate.dll.bytes",    Url = "https://cdn/b", Size = 20, MD5 = "bb" }
                }
            };

            bool ok = VersionManager.TryResolveCodePatchFiles(server, "https://cdn.example.com/Updates", out var patches);

            Assert.IsTrue(ok);
            Assert.AreEqual(2, patches.Count);
        }

        // ── 工具 ─────────────────────────────────────────────────────────────

        /// <summary>生成测试用 RSA 密钥对（XML 格式，与运行时/发布工具一致）。</summary>
        private static void CreateKeyPair(out string privateKeyXml, out string publicKeyXml)
        {
            using (var rsa = RSA.Create())
            {
                rsa.KeySize = 2048;
                privateKeyXml = rsa.ToXmlString(true);
                publicKeyXml = rsa.ToXmlString(false);
            }
        }
    }
}
