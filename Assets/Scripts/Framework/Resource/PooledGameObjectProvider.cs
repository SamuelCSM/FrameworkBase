using System;
using System.Collections.Generic;
using Cysharp.Threading.Tasks;
using UnityEngine;

namespace Framework
{
    /// <summary>
    /// 基于 <see cref="GameObjectPool"/> 的池化 GameObject 实例提供者。
    /// <para>
    /// 适用于 UI 局部面板、特效、可复用场景表现对象等需要频繁创建和回收的实例。
    /// </para>
    /// </summary>
    public sealed class PooledGameObjectProvider : IGameObjectProvider, IDisposable
    {
        /// <summary>池中闲置对象挂载的父节点。</summary>
        private readonly Transform poolParent;

        /// <summary>每个资源 key 的默认预分配容量。</summary>
        private readonly int defaultCapacity;

        /// <summary>每个资源 key 的最大池容量。</summary>
        private readonly int maxSize;

        /// <summary>资源 key 到对象池的映射。</summary>
        private readonly Dictionary<string, GameObjectPool> pools = new Dictionary<string, GameObjectPool>();

        /// <summary>活跃实例到来源对象池的映射，用于归还时定位池。</summary>
        private readonly Dictionary<GameObject, GameObjectPool> activePools = new Dictionary<GameObject, GameObjectPool>();

        /// <summary>
        /// 创建池化 GameObject 实例提供者。
        /// </summary>
        /// <param name="poolParent">池中闲置对象挂载的父节点，可为空。</param>
        /// <param name="defaultCapacity">每个资源 key 的默认预分配容量。</param>
        /// <param name="maxSize">每个资源 key 的最大池容量。</param>
        public PooledGameObjectProvider(Transform poolParent = null, int defaultCapacity = 10, int maxSize = 100)
        {
            this.poolParent = poolParent;
            this.defaultCapacity = Mathf.Max(0, defaultCapacity);
            this.maxSize = Mathf.Max(1, maxSize);
        }

        /// <summary>
        /// 从指定 key 对应的对象池获取实例。
        /// </summary>
        /// <param name="key">Addressables Prefab 地址，同时作为对象池 key。</param>
        /// <param name="parent">实例使用期间挂载的父节点，可为空。</param>
        /// <returns>实例获取成功时返回 GameObject，否则返回 null。</returns>
        public async UniTask<GameObject> GetAsync(string key, Transform parent = null)
        {
            if (string.IsNullOrEmpty(key))
            {
                GameLog.Error("[PooledGameObjectProvider] GetAsync 失败，key 为空");
                return null;
            }

            GameObjectPool pool = GetOrCreatePool(key);
            GameObject instance = await pool.GetAsync(parent: parent);
            if (instance != null)
            {
                activePools[instance] = pool;
            }

            return instance;
        }

        /// <summary>
        /// 归还实例到来源对象池。
        /// </summary>
        /// <param name="instance">需要归还的实例。</param>
        public void Release(GameObject instance)
        {
            if (instance == null)
            {
                return;
            }

            if (!activePools.TryGetValue(instance, out GameObjectPool pool))
            {
                GameLog.Warning($"[PooledGameObjectProvider] Release 收到未知实例，直接销毁: {instance.name}");
                UnityEngine.Object.Destroy(instance);
                return;
            }

            activePools.Remove(instance);
            pool.Release(instance);
        }

        /// <summary>
        /// 预热指定 key 对应的对象池。
        /// </summary>
        /// <param name="key">Addressables Prefab 地址，同时作为对象池 key。</param>
        /// <param name="count">预创建数量。</param>
        public async UniTask PrewarmAsync(string key, int count)
        {
            if (string.IsNullOrEmpty(key) || count <= 0)
            {
                return;
            }

            await GetOrCreatePool(key).PrewarmAsync(count);
        }

        /// <summary>
        /// 清空所有对象池并释放池内模板资源。
        /// </summary>
        public void Dispose()
        {
            foreach (GameObjectPool pool in pools.Values)
            {
                pool.Clear();
            }

            activePools.Clear();
            pools.Clear();
        }

        /// <summary>
        /// 获取或创建指定 key 的对象池。
        /// </summary>
        /// <param name="key">对象池 key。</param>
        /// <returns>对应的对象池。</returns>
        private GameObjectPool GetOrCreatePool(string key)
        {
            if (!pools.TryGetValue(key, out GameObjectPool pool))
            {
                pool = new GameObjectPool(key, poolParent, defaultCapacity, maxSize);
                pools[key] = pool;
            }

            return pool;
        }
    }
}
