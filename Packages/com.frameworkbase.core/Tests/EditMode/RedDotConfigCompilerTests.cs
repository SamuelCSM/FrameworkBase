using System.IO;
using Editor.ExcelTool;
using Framework.Editor.RedDot;
using Framework.Foundation;
using NUnit.Framework;

namespace Framework.Tests
{
    public class RedDotConfigCompilerTests
    {
        [Test]
        public void RedDot工作簿可编译且Id代码产物与源表一致()
        {
            Assert.IsTrue(RedDotConfigCompiler.TryCompile(out RedDotCatalog catalog, out string report), report);
            Assert.AreEqual(2, catalog.Modules.Length);
            Assert.AreEqual(3, catalog.Nodes.Length);
            Assert.AreEqual(2, catalog.Edges.Length);
            Assert.AreEqual(1, catalog.SeenPolicies.Length);

            string generatedCode = RedDotConfigCompiler.GenerateIdsCode(catalog);
            StringAssert.DoesNotContain("\r", generatedCode, "生成代码应使用稳定的 LF 换行。");
            Assert.AreEqual(
                generatedCode,
                NormalizeLineEndings(File.ReadAllText(RedDotConfigCompiler.GeneratedIdsPath)));
        }

        [Test]
        public void 红点源表进入客户端ConfigData_边表使用无主键List模式()
        {
            ConfigExportRuleResolver resolver = ConfigExportRuleResolver.LoadDefault();
            string[] sheets =
            {
                "red_dot_module_ref", "red_dot_node_ref", "red_dot_edge_ref",
                "red_dot_seen_policy_ref", "red_dot_retired_ref"
            };
            foreach (string sheet in sheets)
            {
                Assert.AreEqual(ConfigExportTarget.ClientOnly, resolver.GetTarget(sheet));
                Assert.IsTrue(resolver.ShouldExportToClient(sheet));
                Assert.IsFalse(resolver.ShouldExportToServer(sheet));
                Assert.IsTrue(resolver.ShouldGenerateClientCode(sheet));
            }

            Assert.AreEqual(
                ExcelReader.ExcelSheetKind.List,
                resolver.ResolveSheetKind("red_dot_edge_ref", ExcelReader.ExcelSheetKind.Table));
        }

        private static string NormalizeLineEndings(string value)
            => value.Replace("\r\n", "\n").Replace('\r', '\n');
    }
}
