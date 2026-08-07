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

        /// <summary>本次加载是否在途。并发调用共享 <see cref="loadInFlight"/>，不各自再实例化一份。</summary>
        private bool loading;

        /// <summary>在途加载句柄（已 Preserve，可被多个调用方 await）。</summary>
        private UniTask<TPrefab> loadInFlight;

        /// <summary>
        /// 释放世代号，每次 <see cref="Dispose"/> 递增。加载在途期间宿主被释放时，
        /// 迟到的实例据此判断"我已经不属于任何人了"，直接归还而不是装进字段。
        /// </summary>
        private int disposeGeneration;

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
        public UniTask<TPrefab> LoadAsync()
        {
            if (string.IsNullOrEmpty(key))
            {
                GameLog.Error($"[SceneSubPrefabHost] LoadAsync 失败，key 为空: {typeof(TPrefab).Name}");
                return UniTask.FromResult<TPrefab>(null);
            }

            if (parent == null)
            {
                GameLog.Error($"[SceneSubPrefabHost] LoadAsync 失败，父节点为空: {typeof(TPrefab).Name}");
                return UniTask.FromResult<TPrefab>(null);
            }

            if (IsLoaded)
            {
                return UniTask.FromResult(prefab);
            }

            // 并发加载共享同一次请求：各自往下走会各实例化一个对象，后者覆盖 prefabObject / prefab 字段，
            // 前者从此无人引用也无人释放（Show 两次、或 Show 与预加载撞上就会发生）。
            // Preserve：UniTask 默认只能被 await 一次，多个等待方共享同一句柄必须先保留。
            if (!loading)
            {
                loading = true;
                loadInFlight = LoadCoreAsync().Preserve();
            }

            return loadInFlight;
        }

        /// <summary>
        /// 实际执行一次加载。结束时清掉在途标记，使失败或释放后仍可重新加载。
        /// </summary>
        /// <returns>加载并初始化成功的子预制控制类，否则 null。</returns>
        private async UniTask<TPrefab> LoadCoreAsync()
        {
            try
            {
                return await LoadInstanceAsync();
            }
            finally
            {
                loading = false;
            }
        }

        /// <summary>
        /// 加载主体：取实例、装配 View、初始化控制类。任一步失败都回到"未加载"状态。
        /// </summary>
        /// <returns>加载并初始化成功的子预制控制类，否则 null。</returns>
        private async UniTask<TPrefab> LoadInstanceAsync()
        {
            Dispose();

            // 记下本次加载的世代：await 期间调用方可能 Dispose 宿主，此时迟到的实例不该再装进字段。
            int generation = disposeGeneration;

            GameObject loaded = await provider.GetAsync(key, parent);
            if (loaded != null && generation != disposeGeneration)
            {
                provider.Release(loaded);
                GameLog.Log($"[SceneSubPrefabHost] 加载在途期间宿主已释放，丢弃迟到实例: {typeof(TPrefab).Name}");
                return null;
            }

            prefabObject = loaded;
            if (prefabObject == null)
            {
                ResetRuntimeState();
                GameLog.Error($"[SceneSubPrefabHost] 加载子预制失败: {typeof(TPrefab).Name}, Key={key}");
                return null;
            }

            prefab = new TPrefab();
            SceneSubView view = prefabObject.GetComponent(prefab.ViewType) as SceneSubView;
            if (view == null)
            {
                GameLog.Error($"[SceneSubPrefabHost] 子预制缺少 View 组件: {prefab.ViewType.Name}, Key={key}");
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
            // 递增世代：在途加载回来时据此判断宿主已被释放，把迟到实例归还而不是装进字段。
            disposeGeneration++;
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
