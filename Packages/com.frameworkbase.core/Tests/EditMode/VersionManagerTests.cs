using Framework.HotUpdate;
using NUnit.Framework;

namespace Framework.Tests
{
    /// <summary>
    /// 热更判定矩阵单测（VersionManager）：版本比较、更新类型判定、代码热更判定、兼容性。
    /// 纯静态逻辑，热更闸门的正确性直接决定"该不该整包 / 该不该拉 DLL"，必须锁死。
    /// </summary>
    public class VersionManagerTests
    {
        private static UpdateInfo V(string app, int res, int code) => new UpdateInfo
        {
            AppVersion = app,
            ResourceVersion = res,
            CodeVersion = code
        };

        // ── 版本号比较 ───────────────────────────────────────────────────────

        [Test]
        public void 版本比较_大小与相等()
        {
            Assert.Greater(VersionManager.CompareVersion("1.0.1", "1.0.0"), 0);
            Assert.Less(VersionManager.CompareVersion("1.0.0", "1.1.0"), 0);
            Assert.AreEqual(0, VersionManager.CompareVersion("1.2.3", "1.2.3"));
            Assert.AreEqual(0, VersionManager.CompareVersion("1.0", "1.0.0"), "缺省段按 0 补齐");
            Assert.Greater(VersionManager.CompareVersion("1.0.10", "1.0.9"), 0, "按数值而非字典序比较");
        }

        [Test]
        public void 安全版本比较_非法格式返回失败()
        {
            Assert.IsFalse(VersionManager.TryCompareVersion("1.bad.0", "1.0.0", out _));
            Assert.IsFalse(VersionManager.TryCompareVersion("1..0", "1.0.0", out _));
            Assert.IsFalse(VersionManager.TryCompareVersion(" 1.0.0", "1.0.0", out _));
            Assert.IsTrue(VersionManager.TryCompareVersion("1.0", "1.0.0", out int result));
            Assert.AreEqual(0, result);
        }

        // ── 更新类型判定 ─────────────────────────────────────────────────────

        [Test]
        public void 更新类型_App版本不同为整包更新()
        {
            var type = VersionManager.DetermineUpdateType(V("1.0.0", 5, 5), V("1.1.0", 1, 1));
            Assert.AreEqual(UpdateType.FullUpdate, type, "AppVersion 不同一律整包，即使资源/代码号更低");
        }

        [Test]
        public void 更新类型_同App资源或代码变更为热更()
        {
            Assert.AreEqual(UpdateType.HotUpdate,
                VersionManager.DetermineUpdateType(V("1.0.0", 1, 1), V("1.0.0", 2, 1)), "仅资源变");
            Assert.AreEqual(UpdateType.HotUpdate,
                VersionManager.DetermineUpdateType(V("1.0.0", 1, 1), V("1.0.0", 1, 2)), "仅代码变");
        }

        [Test]
        public void 更新类型_完全一致为无更新()
        {
            Assert.AreEqual(UpdateType.None,
                VersionManager.DetermineUpdateType(V("1.0.0", 3, 3), V("1.0.0", 3, 3)));
        }

        [Test]
        public void 更新类型_版本为空时不更新()
        {
            Assert.AreEqual(UpdateType.None, VersionManager.DetermineUpdateType(null, V("1.0.0", 1, 1)));
            Assert.AreEqual(UpdateType.None, VersionManager.DetermineUpdateType(V("1.0.0", 1, 1), null));
        }

        // ── 代码热更判定 ─────────────────────────────────────────────────────

        [Test]
        public void 代码热更判定_仅CodeVersion变更且非整包()
        {
            // 本地在前、服务器在后
            Assert.IsTrue(VersionManager.ShouldUpdateCode(
                serverVersion: V("1.0.0", 1, 2), localVersion: V("1.0.0", 1, 1)), "代码号变→应拉");
            Assert.IsFalse(VersionManager.ShouldUpdateCode(
                serverVersion: V("1.0.0", 2, 1), localVersion: V("1.0.0", 1, 1)), "仅资源变→不拉代码");
            Assert.IsFalse(VersionManager.ShouldUpdateCode(
                serverVersion: V("1.1.0", 1, 9), localVersion: V("1.0.0", 1, 1)),
                "App 版本不同属整包，不应触发代码热更（避免给旧壳灌新 DLL）");
            Assert.IsFalse(VersionManager.ShouldUpdateCode(null, V("1.0.0", 1, 1)));
        }

        // ── 兼容性 ───────────────────────────────────────────────────────────

        [Test]
        public void 兼容性_低于最低兼容版本判不兼容()
        {
            Assert.IsFalse(VersionManager.CheckCompatibility("1.0.0", "1.2.0"));
            Assert.IsTrue(VersionManager.CheckCompatibility("1.2.0", "1.2.0"), "等于最低版本视为兼容");
            Assert.IsTrue(VersionManager.CheckCompatibility("1.3.0", "1.2.0"));
            Assert.IsTrue(VersionManager.CheckCompatibility("1.0.0", ""), "未设最低版本视为兼容");
        }

        // ── 程序集名推导 ─────────────────────────────────────────────────────

        [Test]
        public void 程序集名_去除bytes与dll后缀()
        {
            Assert.AreEqual("GameProtocol", VersionManager.ToAssemblyName("GameProtocol.dll.bytes"));
            Assert.AreEqual("HotUpdate", VersionManager.ToAssemblyName("HotUpdate.dll"));
            Assert.AreEqual("Foo", VersionManager.ToAssemblyName("Foo.bytes"));
        }

        // ── 热更入口配置（C# 源真相：入口名/类型可配置且与清单自洽）─────────────

        [Test]
        public void 热更入口_入口程序集必在热更清单内_且入口类型为含命名空间全名()
        {
            // 用编译后的真实值校验：入口程序集若不在加载清单内，加载完成后必反射不到入口类型。
            // 该不变量对“默认（AppConfig 留空回退框架默认）”与“配置覆盖”两种情形都必须成立。
            var assemblyNames = System.Array.ConvertAll(
                VersionManager.HotUpdateAssemblyFileNames, VersionManager.ToAssemblyName);
            CollectionAssert.Contains(assemblyNames, VersionManager.EntryHotUpdateAssemblyName,
                "热更入口程序集必须是 HotUpdateAssemblyFileNames 的成员");

            string entryType = VersionManager.HotUpdateEntryTypeFullName;
            Assert.IsFalse(string.IsNullOrWhiteSpace(entryType), "入口类型全名不得为空");
            StringAssert.Contains(".", entryType, "入口类型必须是含命名空间的全名");
        }

        [Test]
        public void 热更入口_默认值与框架约定一致()
        {
            // 锁死默认约定：AppConfig 未配置时回退值即历史硬编码约定，保证既有项目零迁移。
            Assert.AreEqual("HotUpdate.Entry.HotfixEntry", VersionManager.DefaultHotUpdateEntryTypeFullName);
            Assert.AreEqual("HotUpdate", VersionManager.ToAssemblyName(VersionManager.DefaultCodePatchFileName));
        }
    }
}
