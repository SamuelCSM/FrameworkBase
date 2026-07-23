using System.IO;
using Editor.ExcelTool;
using Framework.Editor.Guide;
using Framework.Editor.UI;
using NUnit.Framework;

namespace Framework.Tests
{
    public class GuideConfigCompilerTests
    {
        [Test]
        public void 窗口工作簿可编译且生成的Window与Target常量稳定()
        {
            Assert.IsTrue(UIWindowConfigCompiler.TryCompile(out UIWindowCatalog catalog, out string report), report);
            Assert.AreEqual(1, catalog.Modules.Length);
            Assert.AreEqual(1, catalog.Windows.Length);
            Assert.AreEqual(2, catalog.Targets.Length);

            string generated = UIWindowConfigCompiler.GenerateIdsCode(catalog);
            StringAssert.DoesNotContain("\r", generated);
            Assert.AreEqual(generated, Normalize(File.ReadAllText(UIWindowConfigCompiler.GeneratedIdsPath)));
            StringAssert.Contains("public const int Shop = 110001;", generated);
            StringAssert.Contains("public const int ShopBuyButton = 111001;", generated);
        }

        [Test]
        public void 引导工作簿可编译且GuideId与Guide内StepId分离()
        {
            Assert.IsTrue(GuideConfigCompiler.TryCompile(out GuideConfigCompilation compilation, out string report), report);
            Assert.AreEqual(1, compilation.Guides.Guides.Length);
            Assert.AreEqual(1, compilation.Guides.Steps.Length);
            Assert.AreEqual(3, compilation.Guides.StepActions.Length);
            Assert.AreEqual(1, compilation.Rules.Rules.Length);
            Assert.AreEqual(2, compilation.Triggers.Triggers.Length);
            Assert.AreEqual(2, compilation.Actions.Actions.Length);
            Assert.AreEqual(210001, compilation.Guides.Guides[0].Id);
            Assert.AreEqual(1001, compilation.Guides.Steps[0].StepId);
            Assert.AreEqual(100, compilation.Guides.Steps[0].Order);

            string generated = GuideConfigCompiler.GenerateIdsCode(compilation);
            StringAssert.DoesNotContain("\r", generated);
            Assert.AreEqual(generated, Normalize(File.ReadAllText(GuideConfigCompiler.GeneratedIdsPath)));
            StringAssert.Contains("public const int ClickerShopIntro = 210001;", generated);
            StringAssert.Contains("public const int FocusBuyButton = 1001;", generated);
        }

        [Test]
        public void 新增配置全部进入客户端且关系表使用ConfigList模式()
        {
            ConfigExportRuleResolver resolver = ConfigExportRuleResolver.LoadDefault();
            string[] clientTables =
            {
                "ui_window_module_ref", "ui_window_ref", "ui_target_ref",
                "rule_ref", "rule_node_ref", "rule_edge_ref",
                "trigger_ref", "action_ref", "guide_ref", "guide_step_ref", "guide_step_action_ref",
            };
            foreach (string table in clientTables)
            {
                Assert.AreEqual(ConfigExportTarget.ClientOnly, resolver.GetTarget(table), table);
                Assert.IsTrue(resolver.ShouldGenerateClientCode(table), table);
            }

            string[] listTables =
            {
                "rule_edge_ref", "guide_step_ref", "guide_step_action_ref", "guide_step_retired_ref",
            };
            foreach (string table in listTables)
                Assert.AreEqual(
                    ExcelReader.ExcelSheetKind.List,
                    resolver.ResolveSheetKind(table, ExcelReader.ExcelSheetKind.Table),
                    table);
        }

        private static string Normalize(string value)
            => value.Replace("\r\n", "\n").Replace('\r', '\n');
    }
}
