using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;
using Framework.Core;
using Framework.HotUpdate;
using NUnit.Framework;
using UnityEngine;

namespace Framework.Tests
{
    /// <summary>
    /// 热更新供应链安全回归测试，覆盖传输策略、原始字节签名、密钥轮换、清单时效、防降级和完整代码快照。
    /// 这些规则一旦回退会直接扩大远程代码执行入口，因此必须作为 EditMode 必跑门禁长期锁定。
    /// </summary>
    public class UpdateSecurityTests
    {
        private const string ValidSha256 = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";

        // ── 更新服务 URL 准入 ────────────────────────────────────────────────

        [Test]
        public void 生产环境_明文HTTP_拒绝()
        {
            Assert.IsFalse(UpdateSecurity.ValidateUpdateServerUrl(
                "http://cdn.example.com/Updates", "prod", out string reason));
            Assert.IsNotNull(reason);
        }

        [Test]
        public void 生产环境_HTTPS_放行()
        {
            Assert.IsTrue(UpdateSecurity.ValidateUpdateServerUrl(
                "https://cdn.example.com/Updates", "prod", out string reason));
            Assert.IsNull(reason);
        }

        [Test]
        public void 开发环境_允许本地HTTP()
        {
            Assert.IsTrue(UpdateSecurity.ValidateUpdateServerUrl(
                "http://127.0.0.1:8080/Updates", "dev", out _));
        }

        [Test]
        public void 非HTTP协议与相对地址_拒绝()
        {
            Assert.IsFalse(UpdateSecurity.ValidateUpdateServerUrl("not-a-url", "dev", out _));
            Assert.IsFalse(UpdateSecurity.ValidateUpdateServerUrl("ftp://cdn.example.com/Updates", "dev", out _));
        }

        // ── 补丁文件与完整快照准入 ──────────────────────────────────────────

        [Test]
        public void 补丁缺SHA256或长度_拒绝()
        {
            var missingHash = new PatchFile
            {
                FileName = "HotUpdate.dll.bytes",
                Url = "https://cdn.example.com/HotUpdate.dll.bytes",
                Size = 1,
            };
            Assert.IsFalse(UpdateSecurity.ValidateCodePatchFile(missingHash, out _));

            var missingSize = CreatePatch("HotUpdate.dll.bytes");
            missingSize.Size = 0;
            Assert.IsFalse(UpdateSecurity.ValidateCodePatchFile(missingSize, out _));
        }

        [Test]
        public void 补丁目录穿越与非白名单程序集_拒绝()
        {
            PatchFile traversal = CreatePatch("../HotUpdate.dll.bytes");
            Assert.IsFalse(UpdateSecurity.ValidateCodePatchFile(traversal, out _));

            PatchFile unknown = CreatePatch("Unknown.dll.bytes");
            Assert.IsFalse(UpdateSecurity.ValidateCodePatchFile(unknown, out _));
        }

        [Test]
        public void 生产环境补丁URL非HTTPS_拒绝()
        {
            PatchFile patch = CreatePatch("HotUpdate.dll.bytes");
            patch.Url = "http://cdn.example.com/HotUpdate.dll.bytes";
            Assert.IsFalse(UpdateSecurity.ValidateCodePatchFile(patch, "prod", out _));
        }

        [Test]
        public void 完整代码快照_无缺失无重复_放行()
        {
            List<PatchFile> patches = CreateCompletePatchSet();
            Assert.IsTrue(UpdateSecurity.ValidateCompleteCodePatchSet(patches, "prod", out string reason), reason);
        }

        [Test]
        public void 代码快照缺文件或重复文件_拒绝()
        {
            List<PatchFile> missing = CreateCompletePatchSet();
            missing.RemoveAt(0);
            Assert.IsFalse(UpdateSecurity.ValidateCompleteCodePatchSet(missing, "prod", out _));

            List<PatchFile> duplicated = CreateCompletePatchSet();
            duplicated.Add(CreatePatch(duplicated[0].FileName));
            Assert.IsFalse(UpdateSecurity.ValidateCompleteCodePatchSet(duplicated, "prod", out _));
        }

        // ── 清单字段级安全准入 ──────────────────────────────────────────────

        [Test]
        public void 合法同整包代码升级清单_放行()
        {
            UpdateInfo local = CreateLocalVersion();
            UpdateInfo server = CreateValidManifest(local, codeVersion: local.CodeVersion + 1);
            Assert.IsTrue(UpdateSecurity.ValidateManifest(
                server, local, "prod", "default", out string reason), reason);
        }

        [Test]
        public void 清单协议版本过高或过低_均拒绝()
        {
            UpdateInfo local = CreateLocalVersion();
            UpdateInfo server = CreateValidManifest(local, local.CodeVersion);

            server.ManifestVersion = FrameworkRuntimeInfo.UpdateManifestVersion + 1;
            Assert.IsFalse(UpdateSecurity.ValidateManifest(server, local, "prod", "default", out _));

            server.ManifestVersion = FrameworkRuntimeInfo.UpdateManifestVersion - 1;
            Assert.IsFalse(UpdateSecurity.ValidateManifest(server, local, "prod", "default", out _));
        }

        [Test]
        public void 清单版本格式非法_失败关闭()
        {
            UpdateInfo local = CreateLocalVersion();
            UpdateInfo server = CreateValidManifest(local, local.CodeVersion);
            server.AppVersion = "1.bad.0";
            Assert.IsFalse(UpdateSecurity.ValidateManifest(server, local, "prod", "default", out _));
        }

        [Test]
        public void 清单过期或失效时间早于签发时间_拒绝()
        {
            long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            UpdateInfo local = CreateLocalVersion();
            UpdateInfo server = CreateValidManifest(local, local.CodeVersion);

            server.IssuedAtUnixSeconds = now - 100;
            server.ExpiresAtUnixSeconds = now - 1;
            Assert.IsFalse(UpdateSecurity.ValidateManifest(server, local, "prod", "default", out _, now));

            server.IssuedAtUnixSeconds = now + 10;
            server.ExpiresAtUnixSeconds = now + 5;
            Assert.IsFalse(UpdateSecurity.ValidateManifest(server, local, "prod", "default", out _, now));
        }

        [Test]
        public void 同整包资源或代码降级_拒绝()
        {
            UpdateInfo local = CreateLocalVersion();
            local.ResourceVersion = 5;
            local.CodeVersion = 5;
            UpdateInfo server = CreateValidManifest(local, codeVersion: 4);
            server.ResourceVersion = 4;
            Assert.IsFalse(UpdateSecurity.ValidateManifest(server, local, "prod", "default", out _));
        }

        // ── 公钥环与签名 ────────────────────────────────────────────────────

        [Test]
        public void 签名后验签通过_篡改后失败()
        {
            CreateKeyPair(out string privateKeyXml, out string publicKeyXml);
            byte[] manifest = Encoding.UTF8.GetBytes("manifest-version-2");
            string signature = UpdateSecurity.SignManifest(manifest, privateKeyXml);

            Assert.IsTrue(UpdateSecurity.VerifyManifestSignature(manifest, signature, publicKeyXml));
            Assert.IsFalse(UpdateSecurity.VerifyManifestSignature(
                Encoding.UTF8.GetBytes("manifest-version-3"),
                signature,
                publicKeyXml));
        }

        [Test]
        public void 公钥环存在时_KeyId未命中不得回退旧公钥()
        {
            CreateKeyPair(out _, out string legacyPublicKey);
            CreateKeyPair(out _, out string ringPublicKey);
            var ring = new[]
            {
                new UpdateManifestPublicKeyEntry { KeyId = "prod-key-1", PublicKeyXml = ringPublicKey },
            };

            Assert.IsNull(UpdateSecurity.ResolvePublicKey("unknown", legacyPublicKey, ring));
            Assert.AreEqual(ringPublicKey, UpdateSecurity.ResolvePublicKey("prod-key-1", legacyPublicKey, ring));
        }

        [Test]
        public void 公钥环重复KeyId或包含私钥_拒绝()
        {
            CreateKeyPair(out string privateKey, out string publicKey);
            var duplicate = new[]
            {
                new UpdateManifestPublicKeyEntry { KeyId = "key-1", PublicKeyXml = publicKey },
                new UpdateManifestPublicKeyEntry { KeyId = "key-1", PublicKeyXml = publicKey },
            };
            Assert.IsFalse(UpdateSecurity.ValidatePublicKeyConfiguration(null, duplicate, out _));

            var leakedPrivateKey = new[]
            {
                new UpdateManifestPublicKeyEntry { KeyId = "key-2", PublicKeyXml = privateKey },
            };
            Assert.IsFalse(UpdateSecurity.ValidatePublicKeyConfiguration(null, leakedPrivateKey, out _));
        }

        /// <summary>
        /// 创建与当前 Editor 平台、渠道和整包版本一致的合法清单。
        /// </summary>
        private static UpdateInfo CreateValidManifest(UpdateInfo local, int codeVersion)
        {
            long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            return new UpdateInfo
            {
                ManifestVersion = FrameworkRuntimeInfo.UpdateManifestVersion,
                ManifestId = Guid.NewGuid().ToString("D"),
                KeyId = "test-key",
                IssuedAtUnixSeconds = now - 1,
                ExpiresAtUnixSeconds = now + 3600,
                Platform = UpdateSecurity.GetRuntimePlatformId(),
                Channel = "default",
                MinFrameworkVersion = FrameworkRuntimeInfo.Version,
                AppVersion = Application.version,
                ResourceVersion = local.ResourceVersion,
                CodeVersion = codeVersion,
                GrayPercent = 100,
                PatchFiles = codeVersion > local.CodeVersion
                    ? CreateCompletePatchSet()
                    : new List<PatchFile>(),
            };
        }

        /// <summary>
        /// 创建当前整包的本地版本基线。
        /// </summary>
        private static UpdateInfo CreateLocalVersion() => new UpdateInfo
        {
            AppVersion = Application.version,
            ResourceVersion = 1,
            CodeVersion = 1,
        };

        /// <summary>
        /// 按客户端实际程序集白名单创建完整测试快照，避免测试写死项目扩展程序集数量。
        /// </summary>
        private static List<PatchFile> CreateCompletePatchSet()
        {
            var patches = new List<PatchFile>();
            foreach (string fileName in VersionManager.HotUpdateAssemblyFileNames)
                patches.Add(CreatePatch(fileName));
            return patches;
        }

        /// <summary>
        /// 创建字段完整的测试补丁描述。
        /// </summary>
        private static PatchFile CreatePatch(string fileName) => new PatchFile
        {
            FileName = fileName,
            Url = $"https://cdn.example.com/Updates/{fileName}",
            Size = 1024,
            SHA256 = ValidSha256,
        };

        /// <summary>
        /// 生成仅供测试使用的 RSA-2048 XML 密钥对。
        /// </summary>
        private static void CreateKeyPair(out string privateKeyXml, out string publicKeyXml)
        {
            using (RSA rsa = RSA.Create())
            {
                rsa.KeySize = 2048;
                privateKeyXml = rsa.ToXmlString(true);
                publicKeyXml = rsa.ToXmlString(false);
            }
        }
    }
}
