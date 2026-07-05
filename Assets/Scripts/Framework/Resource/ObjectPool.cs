using System;
using System.Collections.Generic;

namespace Framework
{
    /// <summary>
    /// 通用对象池
    /// 用于管理可复用的对象，减少GC压力
    /// </summary>
    /// <typeparam name="T">对象类型</typeparam>
    public class ObjectPool<T> where T : class, new()
    {
        // 对象池栈
        private readonly Stack<T> _pool;

        // 池内对象集合，用于 O(1) 重复回收检测（仅 _checkDuplicate 为 true 时分配）
        private readonly HashSet<T> _inPoolSet;

        // 对象创建工厂
        private readonly Func<T> _createFunc;
        
        // 对象重置回调
        private readonly Action<T> _onGet;
        private readonly Action<T> _onRelease;
        
        // 对象池配置
        private readonly int _maxSize;
        private readonly bool _checkDuplicate;
        
        // 统计信息
        private int _createCount;
        private int _getCount;
        private int _releaseCount;

        /// <summary>
        /// 当前对象池中的对象数量
        /// </summary>
        public int CountInPool => _pool.Count;

        /// <summary>
        /// 已创建的对象总数
        /// </summary>
        public int CountCreated => _createCount;

        /// <summary>
        /// 当前正在使用的对象数量
        /// </summary>
        public int CountActive => _createCount - _pool.Count;

        /// <summary>
        /// 构造函数
        /// </summary>
        /// <param name="createFunc">对象创建工厂（可选，默认使用new T()）</param>
        /// <param name="onGet">对象取出时的回调（可选）</param>
        /// <param name="onRelease">对象回收时的回调（可选）</param>
        /// <param name="defaultCapacity">初始容量（默认10）</param>
        /// <param name="maxSize">最大容量（默认1000，超过则不回收）</param>
        /// <param name="checkDuplicate">是否检查重复回收（默认true）</param>
        public ObjectPool(
            Func<T> createFunc = null,
            Action<T> onGet = null,
            Action<T> onRelease = null,
            int defaultCapacity = 10,
            int maxSize = 1000,
            bool checkDuplicate = true)
        {
            _pool = new Stack<T>(defaultCapacity);
            _inPoolSet = checkDuplicate ? new HashSet<T>() : null;
            _createFunc = createFunc ?? (() => new T());
            _onGet = onGet;
            _onRelease = onRelease;
            _maxSize = maxSize;
            _checkDuplicate = checkDuplicate;
            _createCount = 0;
            _getCount = 0;
            _releaseCount = 0;
        }

        /// <summary>
        /// 从对象池获取对象
        /// </summary>
        /// <returns>对象实例</returns>
        public T Get()
        {
            T obj;
            
            if (_pool.Count > 0)
            {
                // 从池中取出
                obj = _pool.Pop();
                _inPoolSet?.Remove(obj);
            }
            else
            {
                // 创建新对象
                obj = _createFunc();
                _createCount++;
            }

            _getCount++;

            // 调用取出回调
            _onGet?.Invoke(obj);

            // 如果实现了IPoolable接口，调用OnSpawn
            if (obj is IPoolable poolable)
            {
                poolable.OnSpawn();
            }

            return obj;
        }

        /// <summary>
        /// 回收对象到对象池
        /// </summary>
        /// <param name="obj">要回收的对象</param>
        public void Release(T obj)
        {
            if (obj == null)
            {
                Logger.Warning("ObjectPool.Release: 尝试回收null对象");
                return;
            }

            // 检查重复回收（HashSet O(1)，避免对栈做 O(n) 线性扫描）
            if (_checkDuplicate && _inPoolSet.Contains(obj))
            {
                Logger.Error($"ObjectPool.Release: 对象已在池中，重复回收 - {typeof(T).Name}");
                return;
            }

            // 检查池大小限制
            if (_pool.Count >= _maxSize)
            {
                Logger.Warning($"ObjectPool.Release: 对象池已满({_maxSize})，丢弃对象 - {typeof(T).Name}");
                return;
            }

            _releaseCount++;

            // 如果实现了IPoolable接口，调用OnRecycle
            if (obj is IPoolable poolable)
            {
                poolable.OnRecycle();
            }

            // 调用回收回调
            _onRelease?.Invoke(obj);

            // 放回池中
            _pool.Push(obj);
            _inPoolSet?.Add(obj);
        }

        /// <summary>
        /// 预热对象池
        /// </summary>
        /// <param name="count">预创建的对象数量</param>
        public void Prewarm(int count)
        {
            if (count <= 0)
            {
                return;
            }

            for (int i = 0; i < count; i++)
            {
                T obj = _createFunc();
                _createCount++;
                _pool.Push(obj);
                _inPoolSet?.Add(obj);
            }

            Logger.Log($"ObjectPool.Prewarm: 预热{count}个对象 - {typeof(T).Name}");
        }

        /// <summary>
        /// 清空对象池
        /// </summary>
        public void Clear()
        {
            // 如果对象实现了IDisposable，调用Dispose
            while (_pool.Count > 0)
            {
                T obj = _pool.Pop();
                if (obj is IDisposable disposable)
                {
                    disposable.Dispose();
                }
            }

            _pool.Clear();
            _inPoolSet?.Clear();
            Logger.Log($"ObjectPool.Clear: 清空对象池 - {typeof(T).Name}");
        }

        /// <summary>
        /// 收缩对象池（移除多余的对象）
        /// </summary>
        /// <param name="targetSize">目标大小</param>
        public void Shrink(int targetSize)
        {
            if (targetSize < 0)
            {
                targetSize = 0;
            }

            int removeCount = _pool.Count - targetSize;
            if (removeCount <= 0)
            {
                return;
            }

            for (int i = 0; i < removeCount; i++)
            {
                T obj = _pool.Pop();
                _inPoolSet?.Remove(obj);
                if (obj is IDisposable disposable)
                {
                    disposable.Dispose();
                }
                _createCount--;
            }

            Logger.Log($"ObjectPool.Shrink: 收缩对象池，移除{removeCount}个对象 - {typeof(T).Name}");
        }

        /// <summary>
        /// 获取对象池统计信息
        /// </summary>
        /// <returns>统计信息字符串</returns>
        public string GetStatistics()
        {
            return $"[ObjectPool<{typeof(T).Name}>] " +
                   $"InPool: {CountInPool}, " +
                   $"Active: {CountActive}, " +
                   $"Created: {CountCreated}, " +
                   $"Get: {_getCount}, " +
                   $"Release: {_releaseCount}";
        }

        /// <summary>
        /// 打印统计信息
        /// </summary>
        public void PrintStatistics()
        {
            Logger.Log(GetStatistics());
        }
    }
}
