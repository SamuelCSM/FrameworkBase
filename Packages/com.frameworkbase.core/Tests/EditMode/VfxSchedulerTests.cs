using System.Collections.Generic;
using Framework;
using NUnit.Framework;

namespace Framework.Tests
{
    /// <summary>
    /// 特效调度纯核单测：验证"预算超限丢弃、按时长过期、手动项不过期、Stop 移除"。
    /// 取还/跟随属 MonoBehaviour 集成路径，靠真机/集成验证。
    /// </summary>
    public class VfxSchedulerTests
    {
        [Test]
        public void 预算内_递增id_预算满_丢弃()
        {
            var s = new VfxScheduler(budget: 2);
            int a = s.TryRegister(1f);
            int b = s.TryRegister(1f);
            Assert.Greater(a, 0);
            Assert.Greater(b, 0);
            Assert.AreNotEqual(a, b, "id 递增唯一");
            Assert.AreEqual(2, s.ActiveCount);

            int c = s.TryRegister(1f);
            Assert.AreEqual(0, c, "满预算应丢弃（返回 0）");
            Assert.AreEqual(2, s.ActiveCount, "丢弃不占坑");
        }

        [Test]
        public void 预算0_不限()
        {
            var s = new VfxScheduler(budget: 0);
            for (int i = 0; i < 100; i++)
                Assert.Greater(s.TryRegister(1f), 0);
            Assert.AreEqual(100, s.ActiveCount);
        }

        [Test]
        public void Tick_按时长过期()
        {
            var s = new VfxScheduler(budget: 0);
            int a = s.TryRegister(1.0f);
            int b = s.TryRegister(0.5f);
            var expired = new List<int>();

            s.Tick(0.6f, expired);
            Assert.AreEqual(1, expired.Count, "0.5s 的到期，1.0s 的还在");
            Assert.AreEqual(b, expired[0]);
            Assert.AreEqual(1, s.ActiveCount);

            s.Tick(0.6f, expired);
            Assert.AreEqual(1, expired.Count, "累计 1.2s，1.0s 的到期");
            Assert.AreEqual(a, expired[0]);
            Assert.AreEqual(0, s.ActiveCount);
        }

        [Test]
        public void 手动项_不过期()
        {
            var s = new VfxScheduler(budget: 0);
            int m = s.TryRegister(-1f); // 手动特效（持续光环等）
            var expired = new List<int>();

            s.Tick(9999f, expired);
            Assert.IsEmpty(expired, "手动特效不因时长过期");
            Assert.AreEqual(1, s.ActiveCount);

            Assert.IsTrue(s.Remove(m), "只能被 Stop 移除");
            Assert.AreEqual(0, s.ActiveCount);
        }

        [Test]
        public void Remove_无效id_返回false()
        {
            var s = new VfxScheduler(budget: 0);
            Assert.IsFalse(s.Remove(999));
        }
    }
}
