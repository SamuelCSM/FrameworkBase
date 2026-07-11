using System;
using System.IO;
using System.Text.RegularExpressions;
using Framework.Core;
using Framework.HotUpdate;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace Framework.Tests
{
    /// <summary>
    /// 发布端 → 客户端 端到端演练集成测试。
    /// <para>
    /// 消费 Tools/ci/release-rehearsal.ps1 经真实 ReleaseBatchEntry 发布到本地 uploadRoot 的产物，
    /// 验证发布端与客户端安全契约的接缝：原始字节验签 → KeyId 信封 → 字段级准入
    /// （平台/渠道映射、时效、版本边界）→ 逐文件完整性 → 事务槽安装与确认，以及三个故障注入路径。
    /// 单元测试各自为营时验证不了这些跨端契约（例如发布端 BuildTarget 与客户端 RuntimePlatform 两套
    /// 枚举的平台标识映射），这组测试就是补这条缝的。
    /// </para>
    /// <para>
    /// 未找到演练产物（Artifacts/Rehearsal/rehearsal.json）时整组跳过，不影响常规 CI。
    /// </para>
    /// </summary>
    public class ReleaseRehearsalTests
    {
        [Serializable]
        private sealed class RehearsalConfig
        {
            public string PublicKeyXml;
            public string KeyId;
            public string UploadRoot;
            public string BaseUrl;
            public string ChannelRelative;
            public string AppVersion;
            public int ResourceVersion;
            public int CodeVersion;
            public string ExpectedActiveReleaseId;
            public string ExpectedPreviousReleaseId;
            public int RolledBackCodeVersion;
            public string PromotedChannelRelative;
            public string PromotedKeyId;
            public string ExpectedPromotedReleaseId;
        }

        /// <summary>与 HotUpdateManager 未验签信封解析保持同构的最小结构。</summary>
        [Serializable]
        private sealed class ManifestKeyEnvelope
        {
            public string KeyId = string.Empty;
        }

        private RehearsalConfig _config;
        private byte[] _manifestBytes;
        private string _signatureBase64;
        private UpdateInfo _server;
        private string _slotRoot;

        [SetUp]
        public void SetUp()
        {
            string projectRoot = Directory.GetParent(Application.dataPath).FullName;
            string configPath = Path.Combine(projectRoot, "Artifacts", "Rehearsal", "rehearsal.json");
            if (!File.Exists(configPath))
                Assert.Ignore("未找到发布演练产物（先执行 Tools/ci/release-rehearsal.ps1），跳过端到端演练。");

            _config = JsonUtility.FromJson<RehearsalConfig>(File.ReadAllText(configPath));
            Assert.IsNotNull(_config, "rehearsal.json 解析失败。");

            // 切片 A 布局：清单别名位于渠道根 {uploadRoot}/{env}/{platform}/{channel}/version.json。
            string channelRoot = Path.Combine(
                _config.UploadRoot,
                _config.ChannelRelative.Replace('/', Path.DirectorySeparatorChar));
            string manifestPath = Path.Combine(channelRoot, "version.json");
            Assert.IsTrue(File.Exists(manifestPath), $"渠道根缺少已发布清单：{manifestPath}");
            _manifestBytes = File.ReadAllBytes(manifestPath);
            _signatureBase64 = File.ReadAllText(manifestPath + UpdateSecurity.ManifestSignatureSuffix);

            // 契约：清单必须是无 BOM 的 UTF-8。验签对象是原始字节，任何一端引入 BOM 都会
            // 造成"签名有效但解析器拒收"或跨端解析歧义，必须在发布端根除。
            Assert.IsFalse(
                _manifestBytes.Length >= 3 &&
                _manifestBytes[0] == 0xEF && _manifestBytes[1] == 0xBB && _manifestBytes[2] == 0xBF,
                "发布端写出的 version.json 含 UTF-8 BOM，违反无 BOM 契约。");

            _server = JsonUtility.FromJson<UpdateInfo>(System.Text.Encoding.UTF8.GetString(_manifestBytes));
            Assert.IsNotNull(_server, "已发布清单无法按客户端方式反序列化。");

            _slotRoot = Path.Combine(Path.GetTempPath(), "FrameworkBase-Rehearsal", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_slotRoot);
            HotUpdateSlotManager.TestRootDirectoryOverride = _slotRoot;
            HotUpdateSlotManager.ResetStateForTests();
            HotUpdateSlotManager.PrepareForLaunch();
        }

        [TearDown]
        public void TearDown()
        {
            HotUpdateSlotManager.ResetStateForTests();
            HotUpdateSlotManager.TestRootDirectoryOverride = null;
            if (!string.IsNullOrEmpty(_slotRoot) && Directory.Exists(_slotRoot))
                Directory.Delete(_slotRoot, true);
        }

        [Test]
        public void 发布产物_原始字节验签通过且KeyId信封一致()
        {
            Assert.IsTrue(
                UpdateSecurity.VerifyManifestSignature(_manifestBytes, _signatureBase64, _config.PublicKeyXml),
                "真实发布产物的 version.json.sig 验签失败。");

            var envelope = JsonUtility.FromJson<ManifestKeyEnvelope>(
                System.Text.Encoding.UTF8.GetString(_manifestBytes));
            Assert.AreEqual(_config.KeyId, envelope.KeyId,
                "清单 KeyId 与发布环境 SigningKeyRef 不一致，客户端将无法从公钥环选中正确密钥。");
            Assert.AreEqual(
                _config.PublicKeyXml,
                UpdateSecurity.ResolvePublicKey(
                    envelope.KeyId,
                    legacyPublicKey: null,
                    keyRing: new[]
                    {
                        new UpdateManifestPublicKeyEntry { KeyId = _config.KeyId, PublicKeyXml = _config.PublicKeyXml },
                    }),
                "按信封 KeyId 从公钥环解析不到发布密钥对应的公钥。");
        }

        [Test]
        public void 发布清单_通过客户端字段级准入_平台与渠道映射一致()
        {
            // 接缝主张：发布端 BuildTarget→Platform 与客户端 RuntimePlatform→Platform 两套映射必须收敛到同一标识。
            Assert.AreEqual(UpdateSecurity.GetRuntimePlatformId(), _server.Platform,
                "发布端写入的 Platform 与客户端运行时平台标识不一致，热更将被平台准入拒绝。");

            AppConfigAsset appConfig = AppConfig.Load();
            UpdateInfo local = NewFactoryBaseline();
            Assert.IsTrue(
                UpdateSecurity.ValidateManifest(_server, local, appConfig?.AppEnv, appConfig?.AppChannel, out string reason),
                $"真实发布清单未通过客户端安全准入：{reason}");
        }

        [Test]
        public void 指针_验签准入通过_回滚后指向前一Release且历史链与正本完整()
        {
            string channelRoot = Path.Combine(
                _config.UploadRoot,
                _config.ChannelRelative.Replace('/', Path.DirectorySeparatorChar));
            byte[] pointerBytes = File.ReadAllBytes(Path.Combine(channelRoot, "current.json"));
            string pointerSig = File.ReadAllText(Path.Combine(channelRoot, "current.json.sig"));

            // 指针与清单同一契约：无 BOM + 原始字节验签 + KeyId 公钥环。
            Assert.IsFalse(
                pointerBytes.Length >= 3 &&
                pointerBytes[0] == 0xEF && pointerBytes[1] == 0xBB && pointerBytes[2] == 0xBF,
                "current.json 含 UTF-8 BOM，违反无 BOM 契约。");
            Assert.IsTrue(
                UpdateSecurity.VerifyManifestSignature(pointerBytes, pointerSig, _config.PublicKeyXml),
                "current.json 指针验签失败。");

            var pointer = JsonUtility.FromJson<CurrentPointer>(
                System.Text.Encoding.UTF8.GetString(pointerBytes));
            Assert.IsTrue(
                UpdateSecurity.ValidateCurrentPointer(pointer, AppConfig.Load()?.AppChannel, out string reason),
                $"指针未通过客户端准入：{reason}");

            // 回滚语义：指针回到 code=2 的 release，历史链 PreviousReleaseId 指向被回滚者。
            Assert.AreEqual(_config.ExpectedActiveReleaseId, pointer.ReleaseId, "回滚后指针未指向目标 release。");
            Assert.AreEqual(_config.ExpectedPreviousReleaseId, pointer.PreviousReleaseId, "指针历史链断裂。");

            // 指针指向的正本清单与渠道根别名字节一致（别名同步保护旧客户端）。
            byte[] canonical = File.ReadAllBytes(Path.Combine(
                channelRoot, pointer.ManifestPath.Replace('/', Path.DirectorySeparatorChar)));
            CollectionAssert.AreEqual(canonical, _manifestBytes, "正本清单与渠道根别名不一致。");

            // 被回滚 release 的不可变正本必须原样保留（回滚只动指针，不动产物）。
            string rolledBackManifest = Path.Combine(
                channelRoot, "releases",
                HotUpdateReleaseStepsCompatibleSegment(_config.AppVersion),
                _config.ExpectedPreviousReleaseId, "version.json");
            Assert.IsTrue(File.Exists(rolledBackManifest), "被回滚 release 的正本清单被移除。");
            var rolledBack = JsonUtility.FromJson<UpdateInfo>(File.ReadAllText(rolledBackManifest));
            Assert.AreEqual(_config.RolledBackCodeVersion, rolledBack.CodeVersion, "被回滚正本内容被改动。");
        }

        [Test]
        public void 晋级_同产物重签_目标环境指针与清单契约成立()
        {
            string qaRoot = Path.Combine(
                _config.UploadRoot,
                _config.PromotedChannelRelative.Replace('/', Path.DirectorySeparatorChar));

            // 晋级指针：同一公钥环验签，KeyId 换为目标环境签名引用，指向晋级 releaseId。
            byte[] pointerBytes = File.ReadAllBytes(Path.Combine(qaRoot, "current.json"));
            Assert.IsTrue(
                UpdateSecurity.VerifyManifestSignature(
                    pointerBytes,
                    File.ReadAllText(Path.Combine(qaRoot, "current.json.sig")),
                    _config.PublicKeyXml),
                "晋级后 qa 指针验签失败。");
            var pointer = JsonUtility.FromJson<CurrentPointer>(System.Text.Encoding.UTF8.GetString(pointerBytes));
            Assert.AreEqual(_config.ExpectedPromotedReleaseId, pointer.ReleaseId, "qa 指针未指向晋级 release。");
            Assert.AreEqual(_config.PromotedKeyId, pointer.KeyId, "qa 指针 KeyId 未切换为目标环境签名引用。");

            // 晋级正本清单：重签有效、KeyId 换环境、补丁 URL 已重根到 qa 渠道根、payload 哈希与源一致（同产物）。
            string manifestPath = Path.Combine(qaRoot, pointer.ManifestPath.Replace('/', Path.DirectorySeparatorChar));
            byte[] manifestBytes = File.ReadAllBytes(manifestPath);
            Assert.IsTrue(
                UpdateSecurity.VerifyManifestSignature(
                    manifestBytes,
                    File.ReadAllText(manifestPath + UpdateSecurity.ManifestSignatureSuffix),
                    _config.PublicKeyXml),
                "晋级正本清单验签失败。");
            var promoted = JsonUtility.FromJson<UpdateInfo>(System.Text.Encoding.UTF8.GetString(manifestBytes));
            Assert.AreEqual(_config.PromotedKeyId, promoted.KeyId);
            Assert.AreEqual(_config.CodeVersion, promoted.CodeVersion, "晋级对象应为回滚后 dev 激活的 release。");

            Assert.IsTrue(Uri.TryCreate(_config.BaseUrl, UriKind.Absolute, out Uri baseUri));
            string basePath = baseUri.AbsolutePath.TrimEnd('/') + "/";
            foreach (PatchFile patch in promoted.PatchFiles)
            {
                Assert.IsTrue(Uri.TryCreate(patch.Url, UriKind.Absolute, out Uri uri));
                string relative = uri.AbsolutePath.Substring(basePath.Length);
                StringAssert.StartsWith(_config.PromotedChannelRelative + "/releases/", relative,
                    "晋级清单补丁 URL 未重根到目标渠道根。");
                string local = Path.Combine(_config.UploadRoot, relative.Replace('/', Path.DirectorySeparatorChar));
                Assert.IsTrue(File.Exists(local), $"晋级 payload 缺失：{relative}");
                Assert.IsTrue(FileVerifier.VerifyPatchFile(local, patch, out string error), error);
            }
        }

        /// <summary>与发布端 SanitizePathSegment 同规则的版本号路径段（点与字母数字保持原样）。</summary>
        private static string HotUpdateReleaseStepsCompatibleSegment(string value)
        {
            var chars = value.ToCharArray();
            for (int i = 0; i < chars.Length; i++)
            {
                char c = chars[i];
                if (!(char.IsLetterOrDigit(c) || c == '.' || c == '_' || c == '-')) chars[i] = '_';
            }
            return new string(chars);
        }

        [Test]
        public void 发布补丁URL_通过客户端同源路径根契约()
        {
            // 客户端 UpdateServerUrl 指向渠道根：TryResolveCodePatchFiles 内部经
            // ResolveTrustedPatchUrl 强制补丁 URL 与渠道根同源且在其路径前缀下——
            // 发布端布局与客户端 URL 信任契约必须同时成立，这里用真实清单验证收敛。
            string channelRootUrl = _config.BaseUrl.TrimEnd('/') + "/" + _config.ChannelRelative;
            Assert.IsTrue(
                VersionManager.TryResolveCodePatchFiles(_server, channelRootUrl, out var resolved),
                "真实发布清单未通过客户端补丁 URL 信任解析。");
            Assert.AreEqual(_server.PatchFiles.Count, resolved.Count);
        }

        [Test]
        public void 发布补丁_逐文件完整性校验并完成事务槽安装确认()
        {
            string staging = StageAllPatchesFromUploadRoot();
            Assert.IsTrue(HotUpdateSlotManager.CommitStagingSlot(_server, staging, out string error), error);
            HotUpdateSlotManager.ConfirmPendingSlot();

            Assert.IsTrue(HotUpdateSlotManager.TryGetActiveCodeVersion(out int activeCodeVersion));
            Assert.AreEqual(_server.CodeVersion, activeCodeVersion);
            foreach (PatchFile patch in _server.PatchFiles)
            {
                Assert.IsTrue(HotUpdateSlotManager.TryResolveActiveFile(patch.FileName, out string activePath),
                    $"活动槽解析不到已安装程序集：{patch.FileName}");
                Assert.IsTrue(FileVerifier.VerifyPatchFile(activePath, patch, out string verifyError), verifyError);
            }
        }

        [Test]
        public void 故障注入_篡改DLL_哈希拒绝且拒绝提交()
        {
            string staging = StageAllPatchesFromUploadRoot();
            PatchFile victim = _server.PatchFiles[0];
            string victimPath = HotUpdateSlotManager.GetSafeStagingFilePath(staging, victim.FileName);
            byte[] bytes = File.ReadAllBytes(victimPath);
            bytes[bytes.Length - 1] ^= 0xFF;
            File.WriteAllBytes(victimPath, bytes);

            Assert.IsFalse(FileVerifier.VerifyPatchFile(victimPath, victim, out string verifyError),
                "被篡改的补丁通过了完整性校验。");
            StringAssert.Contains("不一致", verifyError);

            LogAssert.Expect(LogType.Error, new Regex(".*提交 staging 槽失败.*"));
            Assert.IsFalse(HotUpdateSlotManager.CommitStagingSlot(_server, staging, out string commitError),
                "被篡改的补丁集竟然提交成功。");
        }

        [Test]
        public void 故障注入_过期清单_时效拒绝()
        {
            AppConfigAsset appConfig = AppConfig.Load();
            Assert.IsFalse(
                UpdateSecurity.ValidateManifest(
                    _server,
                    NewFactoryBaseline(),
                    appConfig?.AppEnv,
                    appConfig?.AppChannel,
                    out string reason,
                    nowUnixSeconds: _server.ExpiresAtUnixSeconds + 1),
                "过期清单竟然通过准入。");
            StringAssert.Contains("失效", reason);
        }

        [Test]
        public void 故障注入_连续三次未确认启动_回退出厂基线()
        {
            string staging = StageAllPatchesFromUploadRoot();
            Assert.IsTrue(HotUpdateSlotManager.CommitStagingSlot(_server, staging, out string error), error);
            HotUpdateSlotManager.ConfirmPendingSlot();

            for (int attempt = 0; attempt < 3; attempt++)
            {
                HotUpdateSlotManager.ResetStateForTests();
                HotUpdateSlotManager.PrepareForLaunch();
                Assert.IsTrue(HotUpdateSlotManager.TryGetActiveCodeVersion(out _),
                    $"第 {attempt + 1} 次未确认启动就丢失了活动槽。");
            }

            HotUpdateSlotManager.ResetStateForTests();
            LogAssert.Expect(LogType.Error, new Regex(".*判定为崩溃循环.*回退整包出厂基线.*"));
            HotUpdateSlotManager.PrepareForLaunch();
            Assert.IsFalse(HotUpdateSlotManager.TryGetActiveCodeVersion(out _),
                "崩溃循环后活动槽未被清空。");
        }

        /// <summary>
        /// 客户端视角的出厂基线：只信 Application.version，资源与代码版本回到 1。
        /// </summary>
        private static UpdateInfo NewFactoryBaseline() => new UpdateInfo
        {
            AppVersion = Application.version,
            ResourceVersion = 1,
            CodeVersion = 1,
        };

        /// <summary>
        /// 从本地 uploadRoot 按清单逐文件校验后复制进事务 staging（v1 演练不含真实网络跳，
        /// 用不可变 payload 相对路径直接定位文件，等价于下载完成后的落盘状态）。
        /// </summary>
        private string StageAllPatchesFromUploadRoot()
        {
            string staging = HotUpdateSlotManager.PrepareStagingSlot(_server);
            foreach (PatchFile patch in _server.PatchFiles)
            {
                string source = ResolveLocalPayloadPath(patch);
                Assert.IsTrue(File.Exists(source), $"uploadRoot 缺少清单声明的补丁文件：{source}");
                Assert.IsTrue(FileVerifier.VerifyPatchFile(source, patch, out string verifyError), verifyError);
                File.Copy(source, HotUpdateSlotManager.GetSafeStagingFilePath(staging, patch.FileName));
            }
            return staging;
        }

        /// <summary>
        /// 把清单中的补丁 URL 映射回本地 uploadRoot 文件路径：uploadRoot 与 BaseUrl 一一对应，
        /// 取 URL 路径去掉 BaseUrl 路径前缀后的相对部分。同时断言 URL 落在渠道根的
        /// releases/ 不可变目录内（切片 A 布局契约）。
        /// </summary>
        private string ResolveLocalPayloadPath(PatchFile patch)
        {
            Assert.IsTrue(Uri.TryCreate(patch.Url, UriKind.Absolute, out Uri uri),
                $"补丁 URL 不是合法绝对地址：{patch.Url}");
            Assert.IsTrue(Uri.TryCreate(_config.BaseUrl, UriKind.Absolute, out Uri baseUri),
                $"演练 BaseUrl 不是合法绝对地址：{_config.BaseUrl}");

            string basePath = baseUri.AbsolutePath.TrimEnd('/') + "/";
            Assert.IsTrue(uri.AbsolutePath.StartsWith(basePath, StringComparison.Ordinal),
                $"补丁 URL 不在发布 BaseUrl 路径前缀下：{patch.Url}");
            string relative = uri.AbsolutePath.Substring(basePath.Length);
            StringAssert.StartsWith(_config.ChannelRelative + "/releases/", relative,
                "补丁 URL 未落在渠道根的 releases/ 不可变版本目录内。");
            return Path.Combine(_config.UploadRoot, relative.Replace('/', Path.DirectorySeparatorChar));
        }
    }
}
