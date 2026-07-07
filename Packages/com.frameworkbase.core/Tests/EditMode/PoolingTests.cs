using Framework.Pooling;
using NUnit.Framework;

namespace Framework.Tests
{
    public class PoolingTests
    {
        private sealed class Poolable : IPoolable
        {
            public int SpawnCount;
            public int RecycleCount;

            public void OnSpawn() => SpawnCount++;
            public void OnRecycle() => RecycleCount++;
        }

        [Test]
        public void ObjectPool_ReusesReleasedInstance()
        {
            var pool = new ObjectPool<Poolable>(() => new Poolable());

            Poolable first = pool.Get();
            pool.Release(first);
            Poolable second = pool.Get();

            Assert.AreSame(first, second);
            Assert.AreEqual(2, second.SpawnCount);
            Assert.AreEqual(1, second.RecycleCount);
        }

        [Test]
        public void ObjectPool_DuplicateRelease_IsRejected()
        {
            var pool = new ObjectPool<object>(() => new object());
            object item = pool.Get();

            pool.Release(item);
            UnityEngine.TestTools.LogAssert.ignoreFailingMessages = true;
            pool.Release(item);
            UnityEngine.TestTools.LogAssert.ignoreFailingMessages = false;

            Assert.AreEqual(1, pool.CountInPool);
        }
    }
}
