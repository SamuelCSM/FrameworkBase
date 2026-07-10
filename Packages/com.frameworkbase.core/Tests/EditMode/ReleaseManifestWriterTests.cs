using System;
using System.Collections.Generic;
using Framework.Editor.Release;
using Framework.HotUpdate;
using NUnit.Framework;
using UnityEngine;

namespace Framework.Tests
{
    /// <summary>
    /// 发布契约（ReleaseManifestWriter）单元测试：序列化与客户端反序列化（JsonUtility，
    /// 即运行时默认 UnityJsonSerializer 的底层）必须完全对称，字段不漂移、特殊字符不破坏 JSON。
    /// </summary>
    public class ReleaseManifestWriterTests
    {
        /// <summary>模拟客户端侧解析（VersionManager.GetLocalVersion / HotUpdateManager 同底层）。</summary>
        private static UpdateInfo ClientParse(string json) => JsonUtility.FromJson<UpdateInfo>(json);

        [Test]
        public void Description特殊字符_往返不破坏JSON()
        {
            // 曾经的手搓 JSON 生成器（FullPackage 版）不转义 Description，引号/换行会产出非法 JSON。
            var manifest = new UpdateInfo
            {
                AppVersion = "1.0",
                ResourceVersion = 2,
                CodeVersion = 3,
                Description = "修复\"登录\"闪退\n包含反斜杠 C:\\path 与\t制表符",
                PatchFiles = new List<PatchFile>()
            };

            var parsed = ClientParse(ReleaseManifestWriter.ToJson(manifest));

            Assert.IsNotNull(parsed);
            Assert.AreEqual(manifest.Description, parsed.Description);
            Assert.AreEqual(2, parsed.ResourceVersion);
            Assert.AreEqual(3, parsed.CodeVersion);
        }

        [Test]
        public void GrayPercent与UpdateUrl_随清单写出并可读回()
        {
            // 曾经两份手搓生成器都不写这两个字段，导致灰度放量/强更跳转在发布端不可用。
            var manifest = new UpdateInfo
            {
                AppVersion = "1.0",
                ResourceVersion = 5,
                CodeVersion = 4,
                ForceUpdate = true,
                UpdateUrl = "https://store.example.com/app",
                GrayPercent = 20,
                PatchFiles = new List<PatchFile>()
            };

            var parsed = ClientParse(ReleaseManifestWriter.ToJson(manifest));

            Assert.AreEqual(20, parsed.GrayPercent);
            Assert.AreEqual("https://store.example.com/app", parsed.UpdateUrl);
            Assert.IsTrue(parsed.ForceUpdate);
        }

        [Test]
        public void PatchFiles_内容完整往返()
        {
            var manifest = new UpdateInfo
            {
                AppVersion = "1.0",
                CodeVersion = 2,
                PatchFiles = new List<PatchFile>
                {
                    new PatchFile { FileName = "GameProtocol.dll.bytes", Url = "GameProtocol.dll.bytes", Size = 1024, MD5 = "aa11" },
                    new PatchFile { FileName = "HotUpdate.dll.bytes",    Url = "HotUpdate.dll.bytes",    Size = 2048, MD5 = "bb22" }
                }
            };

            var parsed = ClientParse(ReleaseManifestWriter.ToJson(manifest));

            Assert.AreEqual(2, parsed.PatchFiles.Count);
            Assert.AreEqual("GameProtocol.dll.bytes", parsed.PatchFiles[0].FileName);
            Assert.AreEqual(1024, parsed.PatchFiles[0].Size);
            Assert.AreEqual("aa11", parsed.PatchFiles[0].MD5);
            Assert.AreEqual("HotUpdate.dll.bytes", parsed.PatchFiles[1].Url);
        }

        [Test]
        public void PatchFiles为null_规范化为空列表()
        {
            var manifest = new UpdateInfo { AppVersion = "1.0" };

            var parsed = ClientParse(ReleaseManifestWriter.ToJson(manifest));

            Assert.IsNotNull(parsed.PatchFiles);
            Assert.AreEqual(0, parsed.PatchFiles.Count);
        }

        [Test]
        public void 缺AppVersion_抛出契约异常()
        {
            Assert.Throws<ArgumentException>(() =>
                ReleaseManifestWriter.ToJson(new UpdateInfo { AppVersion = "" }));
            Assert.Throws<ArgumentNullException>(() =>
                ReleaseManifestWriter.ToJson(null));
        }
    }
}
