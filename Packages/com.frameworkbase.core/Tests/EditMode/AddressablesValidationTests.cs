using System.Collections.Generic;
using System.Linq;
using Framework.Editor;
using NUnit.Framework;

namespace Framework.Tests
{
    /// <summary>
    /// Addressables 校验规则引擎单测：对纯模型逐条验证规则触发与放行，
    /// 不碰 AssetDatabase / Addressables Settings（采集层不在此测）。
    /// </summary>
    public class AddressablesValidationTests
    {
        private AddressablesValidationThresholds _th;

        [SetUp]
        public void SetUp()
        {
            _th = new AddressablesValidationThresholds(); // 默认值即框架规范
        }

        // ── 构造工具 ─────────────────────────────────────────────────────────

        private AddressablesGroupModel RemoteGroup(string name = "Activity")
        {
            return new AddressablesGroupModel
            {
                Name = name,
                HasBundledSchema = true,
                BuildPathName = _th.RemoteBuildPath,
                LoadPathName = _th.RemoteLoadPath,
            };
        }

        private AddressablesGroupModel LocalGroup(string name = "Framework")
        {
            return new AddressablesGroupModel
            {
                Name = name,
                HasBundledSchema = true,
                BuildPathName = _th.LocalBuildPath,
                LoadPathName = _th.LocalLoadPath,
            };
        }

        private static AddressablesEntryModel Entry(
            string path, string address, string expected = null,
            long size = 100, bool remoteLabel = true, bool isScene = false)
        {
            return new AddressablesEntryModel
            {
                Guid = path, // 测试里用路径充当 guid 即可
                AssetPath = path,
                Address = address,
                ExpectedAddress = expected ?? address,
                SizeBytes = size,
                HasRemoteLabel = remoteLabel,
                IsScene = isScene,
            };
        }

        private static List<AddressablesValidationIssue> OfRule(
            List<AddressablesValidationIssue> issues, string rule)
            => issues.Where(i => i.Rule == rule).ToList();

        // ── 通过基线 ─────────────────────────────────────────────────────────

        [Test]
        public void 合规配置_零问题()
        {
            var model = new AddressablesValidationModel();
            var remote = RemoteGroup();
            remote.Entries.Add(Entry("Assets/ResourcesOut/Activity/a.prefab", "Activity/a"));
            var local = LocalGroup();
            local.Entries.Add(Entry("Packages/pkg/Runtime/f.prefab", "f", expected: null, remoteLabel: false));
            local.Entries[0].ExpectedAddress = null; // 内置资源不受目录规范约束
            model.Groups.Add(remote);
            model.Groups.Add(local);
            model.AddressableAssetPaths.Add("Assets/ResourcesOut/Activity/a.prefab");

            var issues = AddressablesValidationRules.Validate(model, _th);

            Assert.IsEmpty(issues, string.Join("\n", issues));
        }

        // ── 组级规则 ─────────────────────────────────────────────────────────

        [Test]
        public void 组缺Schema_报Error且不再查路径()
        {
            var model = new AddressablesValidationModel();
            var g = RemoteGroup("Broken");
            g.HasBundledSchema = false;
            g.BuildPathName = ""; // 路径同样非法，但只应报 Schema 一条
            model.Groups.Add(g);

            var issues = AddressablesValidationRules.Validate(model, _th);

            Assert.AreEqual(1, issues.Count);
            Assert.AreEqual("GroupMissingSchema", issues[0].Rule);
            Assert.AreEqual(AddressablesIssueSeverity.Error, issues[0].Severity);
        }

        [Test]
        public void 远端组走本地路径_报Error()
        {
            var model = new AddressablesValidationModel();
            var g = RemoteGroup("Activity");
            g.BuildPathName = _th.LocalBuildPath;
            g.LoadPathName = _th.LocalLoadPath;
            g.Entries.Add(Entry("Assets/ResourcesOut/Activity/a.prefab", "Activity/a"));
            model.Groups.Add(g);

            var issues = OfRule(AddressablesValidationRules.Validate(model, _th), "RemoteGroupWrongPath");

            Assert.AreEqual(1, issues.Count, "热更资源被焊死进包必须 Error");
            Assert.AreEqual(AddressablesIssueSeverity.Error, issues[0].Severity);
        }

        [Test]
        public void 本地白名单组走远端路径_报Error()
        {
            var model = new AddressablesValidationModel();
            var g = LocalGroup("Framework");
            g.BuildPathName = _th.RemoteBuildPath;
            g.LoadPathName = _th.RemoteLoadPath;
            g.Entries.Add(Entry("Assets/x.prefab", "x", expected: null, remoteLabel: false));
            model.Groups.Add(g);

            Assert.AreEqual(1, OfRule(AddressablesValidationRules.Validate(model, _th), "LocalGroupWrongPath").Count);
        }

        [Test]
        public void 场景与资产混包_报Error_纯场景组放行()
        {
            var model = new AddressablesValidationModel();
            var mixed = RemoteGroup("Mixed");
            mixed.Entries.Add(Entry("Assets/ResourcesOut/Mixed/s.unity", "Mixed/s", isScene: true));
            mixed.Entries.Add(Entry("Assets/ResourcesOut/Mixed/a.prefab", "Mixed/a"));
            var scenes = RemoteGroup("Scenes");
            scenes.Entries.Add(Entry("Assets/ResourcesOut/Scenes/l1.unity", "Scenes/l1", isScene: true));
            model.Groups.Add(mixed);
            model.Groups.Add(scenes);

            var issues = OfRule(AddressablesValidationRules.Validate(model, _th), "SceneMixedWithAssets");

            Assert.AreEqual(1, issues.Count);
            StringAssert.Contains("Mixed", issues[0].Message);
        }

        [Test]
        public void 空组_报Warning()
        {
            var model = new AddressablesValidationModel();
            model.Groups.Add(RemoteGroup("Empty"));

            var issues = OfRule(AddressablesValidationRules.Validate(model, _th), "EmptyGroup");
            Assert.AreEqual(1, issues.Count);
            Assert.AreEqual(AddressablesIssueSeverity.Warning, issues[0].Severity);
        }

        // ── 条目级规则 ───────────────────────────────────────────────────────

        [Test]
        public void 地址不符合规范_报Warning_无规范约束时放行()
        {
            var model = new AddressablesValidationModel();
            var g = RemoteGroup();
            g.Entries.Add(Entry("Assets/ResourcesOut/Activity/a.prefab", "wrong_addr", expected: "Activity/a"));
            var free = Entry("Assets/Other/b.prefab", "anything");
            free.ExpectedAddress = null; // 不在受管目录，地址自由
            g.Entries.Add(free);
            model.Groups.Add(g);

            var issues = OfRule(AddressablesValidationRules.Validate(model, _th), "AddressMismatch");

            Assert.AreEqual(1, issues.Count);
            StringAssert.Contains("Activity/a", issues[0].Message);
        }

        [Test]
        public void 远端条目缺remote标签_报Warning_本地组不要求()
        {
            var model = new AddressablesValidationModel();
            var remote = RemoteGroup();
            remote.Entries.Add(Entry("Assets/ResourcesOut/Activity/a.prefab", "Activity/a", remoteLabel: false));
            var local = LocalGroup();
            var e = Entry("Assets/f.prefab", "f", remoteLabel: false);
            e.ExpectedAddress = null;
            local.Entries.Add(e);
            model.Groups.Add(remote);
            model.Groups.Add(local);

            var issues = OfRule(AddressablesValidationRules.Validate(model, _th), "MissingRemoteLabel");

            Assert.AreEqual(1, issues.Count, "只有远端组要求 remote label");
        }

        [Test]
        public void 体积超阈值_单资产与组各自告警()
        {
            _th.MaxSingleAssetBytes = 1000;
            _th.MaxGroupSourceBytes = 1500;

            var model = new AddressablesValidationModel();
            var g = RemoteGroup();
            g.Entries.Add(Entry("Assets/ResourcesOut/Activity/big.png", "Activity/big", size: 1200));
            g.Entries.Add(Entry("Assets/ResourcesOut/Activity/ok.png", "Activity/ok", size: 800));
            model.Groups.Add(g);

            var issues = AddressablesValidationRules.Validate(model, _th);

            Assert.AreEqual(1, OfRule(issues, "AssetOverBudget").Count, "1200 > 1000 单资产超限");
            Assert.AreEqual(1, OfRule(issues, "GroupOverBudget").Count, "2000 > 1500 组超限");
        }

        // ── 跨组规则 ─────────────────────────────────────────────────────────

        [Test]
        public void 隐式依赖被多组引用_报重复打包_显式注册则放行()
        {
            var model = new AddressablesValidationModel();
            var g1 = RemoteGroup("A");
            g1.Entries.Add(Entry("Assets/ResourcesOut/A/a.prefab", "A/a"));
            var g2 = RemoteGroup("B");
            g2.Entries.Add(Entry("Assets/ResourcesOut/B/b.prefab", "B/b"));
            model.Groups.Add(g1);
            model.Groups.Add(g2);
            model.AddressableAssetPaths.Add("Assets/ResourcesOut/A/a.prefab");
            model.AddressableAssetPaths.Add("Assets/ResourcesOut/B/b.prefab");

            // shared.png 未显式注册，被两组条目依赖 → 会各拷一份
            model.Dependencies["Assets/ResourcesOut/A/a.prefab"] =
                new List<string> { "Assets/Art/shared.png", "Assets/Art/only_a.png" };
            model.Dependencies["Assets/ResourcesOut/B/b.prefab"] =
                new List<string> { "Assets/Art/shared.png" };
            model.AssetSizes["Assets/Art/shared.png"] = 5 * 1024 * 1024;

            var issues = OfRule(AddressablesValidationRules.Validate(model, _th), "DuplicateImplicitDependency");

            Assert.AreEqual(1, issues.Count, "只有被 ≥2 组依赖的未注册资产才报");
            StringAssert.Contains("shared.png", issues[0].Message);
            StringAssert.Contains("A", issues[0].Message);
            StringAssert.Contains("B", issues[0].Message);

            // 显式注册后不再报（引用共享，同一 bundle）
            model.AddressableAssetPaths.Add("Assets/Art/shared.png");
            Assert.IsEmpty(OfRule(AddressablesValidationRules.Validate(model, _th), "DuplicateImplicitDependency"));
        }

        [Test]
        public void 同组多条目共享依赖_不算重复打包()
        {
            var model = new AddressablesValidationModel();
            var g = RemoteGroup("A");
            g.Entries.Add(Entry("Assets/ResourcesOut/A/a1.prefab", "A/a1"));
            g.Entries.Add(Entry("Assets/ResourcesOut/A/a2.prefab", "A/a2"));
            model.Groups.Add(g);
            model.Dependencies["Assets/ResourcesOut/A/a1.prefab"] = new List<string> { "Assets/Art/shared.png" };
            model.Dependencies["Assets/ResourcesOut/A/a2.prefab"] = new List<string> { "Assets/Art/shared.png" };

            Assert.IsEmpty(OfRule(AddressablesValidationRules.Validate(model, _th), "DuplicateImplicitDependency"),
                "同组内共享依赖只会进同一个 bundle，一份拷贝，不算重复");
        }

        // ── 远端 bundle 布局审计（规则 11~13）───────────────────────────────

        [Test]
        public void 远端命名无内容哈希_报Warning_本地组与哈希命名放行()
        {
            var model = new AddressablesValidationModel();
            var noHash = RemoteGroup("NoHash");
            noHash.Naming = BundleNamingKind.NoHash;
            noHash.Entries.Add(Entry("Assets/ResourcesOut/NoHash/a.prefab", "NoHash/a"));
            var fileNameHash = RemoteGroup("FileNameHash");
            fileNameHash.Naming = BundleNamingKind.FileNameHash;
            fileNameHash.Entries.Add(Entry("Assets/ResourcesOut/FileNameHash/b.prefab", "FileNameHash/b"));
            var appendHash = RemoteGroup("AppendHash");
            appendHash.Naming = BundleNamingKind.AppendHash;
            appendHash.Entries.Add(Entry("Assets/ResourcesOut/AppendHash/c.prefab", "AppendHash/c"));
            // 本地组即便 NoHash 也不报（随包内置，不涉及 CDN）
            var local = LocalGroup();
            local.Naming = BundleNamingKind.NoHash;
            var e = Entry("Assets/f.prefab", "f", remoteLabel: false);
            e.ExpectedAddress = null;
            local.Entries.Add(e);
            model.Groups.Add(noHash);
            model.Groups.Add(fileNameHash);
            model.Groups.Add(appendHash);
            model.Groups.Add(local);

            var issues = OfRule(AddressablesValidationRules.Validate(model, _th), "RemoteBundleNamingNoContentHash");

            Assert.AreEqual(2, issues.Count, "NoHash 与 FileNameHash 两个远端组各报一条，AppendHash 与本地组放行");
            Assert.IsTrue(issues.All(i => i.Severity == AddressablesIssueSeverity.Warning));
        }

        [Test]
        public void 远端未压缩_报Warning_压缩组放行()
        {
            var model = new AddressablesValidationModel();
            var raw = RemoteGroup("Raw");
            raw.Compression = BundleCompressionKind.Uncompressed;
            raw.Entries.Add(Entry("Assets/ResourcesOut/Raw/a.prefab", "Raw/a"));
            var lz4 = RemoteGroup("Lz4");
            lz4.Compression = BundleCompressionKind.LZ4;
            lz4.Entries.Add(Entry("Assets/ResourcesOut/Lz4/b.prefab", "Lz4/b"));
            model.Groups.Add(raw);
            model.Groups.Add(lz4);

            var issues = OfRule(AddressablesValidationRules.Validate(model, _th), "RemoteBundleUncompressed");

            Assert.AreEqual(1, issues.Count);
            StringAssert.Contains("Raw", issues[0].Message);
        }

        [Test]
        public void PackTogether条目超阈值_报补丁粒度粗_分离打包放行()
        {
            _th.MaxPackTogetherRemoteEntries = 3;

            var model = new AddressablesValidationModel();
            var together = RemoteGroup("Together");
            together.Packing = BundlePackingKind.PackTogether;
            for (int i = 0; i < 5; i++)
                together.Entries.Add(Entry($"Assets/ResourcesOut/Together/a{i}.prefab", $"Together/a{i}"));

            // 同样多条目但 PackSeparately → 每条一包，粒度细，不报
            var separately = RemoteGroup("Separately");
            separately.Packing = BundlePackingKind.PackSeparately;
            for (int i = 0; i < 5; i++)
                separately.Entries.Add(Entry($"Assets/ResourcesOut/Separately/b{i}.prefab", $"Separately/b{i}"));

            model.Groups.Add(together);
            model.Groups.Add(separately);

            var issues = OfRule(AddressablesValidationRules.Validate(model, _th), "CoarsePatchGranularity");

            Assert.AreEqual(1, issues.Count, "只有 PackTogether 且条目超阈值才报");
            StringAssert.Contains("Together", issues[0].Message);
        }

        [Test]
        public void bundle布局字段未采集_不误触任何布局规则()
        {
            // Packing/Naming/Compression 全 Unknown（合成模型未设）→ 三条规则都应跳过
            var model = new AddressablesValidationModel();
            var g = RemoteGroup("Default");
            for (int i = 0; i < 100; i++) // 条目再多，Packing 未知也不判粒度
                g.Entries.Add(Entry($"Assets/ResourcesOut/Default/a{i}.prefab", $"Default/a{i}"));
            model.Groups.Add(g);
            foreach (var e in g.Entries) model.AddressableAssetPaths.Add(e.AssetPath);

            var issues = AddressablesValidationRules.Validate(model, _th);

            Assert.IsEmpty(OfRule(issues, "RemoteBundleNamingNoContentHash"));
            Assert.IsEmpty(OfRule(issues, "RemoteBundleUncompressed"));
            Assert.IsEmpty(OfRule(issues, "CoarsePatchGranularity"));
        }

        // ── 漂移检测 ─────────────────────────────────────────────────────────

        [Test]
        public void 受管目录未注册资产_汇总一条Warning()
        {
            var model = new AddressablesValidationModel();
            model.UnregisteredManagedAssets.Add("Assets/ResourcesOut/A/new1.prefab");
            model.UnregisteredManagedAssets.Add("Assets/ResourcesOut/A/new2.prefab");

            var issues = OfRule(AddressablesValidationRules.Validate(model, _th), "UnregisteredManagedAsset");

            Assert.AreEqual(1, issues.Count, "漂移按汇总报一条，不逐个刷屏");
            StringAssert.Contains("2 个", issues[0].Message);
        }
    }
}
