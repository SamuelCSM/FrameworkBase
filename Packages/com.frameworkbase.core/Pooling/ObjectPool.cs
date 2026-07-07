using System;
using System.Collections.Generic;

namespace Framework.Pooling
{
    /// <summary>
    /// 面向引用类型的通用对象池。
    /// 统一处理重复回收检测、生命周期回调、预热/收缩以及基础统计信息。
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

        /// <summary>当前池内可复用对象数量。</summary>
        public int CountInPool => _pool.Count;

        /// <summary>对象池累计创建的对象数量。</summary>
        public int CountCreated => _createCount;

        /// <summary>估算的已取出对象数量。</summary>
        public int CountActive => _createCount - _pool.Count;

        /// <summary>
        /// 创建对象池。
        /// </summary>
        /// <param name="createFunc">池为空时使用的对象创建工厂。</param>
        /// <param name="onGet">对象取出后的回调。</param>
        /// <param name="onRelease">对象放回池前的回调。</param>
        /// <param name="defaultCapacity">对象池初始容量。</param>
        /// <param name="maxSize">对象池最多保留的对象数量。</param>
        /// <param name="checkDuplicate">是否通过 HashSet 以 O(1) 成本拒绝重复回收。</param>
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

        /// <summary>从对象池取出对象；池为空时创建新对象。</summary>
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

        /// <summary>将对象回收到对象池。</summary>
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

        /// <summary>预先创建对象并放入对象池。</summary>
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

        /// <summary>释放池内对象并清空对象池；已取出的对象不受影响。</summary>
        public void Clear()
        {
            while (_pool.Count > 0)
                DisposeIfNeeded(_pool.Pop());

            _pool.Clear();
            _inPoolSet?.Clear();
            GameLog.Log($"ObjectPool.Clear: 清空对象池 - {typeof(T).Name}");
        }

        /// <summary>移除多余池内对象，直到对象池达到目标大小。</summary>
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

        /// <summary>返回便于阅读的对象池统计信息。</summary>
        public string GetStatistics()
        {
            return $"[ObjectPool<{typeof(T).Name}>] " +
                   $"InPool: {CountInPool}, " +
                   $"Active: {CountActive}, " +
                   $"Created: {CountCreated}, " +
                   $"Get: {_getCount}, " +
                   $"Release: {_releaseCount}";
        }

        /// <summary>通过 <see cref="GameLog"/> 输出对象池统计信息。</summary>
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
