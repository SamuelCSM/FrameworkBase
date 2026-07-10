using System;
using System.Collections.Generic;
using Framework.Editor.Release;
using Framework.HotUpdate;
using NUnit.Framework;
using UnityEngine;

namespace Framework.Tests
{
    /// <summary>
    /// 发布清单唯一序列化契约测试，确保安全身份字段、SHA-256 文件描述和运营字段能够被运行时对称读回。
    /// </summary>
    public class ReleaseManifestWriterTests
    {
        private static UpdateInfo ClientParse(string json) => JsonUtility.FromJson<UpdateInfo>(json);

        [Test]
        public void Description特殊字符_往返不破坏JSON()
        {
            UpdateInfo manifest = CreateManifest();
            manifest.Description = "特殊字符测试" + Environment.NewLine + "制表符" + (char)9 + "结束";

            UpdateInfo parsed = ClientParse(ReleaseManifestWriter.ToJson(manifest));

            Assert.IsNotNull(parsed);
            Assert.AreEqual(manifest.Description, parsed.Description);
            Assert.AreEqual(manifest.ManifestId, parsed.ManifestId);
            Assert.AreEqual(manifest.KeyId, parsed.KeyId);
        }

        [Test]
        public void 灰度与整包跳转字段_可完整读回()
        {
            UpdateInfo manifest = CreateManifest();
            manifest.ForceUpdate = true;
            manifest.UpdateUrl = "https://store.example.com/app";
            manifest.GrayPercent = 20;

            UpdateInfo parsed = ClientParse(ReleaseManifestWriter.ToJson(manifest));

            Assert.AreEqual(20, parsed.GrayPercent);
            Assert.AreEqual(manifest.UpdateUrl, parsed.UpdateUrl);
            Assert.IsTrue(parsed.ForceUpdate);
        }

        [Test]
        public void PatchFiles_Size与SHA256完整往返()
        {
            UpdateInfo manifest = CreateManifest();
            manifest.PatchFiles = new List<PatchFile>
            {
                new PatchFile
                {
                    FileName = "GameProtocol.dll.bytes",
                    Url = "https://cdn.example.com/updates/payloads/v2/GameProtocol.dll.bytes",
                    Size = 1024,
                    SHA256 = new string('a', 64),
                },
                new PatchFile
                {
                    FileName = "HotUpdate.dll.bytes",
                    Url = "https://cdn.example.com/updates/payloads/v2/HotUpdate.dll.bytes",
                    Size = 2048,
                    SHA256 = new string('b', 64),
                },
            };

            UpdateInfo parsed = ClientParse(ReleaseManifestWriter.ToJson(manifest));

            Assert.AreEqual(2, parsed.PatchFiles.Count);
            Assert.AreEqual(1024, parsed.PatchFiles[0].Size);
            Assert.AreEqual(new string('a', 64), parsed.PatchFiles[0].SHA256);
            Assert.AreEqual(manifest.PatchFiles[1].Url, parsed.PatchFiles[1].Url);
        }

        [Test]
        public void PatchFiles为null_规范化为空列表()
        {
            UpdateInfo manifest = CreateManifest();
            manifest.PatchFiles = null;

            UpdateInfo parsed = ClientParse(ReleaseManifestWriter.ToJson(manifest));

            Assert.IsNotNull(parsed.PatchFiles);
            Assert.AreEqual(0, parsed.PatchFiles.Count);
        }

        [Test]
        public void 缺少安全身份或有效期_抛出契约异常()
        {
            UpdateInfo missingId = CreateManifest();
            missingId.ManifestId = string.Empty;
            Assert.Throws<ArgumentException>(() => ReleaseManifestWriter.ToJson(missingId));

            UpdateInfo invalidWindow = CreateManifest();
            invalidWindow.ExpiresAtUnixSeconds = invalidWindow.IssuedAtUnixSeconds;
            Assert.Throws<ArgumentException>(() => ReleaseManifestWriter.ToJson(invalidWindow));
        }

        private static UpdateInfo CreateManifest()
        {
            long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            return new UpdateInfo
            {
                ManifestVersion = FrameworkRuntimeInfo.UpdateManifestVersion,
                ManifestId = Guid.NewGuid().ToString("D"),
                IssuedAtUnixSeconds = now,
                ExpiresAtUnixSeconds = now + 3600,
                KeyId = "test-key",
                Platform = "android",
                Channel = "default",
                MinFrameworkVersion = FrameworkRuntimeInfo.Version,
                AppVersion = "1.0.0",
                ResourceVersion = 2,
                CodeVersion = 3,
                PatchFiles = new List<PatchFile>(),
            };
        }
    }
}
