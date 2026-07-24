using Framework.Popup;
using NUnit.Framework;

namespace Framework.Tests.Modules
{
    /// <summary>
    /// 弹窗队列纯核单测：验证"一次一个、优先级降序、同级 FIFO、Key 去重"四项核心语义，
    /// 不碰 Presenter/UniTask（异步泵属集成侧，靠真机/集成验证）。
    /// </summary>
    public class PopupQueueTests
    {
        private static PopupRequest Req(string key, int priority = 0, bool unique = true)
            => new PopupRequest { Key = key, Priority = priority, Unique = unique };

        [Test]
        public void 空队列_不激活()
        {
            var q = new PopupQueue();
            Assert.IsFalse(q.TryActivateNext(out PopupRequest next));
            Assert.IsNull(next);
            Assert.IsFalse(q.IsShowing);
        }

        [Test]
        public void 单请求_激活到完成再排空()
        {
            var q = new PopupQueue();
            Assert.IsTrue(q.Enqueue(Req("a")));
            Assert.AreEqual(1, q.PendingCount);

            Assert.IsTrue(q.TryActivateNext(out PopupRequest next));
            Assert.AreEqual("a", next.Key);
            Assert.IsTrue(q.IsShowing);
            Assert.AreEqual(0, q.PendingCount);

            // 展示中不再激活下一个（一次一个）
            Assert.IsFalse(q.TryActivateNext(out _));

            q.CompleteCurrent();
            Assert.IsFalse(q.IsShowing);
            Assert.IsFalse(q.TryActivateNext(out _), "队列已空");
        }

        [Test]
        public void 高优先级先出()
        {
            var q = new PopupQueue();
            q.Enqueue(Req("low", 1));
            q.Enqueue(Req("high", 10));
            q.Enqueue(Req("mid", 5));

            q.TryActivateNext(out PopupRequest first);
            q.CompleteCurrent();
            q.TryActivateNext(out PopupRequest second);
            q.CompleteCurrent();
            q.TryActivateNext(out PopupRequest third);

            Assert.AreEqual("high", first.Key);
            Assert.AreEqual("mid", second.Key);
            Assert.AreEqual("low", third.Key);
        }

        [Test]
        public void 同优先级_FIFO()
        {
            var q = new PopupQueue();
            q.Enqueue(Req("first", 5));
            q.Enqueue(Req("second", 5));
            q.Enqueue(Req("third", 5));

            q.TryActivateNext(out PopupRequest a);
            q.CompleteCurrent();
            q.TryActivateNext(out PopupRequest b);
            q.CompleteCurrent();
            q.TryActivateNext(out PopupRequest c);

            Assert.AreEqual("first", a.Key);
            Assert.AreEqual("second", b.Key);
            Assert.AreEqual("third", c.Key, "同优先级必须按入队先后");
        }

        [Test]
        public void Unique去重_队列中同Key_择优保留()
        {
            var q = new PopupQueue();
            Assert.IsTrue(q.Enqueue(Req("reward", 1)));
            // 同 Key 更低优先级 → 丢弃新的
            Assert.IsFalse(q.Enqueue(Req("reward", 0)));
            Assert.AreEqual(1, q.PendingCount, "去重后仍只有一条");

            // 同 Key 更高优先级 → 替换
            Assert.IsTrue(q.Enqueue(Req("reward", 9)));
            Assert.AreEqual(1, q.PendingCount);

            q.TryActivateNext(out PopupRequest next);
            Assert.AreEqual(9, next.Priority, "应保留更高优先级那条");
        }

        [Test]
        public void Unique去重_同Key正在展示_丢弃()
        {
            var q = new PopupQueue();
            q.Enqueue(Req("levelup", 1));
            q.TryActivateNext(out _); // levelup 正在展示

            Assert.IsFalse(q.Enqueue(Req("levelup", 5)), "同 Key 正在展示应丢弃，避免叠加");
            Assert.AreEqual(0, q.PendingCount);
        }

        [Test]
        public void 非Unique_允许同Key重复()
        {
            var q = new PopupQueue();
            Assert.IsTrue(q.Enqueue(Req("toast", 0, unique: false)));
            Assert.IsTrue(q.Enqueue(Req("toast", 0, unique: false)));
            Assert.AreEqual(2, q.PendingCount, "非去重请求允许重复入队");
        }

        [Test]
        public void 空Key_不参与去重()
        {
            var q = new PopupQueue();
            Assert.IsTrue(q.Enqueue(Req(null)));
            Assert.IsTrue(q.Enqueue(Req(null)));
            Assert.AreEqual(2, q.PendingCount, "空 Key 无法去重，按普通请求入队");
        }

        [Test]
        public void ClearPending_清待展示不动当前()
        {
            var q = new PopupQueue();
            q.Enqueue(Req("a", 5));
            q.Enqueue(Req("b", 1));
            q.TryActivateNext(out PopupRequest cur); // a 正在展示，b 待展示

            q.ClearPending();
            Assert.AreEqual(0, q.PendingCount);
            Assert.IsTrue(q.IsShowing, "ClearPending 不影响正在展示者");
            Assert.AreEqual("a", cur.Key);
        }

        [Test]
        public void Reset_彻底复位()
        {
            var q = new PopupQueue();
            q.Enqueue(Req("a", 5));
            q.TryActivateNext(out _);
            q.Enqueue(Req("b", 1));

            q.Reset();
            Assert.IsFalse(q.IsShowing);
            Assert.AreEqual(0, q.PendingCount);
        }
    }
}
