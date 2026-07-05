using Cysharp.Threading.Tasks;
using UnityEngine;

namespace Framework
{
    /// <summary>
    /// 运行时加载型场景子预制宿主。
    /// </summary>
    /// <typeparam name="TPrefab">子预制控制类类型。</typeparam>
    public class SceneSubPrefabHost<TPrefab> : System.IDisposable
        where TPrefab : SceneSubPrefabCore, new()
    {
        /// <summary>子预制资源或对象池 key。</summary>
        private readonly string key;

        /// <summary>子预制挂载父节点。</summary>
        private readonly Transform parent;

        /// <summary>子预制实例提供者。</summary>
        private readonly IGameObjectProvider provider;

        /// <summary>当前加载出的子预制根对象。</summary>
        private GameObject prefabObject;

        /// <summary>当前创建的子预制控制类。</summary>
        private TPrefab prefab;

        /// <summary>
        /// 创建运行时加载型场景子预制宿主。
        /// </summary>
        /// <param name="key">子预制资源地址、对象池 key 或其他实例来源标识。</param>
        /// <param name="parent">子预制挂载父节点。</param>
        /// <param name="provider">实例提供者，传空时默认使用 Addressables。</param>
        public SceneSubPrefabHost(string key, Transform parent, IGameObjectProvider provider = null)
        {
            this.key = key;
            this.parent = parent;
            this.provider = provider ?? new AddressableGameObjectProvider();
        }

        /// <summary>当前是否已经加载出子预制实例。</summary>
        public bool IsLoaded => prefab != null && prefabObject != null;

        /// <summary>当前子预制控制类，未加载时为 null。</summary>
        public TPrefab Prefab => prefab;

        /// <summary>当前子预制根对象，未加载时为 null。</summary>
        public GameObject PrefabObject => prefabObject;

        /// <summary>
        /// 加载子预制但不显示。
        /// </summary>
        /// <returns>加载成功时返回子预制控制类，否则返回 null。</returns>
        public async UniTask<TPrefab> LoadAsync()
        {
            if (string.IsNullOrEmpty(key))
            {
                Logger.Error($"[SceneSubPrefabHost] LoadAsync 失败，key 为空: {typeof(TPrefab).Name}");
                return null;
            }

            if (parent == null)
            {
                Logger.Error($"[SceneSubPrefabHost] LoadAsync 失败，父节点为空: {typeof(TPrefab).Name}");
                return null;
            }

            if (IsLoaded)
            {
                return prefab;
            }

            Dispose();

            prefabObject = await provider.GetAsync(key, parent);
            if (prefabObject == null)
            {
                ResetRuntimeState();
                Logger.Error($"[SceneSubPrefabHost] 加载子预制失败: {typeof(TPrefab).Name}, Key={key}");
                return null;
            }

            prefab = new TPrefab();
            SceneSubView view = prefabObject.GetComponent(prefab.ViewType) as SceneSubView;
            if (view == null)
            {
                Logger.Error($"[SceneSubPrefabHost] 子预制缺少 View 组件: {prefab.ViewType.Name}, Key={key}");
                prefab.Dispose();
                prefab = null;
                ReleasePrefabObject();
                ResetRuntimeState();
                return null;
            }

            prefab.Initialize(view);
            if (!prefab.IsInitialized)
            {
                prefab.Dispose();
                prefab = null;
                ReleasePrefabObject();
                ResetRuntimeState();
                return null;
            }

            prefabObject.SetActive(false);
            return prefab;
        }

        /// <summary>
        /// 加载并显示子预制。
        /// </summary>
        /// <param name="userData">本次显示传入的业务数据，可为空。</param>
        /// <returns>显示成功时返回子预制控制类，否则返回 null。</returns>
        public async UniTask<TPrefab> ShowAsync(object userData = null)
        {
            TPrefab loadedPrefab = await LoadAsync();
            loadedPrefab?.Show(userData);
            return loadedPrefab;
        }

        /// <summary>
        /// 隐藏当前子预制。
        /// </summary>
        public void Hide()
        {
            prefab?.Hide();
        }

        /// <summary>
        /// 释放当前子预制控制类与资源实例。
        /// </summary>
        public void Dispose()
        {
            prefab?.Dispose();
            prefab = null;
            ReleasePrefabObject();
            ResetRuntimeState();
        }

        /// <summary>
        /// 释放当前加载出的子预制根对象。
        /// </summary>
        private void ReleasePrefabObject()
        {
            if (prefabObject == null)
            {
                return;
            }

            provider.Release(prefabObject);
        }

        /// <summary>
        /// 重置宿主运行时状态。
        /// </summary>
        private void ResetRuntimeState()
        {
            prefabObject = null;
        }
    }

    /// <summary>
    /// Addressables 场景子预制宿主。
    /// </summary>
    /// <typeparam name="TPrefab">子预制控制类类型。</typeparam>
    public sealed class AddressableSceneSubPrefabHost<TPrefab> : SceneSubPrefabHost<TPrefab>
        where TPrefab : SceneSubPrefabCore, new()
    {
        /// <summary>
        /// 创建 Addressables 场景子预制宿主。
        /// </summary>
        /// <param name="key">Addressables Prefab 地址。</param>
        /// <param name="parent">子预制挂载父节点。</param>
        public AddressableSceneSubPrefabHost(string key, Transform parent) : base(key, parent, new AddressableGameObjectProvider())
        {
        }
    }

    /// <summary>
    /// 池化场景子预制宿主，使用 <see cref="PooledGameObjectProvider"/> 复用 Addressables 预制体实例。
    /// </summary>
    /// <typeparam name="TPrefab">子预制控制类类型。</typeparam>
    public sealed class PooledSceneSubPrefabHost<TPrefab> : SceneSubPrefabHost<TPrefab>
        where TPrefab : SceneSubPrefabCore, new()
    {
        /// <summary>子预制资源地址，同时作为对象池 key。</summary>
        private readonly string pooledKey;

        /// <summary>当前宿主使用的池化实例提供者。</summary>
        private readonly PooledGameObjectProvider pooledProvider;

        /// <summary>
        /// 创建池化场景子预制宿主。
        /// </summary>
        /// <param name="key">Addressables Prefab 地址，同时作为对象池 key。</param>
        /// <param name="parent">子预制使用期间的挂载父节点。</param>
        /// <param name="poolParent">池中闲置对象挂载父节点，可为空。</param>
        /// <param name="defaultCapacity">对象池默认预分配容量。</param>
        /// <param name="maxSize">对象池最大容量。</param>
        public PooledSceneSubPrefabHost(
            string key,
            Transform parent,
            Transform poolParent = null,
            int defaultCapacity = 0,
            int maxSize = 100)
            : this(key, parent, new PooledGameObjectProvider(poolParent, defaultCapacity, maxSize))
        {
        }

        /// <summary>
        /// 使用外部共享池化实例提供者创建池化场景子预制宿主。
        /// </summary>
        /// <param name="key">Addressables Prefab 地址，同时作为对象池 key。</param>
        /// <param name="parent">子预制使用期间的挂载父节点。</param>
        /// <param name="provider">外部共享池化实例提供者。</param>
        public PooledSceneSubPrefabHost(string key, Transform parent, PooledGameObjectProvider provider)
            : base(key, parent, provider)
        {
            pooledKey = key;
            pooledProvider = provider;
        }

        /// <summary>
        /// 预热当前子预制对应的对象池。
        /// </summary>
        /// <param name="count">预创建数量。</param>
        /// <returns>预热任务。</returns>
        public UniTask PrewarmAsync(int count)
        {
            return pooledProvider != null ? pooledProvider.PrewarmAsync(pooledKey, count) : UniTask.CompletedTask;
        }

        /// <summary>
        /// 清空当前宿主持有的池化实例提供者。
        /// </summary>
        public void ClearPool()
        {
            Dispose();
            pooledProvider?.Dispose();
        }
    }
}
