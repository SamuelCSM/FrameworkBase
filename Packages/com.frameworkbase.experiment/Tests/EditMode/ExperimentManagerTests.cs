using System.Collections.Generic;
using Framework.Experiment;
using NUnit.Framework;

namespace Framework.Experiment.Tests
{
    /// <summary>管理器单测：解析 / 曝光去重 / 覆盖 / 预览不打点，均经注入的假来源与假 sink。</summary>
    public class ExperimentManagerTests
    {
        private sealed class FakeSource : IExperimentConfigSource
        {
            private readonly Dictionary<string, ExperimentDefinition> _defs = new Dictionary<string, ExperimentDefinition>();
            public void Add(ExperimentDefinition def) => _defs[def.key] = def;
            public bool TryGet(string key, out ExperimentDefinition def) => _defs.TryGetValue(key, out def);
        }

        private sealed class FakeSink : IExposureSink
        {
            public readonly List<(string exp, string variant)> Calls = new List<(string, string)>();
            public void TrackExposure(string experimentKey, string variant) => Calls.Add((experimentKey, variant));
        }

        private static ExperimentDefinition SingleVariant(string key, string variant) =>
            new ExperimentDefinition
            {
                key = key, enabled = true,
                variants = new[] { new ExperimentVariant { name = variant, weight = 1 } }
            };

        [TearDown]
        public void TearDown() => Experiments.SetInstance(null);

        [Test]
        public void GetVariant_ReturnsAssigned_AndFiresExposureOnce()
        {
            var source = new FakeSource();
            source.Add(SingleVariant("exp", "v1"));
            var sink = new FakeSink();
            var mgr = new ExperimentManager(source, sink);
            mgr.SetUnitId("user_1");

            Assert.AreEqual("v1", mgr.GetVariant("exp"));
            Assert.AreEqual("v1", mgr.GetVariant("exp")); // 第二次不应再曝光

            Assert.AreEqual(1, sink.Calls.Count);
            Assert.AreEqual(("exp", "v1"), sink.Calls[0]);
        }

        [Test]
        public void UnknownExperiment_ReturnsControl_AndExposesControl()
        {
            var sink = new FakeSink();
            var mgr = new ExperimentManager(new FakeSource(), sink);

            Assert.AreEqual(ExperimentAssigner.Control, mgr.GetVariant("missing"));
            Assert.AreEqual(1, sink.Calls.Count);
            Assert.AreEqual(("missing", ExperimentAssigner.Control), sink.Calls[0]);
        }

        [Test]
        public void Override_ForcesVariant()
        {
            var source = new FakeSource();
            source.Add(SingleVariant("exp", "v1"));
            var mgr = new ExperimentManager(source, new FakeSink());
            mgr.SetUnitId("user_1");

            mgr.SetOverride("exp", "forced");
            Assert.AreEqual("forced", mgr.GetVariant("exp"));

            mgr.ClearOverride("exp");
            Assert.AreEqual("v1", mgr.PeekVariant("exp"));
        }

        [Test]
        public void PeekVariant_DoesNotFireExposure()
        {
            var source = new FakeSource();
            source.Add(SingleVariant("exp", "v1"));
            var sink = new FakeSink();
            var mgr = new ExperimentManager(source, sink);

            Assert.AreEqual("v1", mgr.PeekVariant("exp"));
            Assert.AreEqual(0, sink.Calls.Count);
        }

        [Test]
        public void IsInVariant_MatchesAssignment()
        {
            var source = new FakeSource();
            source.Add(SingleVariant("exp", "v1"));
            var mgr = new ExperimentManager(source, new FakeSink());

            Assert.IsTrue(mgr.IsInVariant("exp", "v1"));
            Assert.IsFalse(mgr.IsInVariant("exp", "control"));
        }

        [Test]
        public void Experiments_Facade_UsesInjectedInstance()
        {
            var source = new FakeSource();
            source.Add(SingleVariant("exp", "vX"));
            var mgr = new ExperimentManager(source, new FakeSink());
            Experiments.SetInstance(mgr);

            Assert.AreSame(mgr, Experiments.Instance);
            Assert.AreEqual("vX", Experiments.Instance.GetVariant("exp"));
        }
    }
}
