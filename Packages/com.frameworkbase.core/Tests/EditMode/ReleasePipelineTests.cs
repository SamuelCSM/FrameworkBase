using System;
using System.Collections.Generic;
using Framework.Editor.Release;
using NUnit.Framework;

namespace Framework.Tests
{
    /// <summary>
    /// 发布流水线编排器（ReleasePipeline）与版本递增规则（VersionPolicy）单元测试。
    /// 编排器是纯逻辑：顺序执行、失败中断、结果汇总，用假步骤即可全覆盖。
    /// </summary>
    public class ReleasePipelineTests
    {
        /// <summary>可编程假步骤：记录执行顺序，可按需抛异常。</summary>
        private class FakeStep : IReleaseStep
        {
            private readonly Action<ReleaseContext> _action;
            public string Name { get; }
            public string Description => "测试步骤";

            public FakeStep(string name, Action<ReleaseContext> action = null)
            {
                Name = name;
                _action = action;
            }

            public void Execute(ReleaseContext context) => _action?.Invoke(context);
        }

        // ── 编排器 ───────────────────────────────────────────────────────────

        [Test]
        public void 全部成功_按序执行且结果完整()
        {
            var order = new List<string>();
            var steps = new IReleaseStep[]
            {
                new FakeStep("A", _ => order.Add("A")),
                new FakeStep("B", _ => order.Add("B")),
                new FakeStep("C", _ => order.Add("C"))
            };

            var result = ReleasePipeline.Run(steps, new ReleaseContext());

            Assert.IsTrue(result.Success);
            Assert.IsNull(result.FailedStep);
            CollectionAssert.AreEqual(new[] { "A", "B", "C" }, order);
            Assert.AreEqual(3, result.Steps.Count);
            Assert.IsTrue(result.Steps.TrueForAll(s => s.Success));
        }

        [Test]
        public void 中途失败_中断且后续步骤不执行()
        {
            var order = new List<string>();
            var steps = new IReleaseStep[]
            {
                new FakeStep("A", _ => order.Add("A")),
                new FakeStep("B", _ => throw new Exception("B 炸了")),
                new FakeStep("C", _ => order.Add("C"))
            };

            var result = ReleasePipeline.Run(steps, new ReleaseContext());

            Assert.IsFalse(result.Success);
            Assert.AreEqual("B", result.FailedStep);
            StringAssert.Contains("B 炸了", result.Error);
            CollectionAssert.AreEqual(new[] { "A" }, order);
            // 结果里只有已执行的两步：A 成功、B 失败；C 未进入。
            Assert.AreEqual(2, result.Steps.Count);
            Assert.IsTrue(result.Steps[0].Success);
            Assert.IsFalse(result.Steps[1].Success);
        }

        [Test]
        public void 步骤可经上下文传递中间产物()
        {
            var steps = new IReleaseStep[]
            {
                new FakeStep("Produce", ctx => ctx.ManifestJson = "{}"),
                new FakeStep("Consume", ctx =>
                {
                    if (string.IsNullOrEmpty(ctx.ManifestJson))
                        throw new Exception("上游产物缺失");
                })
            };

            Assert.IsTrue(ReleasePipeline.Run(steps, new ReleaseContext()).Success);
        }

        [Test]
        public void 空参数_抛出()
        {
            Assert.Throws<ArgumentNullException>(() => ReleasePipeline.Run(null, new ReleaseContext()));
            Assert.Throws<ArgumentNullException>(() => ReleasePipeline.Run(Array.Empty<IReleaseStep>(), null));
        }

        // ── 失败补偿（Saga 语义）─────────────────────────────────────────────

        /// <summary>可补偿假步骤：记录补偿调用顺序，可按需让补偿本身抛异常。</summary>
        private class FakeCompensableStep : ICompensableStep
        {
            private readonly List<string> _compensations;
            private readonly bool _compensateThrows;
            public string Name { get; }
            public string Description => "可补偿测试步骤";

            public FakeCompensableStep(string name, List<string> compensations, bool compensateThrows = false)
            {
                Name = name;
                _compensations = compensations;
                _compensateThrows = compensateThrows;
            }

            public void Execute(ReleaseContext context) { }

            public void Compensate(ReleaseContext context)
            {
                if (_compensateThrows)
                    throw new Exception($"{Name} 补偿失败");
                _compensations.Add(Name);
            }
        }

        [Test]
        public void 失败时_已成功的可补偿步骤逆序补偿()
        {
            var compensations = new List<string>();
            var steps = new IReleaseStep[]
            {
                new FakeCompensableStep("A", compensations),
                new FakeStep("B"),                              // 不可补偿，应被跳过
                new FakeCompensableStep("C", compensations),
                new FakeStep("D", _ => throw new Exception("D 炸了")),
                new FakeCompensableStep("E", compensations)     // 未执行到，不应补偿
            };

            var result = ReleasePipeline.Run(steps, new ReleaseContext());

            Assert.IsFalse(result.Success);
            // 逆序：C 先于 A；E 未执行不补偿；B 非可补偿步骤不参与。
            CollectionAssert.AreEqual(new[] { "C", "A" }, compensations);
            Assert.IsTrue(result.Steps[0].Compensated);   // A
            Assert.IsFalse(result.Steps[1].Compensated);  // B
            Assert.IsTrue(result.Steps[2].Compensated);   // C
        }

        [Test]
        public void 单个补偿失败_不阻止其余补偿()
        {
            var compensations = new List<string>();
            var steps = new IReleaseStep[]
            {
                new FakeCompensableStep("A", compensations),
                new FakeCompensableStep("B", compensations, compensateThrows: true),
                new FakeStep("C", _ => throw new Exception("C 炸了"))
            };

            var result = ReleasePipeline.Run(steps, new ReleaseContext());

            Assert.IsFalse(result.Success);
            // B 补偿抛异常只记日志；A 的补偿仍执行。
            CollectionAssert.AreEqual(new[] { "A" }, compensations);
            Assert.IsTrue(result.Steps[0].Compensated);
            Assert.IsFalse(result.Steps[1].Compensated);
        }

        [Test]
        public void 全部成功_不触发补偿()
        {
            var compensations = new List<string>();
            var steps = new IReleaseStep[]
            {
                new FakeCompensableStep("A", compensations),
                new FakeCompensableStep("B", compensations)
            };

            var result = ReleasePipeline.Run(steps, new ReleaseContext());

            Assert.IsTrue(result.Success);
            Assert.AreEqual(0, compensations.Count);
        }

        // ── 版本递增规则 ─────────────────────────────────────────────────────

        [Test]
        public void 整包更新_版本归1()
        {
            Assert.AreEqual((1, 1), VersionPolicy.Next(
                forceUpdate: true, publishResource: true, publishCode: true,
                currentResource: 7, currentCode: 9));
        }

        [Test]
        public void 热更_仅勾选项递增()
        {
            Assert.AreEqual((8, 9), VersionPolicy.Next(false, true, false, 7, 9));
            Assert.AreEqual((7, 10), VersionPolicy.Next(false, false, true, 7, 9));
            Assert.AreEqual((8, 10), VersionPolicy.Next(false, true, true, 7, 9));
            Assert.AreEqual((7, 9), VersionPolicy.Next(false, false, false, 7, 9));
        }
    }
}
