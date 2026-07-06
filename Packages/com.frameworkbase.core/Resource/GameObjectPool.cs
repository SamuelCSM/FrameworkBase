using System.Collections.Generic;
using UnityEngine;
using Cysharp.Threading.Tasks;

namespace Framework
{
    using Framework.Core;
	/// <summary>
	/// GameObject对象池
	/// 用于管理GameObject的复用，减少实例化和销毁的开销
	/// </summary>
	public class GameObjectPool
    {
        // 预制体地址
        private readonly string _address;
        
        // 预制体模板
        private GameObject _template;
        
        // 对象池栈
        private readonly Stack<GameObject> _pool;
        
        // 父节点
        private readonly Transform _parent;
        
        // 对象池配置
        private readonly int _maxSize;
        
        // 统计信息
        private int _createCount;
        private int _getCount;
        private int _releaseCount;
        
        // 所有活跃的对象（用于检查重复回收）
        private readonly HashSet<GameObject> _activeObjects;

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
        public int CountActive => _activeObjects.Count;

        /// <summary>
        /// 预制体地址
        /// </summary>
        public string Address => _address;

        /// <summary>
        /// 构造函数
        /// </summary>
        /// <param name="address">预制体地址</param>
        /// <param name="parent">对象池父节点（可选）</param>
        /// <param name="defaultCapacity">初始容量（默认10）</param>
        /// <param name="maxSize">最大容量（默认100）</param>
        public GameObjectPool(
            string address,
            Transform parent = null,
            int defaultCapacity = 10,
            int maxSize = 100)
        {
            _address = address;
            _parent = parent;
            _pool = new Stack<GameObject>(defaultCapacity);
            _maxSize = maxSize;
            _createCount = 0;
            _getCount = 0;
            _releaseCount = 0;
            _activeObjects = new HashSet<GameObject>();
        }

        /// <summary>
        /// 预热对象池
        /// </summary>
        /// <param name="count">预创建的对象数量</param>
        public async UniTask PrewarmAsync(int count)
        {
            if (count <= 0)
            {
                return;
            }

            // 加载预制体模板
            if (_template == null)
            {
                _template = await GameEntry.Resource.LoadAssetAsync<GameObject>(_address);
                if (_template == null)
                {
                    GameLog.Error($"GameObjectPool.PrewarmAsync: 加载预制体失败 - {_address}");
                    return;
                }
            }

            // 预创建对象
            for (int i = 0; i < count; i++)
            {
                GameObject obj = Object.Instantiate(_template, _parent);
                obj.SetActive(false);
                _pool.Push(obj);
                _createCount++;
            }

            GameLog.Log($"GameObjectPool.PrewarmAsync: 预热{count}个对象 - {_address}");
        }

        /// <summary>
        /// 从对象池获取GameObject
        /// </summary>
        /// <param name="position">位置（可选）</param>
        /// <param name="rotation">旋转（可选）</param>
        /// <param name="parent">父节点（可选）</param>
        /// <returns>GameObject实例</returns>
        public async UniTask<GameObject> GetAsync(
            Vector3? position = null,
            Quaternion? rotation = null,
            Transform parent = null)
        {
            bool requiresLiveParent = parent != null;
            if (requiresLiveParent && parent == null)
            {
                return null;
            }

            GameObject obj = null;

            if (_pool.Count > 0)
            {
                // 从池中取出
                while (_pool.Count > 0 && obj == null)
                {
                    obj = _pool.Pop();
                }
            }

            if (obj == null)
            {
                // 加载预制体模板
                if (_template == null)
                {
                    _template = await GameEntry.Resource.LoadAssetAsync<GameObject>(_address);
                    if (_template == null)
                    {
                        GameLog.Error($"GameObjectPool.GetAsync: 加载预制体失败 - {_address}");
                        return null;
                    }
                }

                if (requiresLiveParent && parent == null)
                {
                    return null;
                }

                Transform poolParent = _parent != null ? _parent : null;

                // 创建新对象
                obj = Object.Instantiate(_template, poolParent);
                _createCount++;
            }

            if (requiresLiveParent && parent == null)
            {
                Object.Destroy(obj);
                _createCount = Mathf.Max(0, _createCount - 1);
                return null;
            }

            _getCount++;

            // 设置位置和旋转
            if (position.HasValue)
            {
                obj.transform.position = position.Value;
            }
            if (rotation.HasValue)
            {
                obj.transform.rotation = rotation.Value;
            }
            if (parent != null)
            {
                obj.transform.SetParent(parent, false);
            }

            // 激活对象
            obj.SetActive(true);

            // 添加到活跃对象集合
            _activeObjects.Add(obj);

            // 调用IPoolable接口
            var poolables = obj.GetComponents<IPoolable>();
            foreach (var poolable in poolables)
            {
                poolable.OnSpawn();
            }

            return obj;
        }

        /// <summary>
        /// 回收GameObject到对象池
        /// </summary>
        /// <param name="obj">要回收的GameObject</param>
        public void Release(GameObject obj)
        {
            if (obj == null)
            {
                GameLog.Warning("GameObjectPool.Release: 尝试回收null对象");
                return;
            }

            // 检查是否是活跃对象
            if (!_activeObjects.Contains(obj))
            {
                GameLog.Error($"GameObjectPool.Release: 对象不在活跃列表中，可能已被回收 - {obj.name}");
                return;
            }

            // 从活跃对象集合中移除
            _activeObjects.Remove(obj);

            // 检查池大小限制
            if (_pool.Count >= _maxSize)
            {
                GameLog.Warning($"GameObjectPool.Release: 对象池已满({_maxSize})，销毁对象 - {obj.name}");
                Object.Destroy(obj);
                _createCount--;
                return;
            }

            _releaseCount++;

            // 调用IPoolable接口
            var poolables = obj.GetComponents<IPoolable>();
            foreach (var poolable in poolables)
            {
                poolable.OnRecycle();
            }

            // 重置对象
            obj.transform.SetParent(_parent, false);
            obj.SetActive(false);

            // 放回池中
            _pool.Push(obj);
        }

        /// <summary>
        /// 清空对象池
        /// </summary>
        public void Clear()
        {
            // 销毁池中的所有对象
            while (_pool.Count > 0)
            {
                GameObject obj = _pool.Pop();
                if (obj != null)
                {
                    Object.Destroy(obj);
                }
            }

            // 销毁所有活跃对象
            foreach (var obj in _activeObjects)
            {
                if (obj != null)
                {
                    Object.Destroy(obj);
                }
            }

            _pool.Clear();
            _activeObjects.Clear();
            _createCount = 0;

            // 释放预制体模板
            if (_template != null)
            {
                GameEntry.Resource.ReleaseAsset(_address);
                _template = null;
            }

            GameLog.Log($"GameObjectPool.Clear: 清空对象池 - {_address}");
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
                GameObject obj = _pool.Pop();
                if (obj != null)
                {
                    Object.Destroy(obj);
                }
                _createCount--;
            }

            GameLog.Log($"GameObjectPool.Shrink: 收缩对象池，移除{removeCount}个对象 - {_address}");
        }

        /// <summary>
        /// 获取对象池统计信息
        /// </summary>
        /// <returns>统计信息字符串</returns>
        public string GetStatistics()
        {
            return $"[GameObjectPool: {_address}] " +
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
            GameLog.Log(GetStatistics());
        }
    }
}
