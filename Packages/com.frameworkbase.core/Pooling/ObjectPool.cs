using System;
using System.Collections.Generic;

namespace Framework.Pooling
{
    /// <summary>
    /// General-purpose object pool for reference types.
    /// It centralizes duplicate-release checks, lifecycle callbacks, prewarm/shrink behavior, and simple statistics.
    /// </summary>
    public class ObjectPool<T> where T : class
    {
        private readonly Stack<T> _pool;
        private readonly HashSet<T> _inPoolSet;
        private readonly Func<T> _createFunc;
        private readonly Action<T> _onGet;
        private readonly Action<T> _onRelease;
        private readonly int _maxSize;
        private readonly bool _checkDuplicate;

        private int _createCount;
        private int _getCount;
        private int _releaseCount;

        /// <summary>Current number of objects available in the pool.</summary>
        public int CountInPool => _pool.Count;

        /// <summary>Total number of objects created by this pool.</summary>
        public int CountCreated => _createCount;

        /// <summary>Estimated number of checked-out objects.</summary>
        public int CountActive => _createCount - _pool.Count;

        /// <summary>
        /// Create a pool.
        /// </summary>
        /// <param name="createFunc">Factory used when the pool is empty.</param>
        /// <param name="onGet">Callback invoked after an item is checked out.</param>
        /// <param name="onRelease">Callback invoked before an item is stored back in the pool.</param>
        /// <param name="defaultCapacity">Initial stack capacity.</param>
        /// <param name="maxSize">Maximum number of objects retained in the pool.</param>
        /// <param name="checkDuplicate">Whether duplicate releases are rejected in O(1) through a HashSet.</param>
        public ObjectPool(
            Func<T> createFunc,
            Action<T> onGet = null,
            Action<T> onRelease = null,
            int defaultCapacity = 10,
            int maxSize = 1000,
            bool checkDuplicate = true)
        {
            if (createFunc == null)
                throw new ArgumentNullException(nameof(createFunc));

            _pool = new Stack<T>(Math.Max(0, defaultCapacity));
            _inPoolSet = checkDuplicate ? new HashSet<T>() : null;
            _createFunc = createFunc;
            _onGet = onGet;
            _onRelease = onRelease;
            _maxSize = Math.Max(0, maxSize);
            _checkDuplicate = checkDuplicate;
        }

        /// <summary>Get an object from the pool, creating a new one if needed.</summary>
        public T Get()
        {
            T obj = _pool.Count > 0 ? _pool.Pop() : CreateNew();
            _inPoolSet?.Remove(obj);

            _getCount++;
            _onGet?.Invoke(obj);
            if (obj is IPoolable poolable)
                poolable.OnSpawn();

            return obj;
        }

        /// <summary>Release an object back to the pool.</summary>
        public void Release(T obj)
        {
            if (obj == null)
            {
                GameLog.Warning("ObjectPool.Release: 尝试回收 null 对象");
                return;
            }

            if (_checkDuplicate && _inPoolSet.Contains(obj))
            {
                GameLog.Error($"ObjectPool.Release: 对象已在池中，重复回收 - {typeof(T).Name}");
                return;
            }

            if (_pool.Count >= _maxSize)
            {
                GameLog.Warning($"ObjectPool.Release: 对象池已满({_maxSize})，丢弃对象 - {typeof(T).Name}");
                DisposeIfNeeded(obj);
                return;
            }

            _releaseCount++;
            if (obj is IPoolable poolable)
                poolable.OnRecycle();

            _onRelease?.Invoke(obj);
            _pool.Push(obj);
            _inPoolSet?.Add(obj);
        }

        /// <summary>Pre-create objects and store them in the pool.</summary>
        public void Prewarm(int count)
        {
            if (count <= 0)
                return;

            for (int i = 0; i < count; i++)
            {
                T obj = CreateNew();
                _pool.Push(obj);
                _inPoolSet?.Add(obj);
            }

            GameLog.Log($"ObjectPool.Prewarm: 预热 {count} 个对象 - {typeof(T).Name}");
        }

        /// <summary>Dispose pooled items and clear the pool. Checked-out items are not touched.</summary>
        public void Clear()
        {
            while (_pool.Count > 0)
                DisposeIfNeeded(_pool.Pop());

            _pool.Clear();
            _inPoolSet?.Clear();
            GameLog.Log($"ObjectPool.Clear: 清空对象池 - {typeof(T).Name}");
        }

        /// <summary>Remove pooled items until the pool reaches the target size.</summary>
        public void Shrink(int targetSize)
        {
            int normalizedTarget = Math.Max(0, targetSize);
            int removeCount = _pool.Count - normalizedTarget;
            if (removeCount <= 0)
                return;

            for (int i = 0; i < removeCount; i++)
            {
                T obj = _pool.Pop();
                _inPoolSet?.Remove(obj);
                DisposeIfNeeded(obj);
                _createCount--;
            }

            GameLog.Log($"ObjectPool.Shrink: 收缩对象池，移除 {removeCount} 个对象 - {typeof(T).Name}");
        }

        /// <summary>Return human-readable pool statistics.</summary>
        public string GetStatistics()
        {
            return $"[ObjectPool<{typeof(T).Name}>] " +
                   $"InPool: {CountInPool}, " +
                   $"Active: {CountActive}, " +
                   $"Created: {CountCreated}, " +
                   $"Get: {_getCount}, " +
                   $"Release: {_releaseCount}";
        }

        /// <summary>Log pool statistics through <see cref="GameLog"/>.</summary>
        public void PrintStatistics()
        {
            GameLog.Log(GetStatistics());
        }

        private T CreateNew()
        {
            _createCount++;
            return _createFunc();
        }

        private static void DisposeIfNeeded(T obj)
        {
            if (obj is IDisposable disposable)
                disposable.Dispose();
        }
    }
}
