using System;
using System.Collections.Generic;
using Cysharp.Threading.Tasks;
using UnityEngine;

namespace Framework
{
    /// <summary>
    /// 场景加载型子预制缓存池，统一管理 <see cref="SceneSubPrefabHost{TPrefab}"/> 的租借、归还和预热。
    /// </summary>
    /// <typeparam name="TPrefab">子预制控制类类型。</typeparam>
    public sealed class SceneSubPrefabPool<TPrefab> : IDisposable
        where TPrefab : SceneSubPrefabCore, new()
    {
        /// <summary>共享池化实例提供者，负责真正的 GameObject 实例缓存。</summary>
        private readonly PooledGameObjectProvider provider;

        /// <summary>当前租借出去的 Host 集合，释放池时统一回收。</summary>
        private readonly HashSet<SceneSubPrefabHost<TPrefab>> activeHosts = new HashSet<SceneSubPrefabHost<TPrefab>>();

        /// <summary>是否已经释放。</summary>
        private bool disposed;

        /// <summary>
        /// 创建场景加载型子预制缓存池。
        /// </summary>
        /// <param name="poolParent">池中闲置对象挂载父节点，可为空。</param>
        /// <param name="defaultCapacity">每个资源 key 的默认预分配容量。</param>
        /// <param name="maxSize">每个资源 key 的最大池容量。</param>
        public SceneSubPrefabPool(Transform poolParent = null, int defaultCapacity = 0, int maxSize = 100)
        {
            provider = new PooledGameObjectProvider(poolParent, defaultCapacity, maxSize);
        }

        /// <summary>
        /// 加载并租借一个场景子预制 Host。
        /// </summary>
        /// <param name="key">Addressables Prefab 地址，同时作为对象池 key。</param>
        /// <param name="parent">子预制使用期间的挂载父节点。</param>
        /// <returns>加载成功时返回 Host，否则返回 null。</returns>
        public async UniTask<SceneSubPrefabHost<TPrefab>> LoadAsync(string key, Transform parent)
        {
            if (disposed)
            {
                GameLog.Warning($"[SceneSubPrefabPool] 已释放的缓存池不能加载: {typeof(TPrefab).Name}");
                return null;
            }

            var host = new SceneSubPrefabHost<TPrefab>(key, parent, provider);
            TPrefab prefab = await host.LoadAsync();
            if (prefab == null)
            {
                host.Dispose();
                return null;
            }

            activeHosts.Add(host);
            return host;
        }

        /// <summary>
        /// 加载、显示并租借一个场景子预制 Host。
        /// </summary>
        /// <param name="key">Addressables Prefab 地址，同时作为对象池 key。</param>
        /// <param name="parent">子预制使用期间的挂载父节点。</param>
        /// <param name="userData">本次显示传入的业务数据，可为空。</param>
        /// <returns>显示成功时返回 Host，否则返回 null。</returns>
        public async UniTask<SceneSubPrefabHost<TPrefab>> ShowAsync(string key, Transform parent, object userData = null)
        {
            SceneSubPrefabHost<TPrefab> host = await LoadAsync(key, parent);
            host?.Prefab?.Show(userData);
            return host;
        }

        /// <summary>
        /// 归还一个已租借的场景子预制 Host，底层实例会回到共享对象池。
        /// </summary>
        /// <param name="host">待归还 Host。</param>
        public void Release(SceneSubPrefabHost<TPrefab> host)
        {
            if (host == null)
            {
                return;
            }

            activeHosts.Remove(host);
            host.Dispose();
        }

        /// <summary>
        /// 预热指定 key 对应的对象池。
        /// </summary>
        /// <param name="key">Addressables Prefab 地址，同时作为对象池 key。</param>
        /// <param name="count">预创建数量。</param>
        /// <returns>预热任务。</returns>
        public UniTask PrewarmAsync(string key, int count)
        {
            return disposed ? UniTask.CompletedTask : provider.PrewarmAsync(key, count);
        }

        /// <summary>
        /// 释放所有已租借 Host，并清空底层对象池。
        /// </summary>
        public void Dispose()
        {
            if (disposed)
            {
                return;
            }

            disposed = true;
            foreach (SceneSubPrefabHost<TPrefab> host in activeHosts)
            {
                host?.Dispose();
            }

            activeHosts.Clear();
            provider.Dispose();
        }
    }
}
