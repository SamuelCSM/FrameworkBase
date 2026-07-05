using Framework;
using NUnit.Framework;
using UnityEngine.TestTools;

namespace Framework.Tests
{
    /// <summary>
    /// ObjectPool 通用对象池单元测试。
    /// 重点覆盖复用、O(1) 重复回收检测、容量上限、预热/收缩与 IPoolable 生命周期回调。
    /// </summary>
    public class ObjectPoolTests
    {
        /// <summary>可被池化的测试对象，记录 Spawn/Recycle 调用次数。</summary>
        private sealed class Poolable : IPoolable
        {
            public int SpawnCount;
            public int RecycleCount;

            public void OnSpawn() => SpawnCount++;
            public void OnRecycle() => RecycleCount++;
        }

        /// <summary>空池取对象会新建，回收后再取应复用同一实例。</summary>
        [Test]
        public void GetReleaseGet_ReusesSameInstance()
        {
            var pool = new ObjectPool<object>();

            object a = pool.Get();
            Assert.AreEqual(1, pool.CountCreated);
            Assert.AreEqual(1, pool.CountActive);

            pool.Release(a);
            Assert.AreEqual(1, pool.CountInPool);

            object b = pool.Get();
            Assert.AreSame(a, b, "回收后应复用同一实例");
            Assert.AreEqual(1, pool.CountCreated, "复用不应新建对象");
        }

        /// <summary>重复回收同一对象应被拦截（记录一条错误日志），池内数量不翻倍。</summary>
        [Test]
        public void Release_DuplicateObject_IsRejected()
        {
            var pool = new ObjectPool<object>();
            object a = pool.Get();

            pool.Release(a);
            LogAssert.ignoreFailingMessages = true; // 第二次回收会主动记录 Error，属预期
            pool.Release(a);
            LogAssert.ignoreFailingMessages = false;

            Assert.AreEqual(1, pool.CountInPool, "重复回收不应使池内数量增加");
        }

        /// <summary>预热应按数量预创建对象并放入池中。</summary>
        [Test]
        public void Prewarm_PrecreatesObjects()
        {
            var pool = new ObjectPool<object>();
            pool.Prewarm(5);

            Assert.AreEqual(5, pool.CountInPool);
            Assert.AreEqual(5, pool.CountCreated);
        }

        /// <summary>超过 maxSize 的回收对象应被丢弃，不进入池。</summary>
        [Test]
        public void Release_BeyondMaxSize_DiscardsObject()
        {
            var pool = new ObjectPool<object>(maxSize: 1, checkDuplicate: false);

            pool.Release(new object());
            LogAssert.ignoreFailingMessages = true; // 满池丢弃会记录 Warning
            pool.Release(new object());
            LogAssert.ignoreFailingMessages = false;

            Assert.AreEqual(1, pool.CountInPool, "超出上限的对象应被丢弃");
        }

        /// <summary>IPoolable 的 OnSpawn/OnRecycle 应在取出/回收时分别触发。</summary>
        [Test]
        public void PoolableLifecycle_HooksInvoked()
        {
            var pool = new ObjectPool<Poolable>();

            Poolable obj = pool.Get();
            Assert.AreEqual(1, obj.SpawnCount);
            Assert.AreEqual(0, obj.RecycleCount);

            pool.Release(obj);
            Assert.AreEqual(1, obj.RecycleCount);

            pool.Get();
            Assert.AreEqual(2, obj.SpawnCount, "再次取出应再次触发 OnSpawn");
        }

        /// <summary>收缩应移除多余对象到目标数量。</summary>
        [Test]
        public void Shrink_RemovesExcessToTarget()
        {
            var pool = new ObjectPool<object>();
            pool.Prewarm(10);

            pool.Shrink(3);

            Assert.AreEqual(3, pool.CountInPool);
        }

        /// <summary>onGet/onRelease 回调应在取出/回收时被调用。</summary>
        [Test]
        public void GetReleaseCallbacks_AreInvoked()
        {
            int got = 0, released = 0;
            var pool = new ObjectPool<object>(onGet: _ => got++, onRelease: _ => released++);

            object a = pool.Get();
            pool.Release(a);

            Assert.AreEqual(1, got);
            Assert.AreEqual(1, released);
        }
    }
}
