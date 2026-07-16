using System;
using System.Collections.Generic;
using System.Linq;
using Framework.Core;
using NUnit.Framework;

namespace Framework.Tests
{
    /// <summary>
    /// GameEntry Manager 注册清单的拓扑门禁（与 asmdef 依赖门禁同思路：结构约束进 CI）。
    /// 清单是初始化顺序的唯一事实源——顺序违规 / 漏登记 / 依赖悬空在此挡下，不必等运行期炸。
    /// </summary>
    public class GameEntryManagerManifestTests
    {
        [Test]
        public void 清单无重复登记()
        {
            var seen = new HashSet<Type>();
            foreach (GameEntry.ManagerRegistration reg in GameEntry.ManagerManifest)
            {
                Assert.IsTrue(seen.Add(reg.ManagerType),
                    $"{reg.ManagerType.Name} 在清单中登记了多次。");
            }
        }

        [Test]
        public void 声明的依赖必须先于自身初始化_且依赖项必须在清单内()
        {
            var initialized = new HashSet<Type>();
            var all = new HashSet<Type>(GameEntry.ManagerManifest.Select(r => r.ManagerType));

            foreach (GameEntry.ManagerRegistration reg in GameEntry.ManagerManifest)
            {
                foreach (Type dependency in reg.DependsOn)
                {
                    Assert.IsTrue(all.Contains(dependency),
                        $"{reg.ManagerType.Name} 声明的依赖 {dependency.Name} 不在注册清单内（悬空依赖）。");
                    Assert.IsTrue(initialized.Contains(dependency),
                        $"清单顺序违规：{reg.ManagerType.Name} 依赖 {dependency.Name}，但后者声明在其之后。" +
                        "调整清单顺序（合法顺序存在即无环）。");
                    Assert.AreNotEqual(reg.ManagerType, dependency,
                        $"{reg.ManagerType.Name} 声明依赖自身。");
                }
                initialized.Add(reg.ManagerType);
            }
        }

        [Test]
        public void 清单完整性_框架程序集全部具体Manager都已登记()
        {
            // 完整性方向一：框架程序集里新增了 FrameworkComponent 派生 Manager 却忘进清单 → 挡下。
            // 扫描范围 = GameEntry 所在程序集 ∪ 清单现有类型所在的全部程序集：Manager 不全在
            // Framework——Event/Timer 经 .asmref 划归 Framework.Kernel，只扫 GameEntry 程序集会漏。
            // （新 Manager 若落进一个此前从无 Manager 的新程序集会逃过本扫描，属可接受盲区：
            // 该程序集一旦有第一个 Manager 进清单，后续同程序集的新增即被覆盖。）
            var assemblies = GameEntry.ManagerManifest.Select(r => r.ManagerType.Assembly)
                .Append(typeof(GameEntry).Assembly)
                .Distinct();
            var concreteManagers = assemblies
                .SelectMany(a => a.GetTypes())
                .Where(t => typeof(FrameworkComponent).IsAssignableFrom(t) && !t.IsAbstract)
                .ToHashSet();
            var manifest = GameEntry.ManagerManifest.Select(r => r.ManagerType).ToHashSet();

            var missing = concreteManagers.Except(manifest).Select(t => t.Name).OrderBy(n => n).ToArray();
            var unknown = manifest.Except(concreteManagers).Select(t => t.Name).OrderBy(n => n).ToArray();

            Assert.IsEmpty(missing,
                "以下 Manager 未登记进 GameEntry.ManagerManifest（新增 Manager 必须在清单声明初始化时机与依赖）：" +
                string.Join(", ", missing));
            Assert.IsEmpty(unknown,
                "清单中存在非 FrameworkComponent 的登记项：" + string.Join(", ", unknown));
        }
    }
}
