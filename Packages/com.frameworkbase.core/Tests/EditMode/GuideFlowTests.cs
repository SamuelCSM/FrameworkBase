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
            public readonly Dictionary<string, int> Steps = new Dictionary<string, int>();
            public readonly HashSet<string> Done = new HashSet<string>();

            public int GetStepIndex(string guideId) => Steps.TryGetValue(guideId, out int i) ? i : 0;
            public void SetStepIndex(string guideId, int index) => Steps[guideId] = index;
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
            Assert.AreEqual(1, store.GetStepIndex("newbie"), "每步推进即落档");
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
            store.SetStepIndex("g", 1);
            var flow = new GuideFlow(new GuideScript("g", "a", "b", "c"), store);
            var entered = new List<string>();
            flow.StepEntered += (id, _) => entered.Add(id);

            Assert.IsTrue(flow.Start());
            CollectionAssert.AreEqual(new[] { "b" }, entered, "断点在 b，不重放 a");
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
        public void 剧本改短后旧存档越界_按完成收尾不卡死()
        {
            var store = new FakeStore();
            store.SetStepIndex("g", 5); // 旧版剧本存的断点
            var flow = new GuideFlow(new GuideScript("g", "a", "b"), store);
            bool completed = false;
            flow.Completed += () => completed = true;

            Assert.IsFalse(flow.Start());
            Assert.IsTrue(completed);
            Assert.IsTrue(store.IsCompleted("g"));
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
