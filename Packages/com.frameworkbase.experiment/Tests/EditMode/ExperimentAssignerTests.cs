using Framework.Experiment;
using NUnit.Framework;

namespace Framework.Experiment.Tests
{
    /// <summary>纯分配逻辑单测：兜底 / 稳定性 / 权重分布 / 盐值重洗。</summary>
    public class ExperimentAssignerTests
    {
        private static ExperimentDefinition Def(string key, params (string name, int weight)[] variants)
        {
            var vs = new ExperimentVariant[variants.Length];
            for (int i = 0; i < variants.Length; i++)
                vs[i] = new ExperimentVariant { name = variants[i].name, weight = variants[i].weight };
            return new ExperimentDefinition { key = key, enabled = true, salt = "", variants = vs };
        }

        [Test]
        public void NullOrInvalidDef_ReturnsControl()
        {
            Assert.AreEqual(ExperimentAssigner.Control, ExperimentAssigner.Assign("u1", null));
            Assert.AreEqual(ExperimentAssigner.Control, ExperimentAssigner.Assign("u1",
                new ExperimentDefinition { key = "x", enabled = false, variants = new[] { new ExperimentVariant { name = "a", weight = 1 } } }));
            Assert.AreEqual(ExperimentAssigner.Control, ExperimentAssigner.Assign("u1", Def("x")));
        }

        [Test]
        public void ZeroTotalWeight_ReturnsControl()
        {
            Assert.AreEqual(ExperimentAssigner.Control,
                ExperimentAssigner.Assign("u1", Def("x", ("a", 0), ("b", 0))));
        }

        [Test]
        public void SingleFullWeightVariant_AlwaysAssigned()
        {
            var def = Def("x", ("only", 100));
            for (int i = 0; i < 500; i++)
                Assert.AreEqual("only", ExperimentAssigner.Assign("user_" + i, def));
        }

        [Test]
        public void Deterministic_SameUnitSameVariant()
        {
            var def = Def("shop_layout", ("control", 50), ("v1", 50));
            for (int i = 0; i < 200; i++)
            {
                string unit = "user_" + i;
                string first = ExperimentAssigner.Assign(unit, def);
                Assert.AreEqual(first, ExperimentAssigner.Assign(unit, def), "同一单元多次分配必须一致");
            }
        }

        [Test]
        public void WeightDistribution_ApproximatelyProportional()
        {
            var def = Def("x", ("control", 50), ("v1", 50));
            int n = 4000, v1 = 0;
            for (int i = 0; i < n; i++)
                if (ExperimentAssigner.Assign("dist_" + i, def) == "v1") v1++;

            double ratio = (double)v1 / n;
            Assert.That(ratio, Is.InRange(0.42, 0.58), $"50/50 实测 v1 占比 {ratio:P1}");
        }

        [Test]
        public void WeightDistribution_Respects80_20()
        {
            var def = Def("x", ("control", 80), ("v1", 20));
            int n = 4000, v1 = 0;
            for (int i = 0; i < n; i++)
                if (ExperimentAssigner.Assign("skew_" + i, def) == "v1") v1++;

            double ratio = (double)v1 / n;
            Assert.That(ratio, Is.InRange(0.14, 0.26), $"80/20 实测 v1 占比 {ratio:P1}");
        }

        [Test]
        public void DifferentSalt_ReshufflesSomeAssignments()
        {
            var a = Def("x", ("control", 50), ("v1", 50));
            var b = new ExperimentDefinition
            {
                key = "x", enabled = true, salt = "round2",
                variants = new[]
                {
                    new ExperimentVariant { name = "control", weight = 50 },
                    new ExperimentVariant { name = "v1", weight = 50 },
                }
            };

            int changed = 0, n = 500;
            for (int i = 0; i < n; i++)
            {
                string unit = "salt_" + i;
                if (ExperimentAssigner.Assign(unit, a) != ExperimentAssigner.Assign(unit, b))
                    changed++;
            }
            Assert.Greater(changed, 0, "改盐后应有部分单元被重洗到另一变体");
        }
    }
}
