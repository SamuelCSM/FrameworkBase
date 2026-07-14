using System.Collections.Generic;
using Framework.Network;
using NUnit.Framework;

namespace Framework.Tests
{
    /// <summary>
    /// 断线待发队列单测：入队上限、FIFO 补发、TTL 过期收尾、放弃时全部失败、
    /// 回调异常隔离、补发中再入队不撞遍历。纯逻辑，不碰真实网络。
    /// </summary>
    public class OfflineRequestQueueTests
    {
        [Test]
        public void 入队_超过上限拒绝()
        {
            var queue = new OfflineRequestQueue { MaxItems = 2 };

            Assert.IsTrue(queue.TryEnqueue(() => { }, () => { }, 30, now: 0, isReplaySafe: true));
            Assert.IsTrue(queue.TryEnqueue(() => { }, () => { }, 30, now: 0, isReplaySafe: true));
            Assert.IsFalse(queue.TryEnqueue(() => { }, () => { }, 30, now: 0, isReplaySafe: true), "超限必须拒绝，断网请求洪水不能无界积压");
            Assert.AreEqual(2, queue.Count);
        }

        [Test]
        public void 入队_空回调拒绝()
        {
            var queue = new OfflineRequestQueue();

            Assert.IsFalse(queue.TryEnqueue(null, () => { }, 30, 0, isReplaySafe: true));
            Assert.IsFalse(queue.TryEnqueue(() => { }, null, 30, 0, isReplaySafe: true));
            Assert.AreEqual(0, queue.Count);
        }

        [Test]
        public void FlushAll_按入队顺序补发并清空()
        {
            var queue = new OfflineRequestQueue();
            var order = new List<int>();
            queue.TryEnqueue(() => order.Add(1), () => { }, 30, 0, isReplaySafe: true);
            queue.TryEnqueue(() => order.Add(2), () => { }, 30, 0, isReplaySafe: true);
            queue.TryEnqueue(() => order.Add(3), () => { }, 30, 0, isReplaySafe: true);

            queue.FlushAll();

            CollectionAssert.AreEqual(new[] { 1, 2, 3 }, order, "补发必须保持 FIFO（业务请求可能有先后依赖）");
            Assert.AreEqual(0, queue.Count);

            queue.FlushAll(); // 空队列幂等
            CollectionAssert.AreEqual(new[] { 1, 2, 3 }, order);
        }

        [Test]
        public void TTL过期_按失败收尾并移除_未到期保留()
        {
            var queue = new OfflineRequestQueue();
            bool failedShort = false, failedLong = false;
            queue.TryEnqueue(() => { }, () => failedShort = true, ttlSeconds: 5, now: 100, isReplaySafe: true);
            queue.TryEnqueue(() => { }, () => failedLong = true, ttlSeconds: 60, now: 100, isReplaySafe: true);

            queue.Update(now: 104);
            Assert.IsFalse(failedShort, "未到期不收尾");
            Assert.AreEqual(2, queue.Count);

            queue.Update(now: 106);
            Assert.IsTrue(failedShort, "TTL 到期必须失败收尾（玩家早离开界面了，迟到补发只造成脏数据）");
            Assert.IsFalse(failedLong);
            Assert.AreEqual(1, queue.Count);
        }

        [Test]
        public void FailAll_全部失败收尾并清空()
        {
            var queue = new OfflineRequestQueue();
            int failed = 0, sent = 0;
            queue.TryEnqueue(() => sent++, () => failed++, 30, 0, isReplaySafe: true);
            queue.TryEnqueue(() => sent++, () => failed++, 30, 0, isReplaySafe: true);

            queue.FailAll();

            Assert.AreEqual(2, failed);
            Assert.AreEqual(0, sent, "FailAll 不得触发补发");
            Assert.AreEqual(0, queue.Count);
        }

        [Test]
        public void 回调异常_逐项隔离不阻断后续()
        {
            var queue = new OfflineRequestQueue();
            bool secondSent = false;
            queue.TryEnqueue(() => throw new System.InvalidOperationException("boom"), () => { }, 30, 0, isReplaySafe: true);
            queue.TryEnqueue(() => secondSent = true, () => { }, 30, 0, isReplaySafe: true);

            UnityEngine.TestTools.LogAssert.Expect(
                UnityEngine.LogType.Error,
                new System.Text.RegularExpressions.Regex(@"\[OfflineRequestQueue\] 补发回调异常"));
            Assert.DoesNotThrow(() => queue.FlushAll());
            Assert.IsTrue(secondSent, "首项回调炸了不得影响后续项补发");
        }

        [Test]
        public void 补发过程中再入队_不撞遍历且留在队列()
        {
            var queue = new OfflineRequestQueue();
            // 模拟"补发瞬间又断线 → 请求再次入队"的重入场景
            queue.TryEnqueue(
                () => queue.TryEnqueue(() => { }, () => { }, 30, 0, isReplaySafe: true),
                () => { }, 30, 0, isReplaySafe: true);

            Assert.DoesNotThrow(() => queue.FlushAll());
            Assert.AreEqual(1, queue.Count, "补发中新入队的项应留待下一次 Flush");
        }

        [Test]
        public void 非幂等请求_即使调用方要求排队也拒绝进入重放队列()
        {
            var queue = new OfflineRequestQueue();
            Assert.IsFalse(queue.TryEnqueue(
                () => { }, () => { }, 30, 0, isReplaySafe: false));
            Assert.AreEqual(0, queue.Count);

            var config = new NetworkRequestConfig { QueueWhileDisconnected = true };
            Assert.IsFalse(config.IsOfflineReplayAllowed);
            config.ReplaySafety = NetworkReplaySafety.ReadOnly;
            Assert.IsTrue(config.IsOfflineReplayAllowed);
        }
    }
}
