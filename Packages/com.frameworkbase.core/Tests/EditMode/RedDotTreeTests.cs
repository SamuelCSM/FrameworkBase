using System;
using System.Collections.Generic;
using System.Linq;
using Framework.Foundation;
using NUnit.Framework;

namespace Framework.Tests
{
    public class RedDotTreeTests
    {
        // ── 计数与聚合 ──────────────────────────────────────────────────────

        [Test]
        public void 叶子计数沿祖先链聚合_读不存在的路径返回零()
        {
            var tree = new RedDotTree();
            tree.SetCount("Mail/System", 3);
            tree.SetCount("Mail/Friend", 2);
            tree.SetCount("Bag/Equip/New", 1);

            Assert.AreEqual(3, tree.GetCount("Mail/System"));
            Assert.AreEqual(5, tree.GetCount("Mail"));
            Assert.AreEqual(1, tree.GetCount("Bag"));
            Assert.AreEqual(6, tree.TotalCount);
            Assert.AreEqual(0, tree.GetCount("Nope/NotExist"), "读不创建节点且返回 0");
        }

        [Test]
        public void 重复设同值不触发任何变化_覆盖设值按差量聚合()
        {
            var tree = new RedDotTree();
            var notified = new List<int>();
            tree.Subscribe("Mail", notified.Add, notifyImmediately: false);

            tree.SetCount("Mail/System", 3);
            tree.SetCount("Mail/System", 3); // 同值：不通知
            tree.SetCount("Mail/System", 1); // 差量 -2

            CollectionAssert.AreEqual(new[] { 3, 1 }, notified);
            Assert.AreEqual(1, tree.TotalCount);
        }

        [Test]
        public void AddCount增量修改_结果为负按零截断()
        {
            var tree = new RedDotTree();
            tree.AddCount("Task/Daily", 2);
            tree.AddCount("Task/Daily", 3);
            Assert.AreEqual(5, tree.GetCount("Task/Daily"));

            tree.AddCount("Task/Daily", -100);
            Assert.AreEqual(0, tree.GetCount("Task/Daily"), "过度消费按 0 截断，不把计数打穿");
        }

        // ── 结构性错误 fail-loud ────────────────────────────────────────────

        [Test]
        public void 非叶子写计数抛异常_持有计数的叶子挂子节点抛异常()
        {
            var tree = new RedDotTree();
            tree.SetCount("Mail/System", 1);

            // Mail 已有子节点 → 写计数是双重计数歧义
            Assert.Throws<InvalidOperationException>(() => tree.SetCount("Mail", 5));

            // Mail/System 已作为叶子持有计数 → 其下挂子节点同样歧义
            Assert.Throws<InvalidOperationException>(() => tree.SetCount("Mail/System/Sub", 1));

            // 清零后的叶子可以转为内部节点（计数为 0 无歧义）
            tree.SetCount("Mail/System", 0);
            tree.SetCount("Mail/System/Sub", 2);
            Assert.AreEqual(2, tree.GetCount("Mail/System"));
        }

        [Test]
        public void 路径校验_空路径_空段_首尾分隔符_空白字符_负计数均拒绝()
        {
            var tree = new RedDotTree();
            Assert.Throws<ArgumentException>(() => tree.SetCount(null, 1));
            Assert.Throws<ArgumentException>(() => tree.SetCount("", 1));
            Assert.Throws<ArgumentException>(() => tree.SetCount("a//b", 1));
            Assert.Throws<ArgumentException>(() => tree.SetCount("/a", 1));
            Assert.Throws<ArgumentException>(() => tree.SetCount("a/", 1));
            Assert.Throws<ArgumentException>(() => tree.SetCount("a b", 1));
            Assert.Throws<ArgumentOutOfRangeException>(() => tree.SetCount("ok", -1));
        }

        // ── 订阅与通知 ──────────────────────────────────────────────────────

        [Test]
        public void 订阅立即回调当前值_变化时路径上值变的节点都收到通知()
        {
            var tree = new RedDotTree();
            tree.SetCount("Mail/System", 3);

            var immediate = new List<int>();
            tree.Subscribe("Mail", immediate.Add);
            CollectionAssert.AreEqual(new[] { 3 }, immediate, "订阅默认立即回调当前值");

            var leaf = new List<int>();
            var mid = new List<int>();
            tree.Subscribe("Mail/System", leaf.Add, notifyImmediately: false);
            tree.Subscribe("Mail", mid.Add, notifyImmediately: false);

            tree.SetCount("Mail/System", 5);
            CollectionAssert.AreEqual(new[] { 5 }, leaf);
            CollectionAssert.AreEqual(new[] { 5 }, mid);

            // 兄弟叶子变化：Mail 聚合变，Mail/System 不变不通知
            tree.SetCount("Mail/Friend", 1);
            CollectionAssert.AreEqual(new[] { 5 }, leaf);
            CollectionAssert.AreEqual(new[] { 5, 6 }, mid);
        }

        [Test]
        public void 退订句柄Dispose幂等_退订后不再收通知()
        {
            var tree = new RedDotTree();
            var notified = new List<int>();
            IDisposable handle = tree.Subscribe("A", notified.Add, notifyImmediately: false);

            tree.SetCount("A", 1);
            handle.Dispose();
            handle.Dispose();
            tree.SetCount("A", 2);

            CollectionAssert.AreEqual(new[] { 1 }, notified);
        }

        [Test]
        public void 回调内退订自身安全_其余订阅者仍被通知()
        {
            var tree = new RedDotTree();
            var received = new List<string>();
            IDisposable selfUnsub = null;
            selfUnsub = tree.Subscribe("A", _ =>
            {
                received.Add("first");
                selfUnsub.Dispose(); // 通知回调内退订自身
            }, notifyImmediately: false);
            tree.Subscribe("A", _ => received.Add("second"), notifyImmediately: false);

            tree.SetCount("A", 1);
            tree.SetCount("A", 2);

            // 第一次两者都收到；第二次只剩 second
            CollectionAssert.AreEqual(new[] { "first", "second", "second" }, received);
        }

        [Test]
        public void 订阅者异常被隔离_送ErrorSink_其余订阅者不受影响()
        {
            var sink = new List<Exception>();
            var tree = new RedDotTree { ObserverErrorSink = sink.Add };
            var notified = new List<int>();

            tree.Subscribe("A", _ => throw new Exception("订阅者炸了"), notifyImmediately: false);
            tree.Subscribe("A", notified.Add, notifyImmediately: false);

            tree.SetCount("A", 1);

            Assert.AreEqual(1, sink.Count);
            Assert.AreEqual("订阅者炸了", sink[0].Message);
            CollectionAssert.AreEqual(new[] { 1 }, notified);
        }

        // ── 子树清零 ────────────────────────────────────────────────────────

        [Test]
        public void 清零子树_子树内与上方祖先都按新值通知_树外不受影响()
        {
            var tree = new RedDotTree();
            tree.SetCount("Mail/System/A", 1);
            tree.SetCount("Mail/System/B", 2);
            tree.SetCount("Mail/Friend", 4);
            tree.SetCount("Bag/New", 8);

            var mailNotified = new List<int>();
            var subNotified = new List<int>();
            tree.Subscribe("Mail", mailNotified.Add, notifyImmediately: false);
            tree.Subscribe("Mail/System", subNotified.Add, notifyImmediately: false);

            tree.ClearSubtree("Mail/System");

            Assert.AreEqual(0, tree.GetCount("Mail/System"));
            Assert.AreEqual(4, tree.GetCount("Mail"), "子树外的兄弟叶子不受影响");
            Assert.AreEqual(8, tree.GetCount("Bag"));
            CollectionAssert.AreEqual(new[] { 0 }, subNotified);
            CollectionAssert.AreEqual(new[] { 4 }, mailNotified);

            // 已全零的子树再清：无变化不通知
            tree.ClearSubtree("Mail/System");
            tree.ClearSubtree("不存在的路径");
            CollectionAssert.AreEqual(new[] { 4 }, mailNotified);
        }

        // ── 调试快照 ────────────────────────────────────────────────────────

        [Test]
        public void 快照DFS先序且兄弟按名排序_输出稳定()
        {
            var tree = new RedDotTree();
            tree.SetCount("Zoo/B", 1);
            tree.SetCount("Mail/System", 2);
            tree.SetCount("Zoo/A", 3);

            string[] paths = tree.Snapshot().Select(n => n.Path).ToArray();
            CollectionAssert.AreEqual(
                new[] { "Mail", "Mail/System", "Zoo", "Zoo/A", "Zoo/B" }, paths);

            RedDotNodeInfo zoo = tree.Snapshot().First(n => n.Path == "Zoo");
            Assert.AreEqual(4, zoo.TotalCount);
            Assert.AreEqual(0, zoo.OwnCount);
            Assert.IsTrue(zoo.HasChildren);
        }
    }
}
