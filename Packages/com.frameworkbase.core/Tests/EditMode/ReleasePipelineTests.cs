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
