using System;
using System.Collections.Generic;
using NUnit.Framework;

namespace Framework.Tests
{
    public class GuideFlowTests
    {
        /// <summary>内存断点存储：记录写入序列供断言。</summary>
        private sealed class FakeStore : IGuideProgressStore
        {
            public readonly Dictionary<string, string> Steps = new Dictionary<string, string>();
            public readonly HashSet<string> Done = new HashSet<string>();

            public string GetStepId(string guideId) => Steps.TryGetValue(guideId, out string id) ? id : string.Empty;
            public void SetStepId(string guideId, string stepId) => Steps[guideId] = stepId;
            public bool IsCompleted(string guideId) => Done.Contains(guideId);
            public void MarkCompleted(string guideId) => Done.Add(guideId);
            public void Clear(string guideId)
            {
                Steps.Remove(guideId);
                Done.Remove(guideId);
            }
        }

        [Test]
        public void 全新启动_按序完成每步_最后一步整条完成()
        {
            var store = new FakeStore();
            var flow = new GuideFlow(new GuideScript("newbie", "click_bag", "equip_item", "close"), store);
            var entered = new List<string>();
            bool completed = false;
            flow.StepEntered += (id, index) => entered.Add($"{index}:{id}");
            flow.Completed += () => completed = true;

            Assert.IsTrue(flow.Start());
            Assert.AreEqual("click_bag", flow.CurrentStepId);

            flow.CompleteStep("click_bag");
            Assert.AreEqual("equip_item", store.GetStepId("newbie"), "每步推进即落档存下一步 id");
            flow.CompleteStep("equip_item");
            flow.CompleteStep("close");

            CollectionAssert.AreEqual(new[] { "0:click_bag", "1:equip_item", "2:close" }, entered);
            Assert.IsTrue(completed);
            Assert.IsTrue(flow.IsCompleted);
            Assert.IsFalse(flow.IsRunning);
            Assert.IsNull(flow.CurrentStepId);
        }

        [Test]
        public void 断点续跑_从存档步骤进入()
        {
            var store = new FakeStore();
            store.SetStepId("g", "b");
            var flow = new GuideFlow(new GuideScript("g", "a", "b", "c"), store);
            var entered = new List<string>();
            flow.StepEntered += (id, _) => entered.Add(id);

            Assert.IsTrue(flow.Start());
            CollectionAssert.AreEqual(new[] { "b" }, entered, "断点在 b，不重放 a");
        }

        [Test]
        public void 断点前插入新步骤_按id续在原步骤_不因序号漂移错位()
        {
            // 玩家断点在 "b"。线上新版在 a 前插了一步 intro——旧的存序号模型会让玩家续到错位的一步，
            // 存 id 则按 "b" 在新剧本里重新定位（序号从 1 变 2），玩家仍续在正确的 b 上。
            var store = new FakeStore();
            store.SetStepId("g", "b");
            var flow = new GuideFlow(new GuideScript("g", "intro", "a", "b", "c"), store);
            var entered = new List<string>();
            flow.StepEntered += (id, index) => entered.Add($"{index}:{id}");

            Assert.IsTrue(flow.Start());
            CollectionAssert.AreEqual(new[] { "2:b" }, entered, "按 id 续在 b（新序号 2），不错位到 intro/a");
        }

        [Test]
        public void 断点步骤被删或改名_id找不到_从头重播不卡死()
        {
            var store = new FakeStore();
            store.SetStepId("g", "removed_step"); // 该步在当前剧本已不存在
            var flow = new GuideFlow(new GuideScript("g", "a", "b"), store);
            var entered = new List<string>();
            flow.StepEntered += (id, _) => entered.Add(id);

            Assert.IsTrue(flow.Start(), "断点步骤找不到时从头重播，而非卡死或静默完成");
            CollectionAssert.AreEqual(new[] { "a" }, entered);
        }

        [Test]
        public void 已完成的引导_Start返回false且无事件()
        {
            var store = new FakeStore();
            store.MarkCompleted("g");
            var flow = new GuideFlow(new GuideScript("g", "a"), store);
            var entered = new List<string>();
            flow.StepEntered += (id, _) => entered.Add(id);

            Assert.IsFalse(flow.Start());
            Assert.IsEmpty(entered);
        }

        [Test]
        public void 乱序或未运行的完成_抛异常()
        {
            var store = new FakeStore();
            var flow = new GuideFlow(new GuideScript("g", "a", "b"), store);

            Assert.Throws<InvalidOperationException>(() => flow.CompleteStep("a"), "未 Start");

            flow.Start();
            Assert.Throws<InvalidOperationException>(() => flow.CompleteStep("b"), "跳步");
            Assert.Throws<InvalidOperationException>(() => flow.CompleteStep("nope"), "未知步骤");

            flow.CompleteStep("a");
            Assert.Throws<InvalidOperationException>(() => flow.CompleteStep("a"), "迟到的重复完成");
        }

        [Test]
        public void 跳过_标记完成且幂等_未启动也可跳过()
        {
            var store = new FakeStore();
            var flow = new GuideFlow(new GuideScript("g", "a", "b"), store);
            int completedCount = 0;
            flow.Completed += () => completedCount++;

            flow.Skip(); // 未启动直接跳过（防重复弹出）
            Assert.IsTrue(flow.IsCompleted);
            flow.Skip(); // 已完成再跳过：无第二次事件
            Assert.AreEqual(1, completedCount);
            Assert.IsFalse(flow.Start(), "跳过后不再启动");
        }

        [Test]
        public void Reset清进度_可从头重跑()
        {
            var store = new FakeStore();
            var flow = new GuideFlow(new GuideScript("g", "a", "b"), store);
            flow.Start();
            flow.CompleteStep("a");
            flow.CompleteStep("b");
            Assert.IsTrue(flow.IsCompleted);

            flow.Reset();
            Assert.IsFalse(flow.IsCompleted);

            var entered = new List<string>();
            flow.StepEntered += (id, _) => entered.Add(id);
            Assert.IsTrue(flow.Start());
            CollectionAssert.AreEqual(new[] { "a" }, entered);
        }

        [Test]
        public void 订阅者异常隔离_流程照常推进_送ErrorSink()
        {
            var store = new FakeStore();
            var sink = new List<Exception>();
            var flow = new GuideFlow(new GuideScript("g", "a"), store) { ObserverErrorSink = sink.Add };
            flow.StepEntered += (_, __) => throw new Exception("表现层炸了");

            Assert.IsTrue(flow.Start(), "订阅者异常不阻断流程");
            flow.CompleteStep("a");

            Assert.IsTrue(flow.IsCompleted);
            Assert.AreEqual(1, sink.Count);
        }

        [Test]
        public void 剧本校验_空id_空步骤_重复步骤_空白步骤均拒绝()
        {
            Assert.Throws<ArgumentException>(() => new GuideScript("", "a"));
            Assert.Throws<ArgumentException>(() => new GuideScript("g"));
            Assert.Throws<ArgumentException>(() => new GuideScript("g", "a", "a"));
            Assert.Throws<ArgumentException>(() => new GuideScript("g", "a", " "));
        }
    }
}
