using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.AddressableAssets;
using UnityEngine.ResourceManagement.AsyncOperations;
using UnityEngine.SceneManagement;
using Cysharp.Threading.Tasks;

namespace Framework
{
    /// <summary>
    /// 资源管理器
    /// 封装 Addressables，提供统一的资源加载接口和引用计数管理
    /// 
    /// 职责：
    ///   1. 初始化 Addressables 运行时
    ///   2. 检查并更新远端 Catalog（资源目录）
    ///   3. 按分组/标签计算并下载远端资源包
    ///   4. 提供带引用计数的异步加载 / 实例化 / 释放接口
    /// </summary>
    public class ResourceManager : Core.FrameworkComponent, IResourceScopeHost
    {
        // 资源引用计数字典（仅统计 LoadAsset/LoadAssetAsync 加载的资源句柄）
        private Dictionary<string, int> _referenceCount = new Dictionary<string, int>();

        // 实例引用计数字典（仅统计 InstantiateAsync 创建的实例，与资源句柄计数严格分离）
        // 分离原因：实例由 Addressables 自身的实例句柄独立管理生命周期，
        // 若与资源计数共用一个 key，会出现「ReleaseInstance 把计数减到 0、却不释放 _handleCache 句柄」的泄漏。
        private Dictionary<string, int> _instanceCountByAddress = new Dictionary<string, int>();

        // 资源句柄缓存
        private Dictionary<string, AsyncOperationHandle> _handleCache = new Dictionary<string, AsyncOperationHandle>();

        // 标签预加载句柄缓存（按 label 保留，供后续释放，避免句柄泄漏）
        private Dictionary<string, AsyncOperationHandle> _labelHandleCache = new Dictionary<string, AsyncOperationHandle>();

        // 实例化对象到地址的映射
        private Dictionary<GameObject, string> _instanceToAddress = new Dictionary<GameObject, string>();

        #region 生命周期

        public override void OnInit()
        {
            GameLog.Log("ResourceManager 初始化");
        }

        public override void OnUpdate(float deltaTime)
        {
            // 资源管理器不需要每帧更新
        }

        public override void OnShutdown()
        {
            // 先释放仍在跟踪的实例句柄，避免异常退出 / 未逐个 ReleaseInstance 时 Addressables 实例句柄泄漏。
            // 实例句柄由 Addressables 独立管理，必须走 ReleaseInstance；拷贝键集合后遍历，避免释放过程中修改字典。
            if (_instanceToAddress.Count > 0)
            {
                var instances = new List<GameObject>(_instanceToAddress.Keys);
                foreach (var instance in instances)
                {
                    if (instance != null)
                    {
                        Addressables.ReleaseInstance(instance);
                    }
                }
                GameLog.Log($"ResourceManager 关闭：释放残留实例 {instances.Count} 个");
            }

            // 清理所有资源
            foreach (var handle in _handleCache.Values)
            {
                if (handle.IsValid())
                {
                    Addressables.Release(handle);
                }
            }

            // 释放标签预加载句柄，避免泄漏
            foreach (var handle in _labelHandleCache.Values)
            {
                if (handle.IsValid())
                {
                    Addressables.Release(handle);
                }
            }

            _handleCache.Clear();
            _labelHandleCache.Clear();
            _referenceCount.Clear();
            _instanceCountByAddress.Clear();
            _instanceToAddress.Clear();

            GameLog.Log("ResourceManager 关闭");
        }

        #endregion

        #region Addressables 初始化与热更新

        /// <summary>
        /// 初始化 Addressables 运行时。
        /// 必须在任何加载 / 下载操作之前调用一次。
        /// </summary>
        public async UniTask InitializeAsync()
        {
            GameLog.Log("[ResourceManager] 初始化 Addressables...");
            var handle = Addressables.InitializeAsync();
            await handle.Task;
            GameLog.Log("[ResourceManager] Addressables 初始化完成");
        }

        /// <summary>
        /// 检查远端是否有新 Catalog，并自动更新。
        /// 返回本次实际更新的 Catalog 数量（0 表示无更新）。
        /// </summary>
        public async UniTask<int> CheckAndUpdateCatalogsAsync()
        {
            GameLog.Log("[ResourceManager] 检查 Catalog 更新...");

            try
            {
                var checkHandle = Addressables.CheckForCatalogUpdates(false);
                await checkHandle.Task;

                if (checkHandle.Status != AsyncOperationStatus.Succeeded)
                {
                    GameLog.Warning("[ResourceManager] CheckForCatalogUpdates 失败，跳过更新");
                    Addressables.Release(checkHandle);
                    return 0;
                }

                var updatedCatalogs = checkHandle.Result;
                Addressables.Release(checkHandle);

                if (updatedCatalogs == null || updatedCatalogs.Count == 0)
                {
                    GameLog.Log("[ResourceManager] Catalog 已是最新（Editor Play Mode 下属正常，" +
                               "如需强制测试下载流程请先调用 ClearCacheAsync）");
                    return 0;
                }

                GameLog.Log($"[ResourceManager] 发现 {updatedCatalogs.Count} 个 Catalog 需要更新，开始下载...");

                var updateHandle = Addressables.UpdateCatalogs(updatedCatalogs, false);
                await updateHandle.Task;
                Addressables.Release(updateHandle);

                GameLog.Log($"[ResourceManager] Catalog 更新完成，共更新 {updatedCatalogs.Count} 个");
                return updatedCatalogs.Count;
            }
            catch (Exception e)
            {
                GameLog.Error($"[ResourceManager] Catalog 更新异常: {e.Message}");
                return 0;
            }
        }

        /// <summary>
        /// 清除本地 AssetBundle 缓存，强制下次启动重新下载所有远端 bundle。
        /// 仅用于开发测试（模拟首次安装），生产环境不要主动调用。
        /// </summary>
        public void ClearCache()
        {
            bool cleared = Caching.ClearCache();
            GameLog.Log(cleared
                ? "[ResourceManager] 本地 bundle 缓存已全部清除，下次启动将重新下载"
                : "[ResourceManager] 缓存清除失败（可能有 bundle 正在使用中）");
        }

        /// <summary>
        /// 计算指定 key（Address / Label / AssetReference）需要下载的字节数。
        /// 返回 0 表示无需下载（已缓存或本地包含）。
        /// InvalidKeyException（key 不存在）会静默返回 0，不会中断流程。
        /// </summary>
        public async UniTask<long> GetDownloadSizeAsync(object key)
        {
            try
            {
                var handle = Addressables.GetDownloadSizeAsync(key);
                await handle.Task;
                long size = handle.Result;
                Addressables.Release(handle);
                GameLog.Log($"[ResourceManager] 待下载大小 [{key}]: {FileUtils.FormatBytes(size)}");
                return size;
            }
            catch (InvalidKeyException)
            {
                // key 不存在于当前 catalog（例如分组名作为 key、label 未设置），跳过
                GameLog.Warning($"[ResourceManager] GetDownloadSizeAsync: key [{key}] 在 catalog 中不存在，跳过");
                return 0;
            }
            catch (Exception e)
            {
                GameLog.Error($"[ResourceManager] GetDownloadSizeAsync 异常 [{key}]: {e.Message}");
                return 0;
            }
        }

        /// <summary>
        /// 计算当前 catalog 中所有远端资源的待下载总字节数。
        /// 不需要指定 label，自动遍历所有 ResourceLocator 的 key。
        /// </summary>
        public async UniTask<long> GetTotalRemoteDownloadSizeAsync()
        {
            try
            {
                // 收集所有 locator 的 key
                var keys = new List<object>();
                foreach (var locator in Addressables.ResourceLocators)
                    foreach (var key in locator.Keys)
                        keys.Add(key);

                if (keys.Count == 0)
                {
                    GameLog.Warning("[ResourceManager] 当前 catalog 无任何 key，无法计算下载大小");
                    return 0;
                }

                var handle = Addressables.GetDownloadSizeAsync((IEnumerable<object>)keys);
                await handle.Task;
                long size = handle.Result;
                Addressables.Release(handle);
                GameLog.Log($"[ResourceManager] 全量待下载大小: {FileUtils.FormatBytes(size)}");
                return size;
            }
            catch (Exception e)
            {
                GameLog.Error($"[ResourceManager] GetTotalRemoteDownloadSizeAsync 异常: {e.Message}");
                return 0;
            }
        }

        /// <summary>
        /// 下载当前 catalog 中所有远端资源包（全量预下载）。
        /// 不需要指定 label，自动覆盖所有分组。
        /// </summary>
        public async UniTask<bool> DownloadAllRemoteDependenciesAsync(Action<float> onProgress = null)
        {
            GameLog.Log("[ResourceManager] 开始全量下载远端资源依赖...");

            try
            {
                var keys = new List<object>();
                foreach (var locator in Addressables.ResourceLocators)
                    foreach (var key in locator.Keys)
                        keys.Add(key);

                if (keys.Count == 0)
                {
                    GameLog.Warning("[ResourceManager] 当前 catalog 无 key，跳过下载");
                    onProgress?.Invoke(1f);
                    return true;
                }

                var handle = Addressables.DownloadDependenciesAsync((IEnumerable<object>)keys, false);

                while (!handle.IsDone)
                {
                    onProgress?.Invoke(handle.PercentComplete);
                    await UniTask.Yield();
                }

                if (handle.Status != AsyncOperationStatus.Succeeded)
                {
                    GameLog.Error($"[ResourceManager] 全量下载失败: {handle.OperationException?.Message}");
                    Addressables.Release(handle);
                    return false;
                }

                onProgress?.Invoke(1f);
                Addressables.Release(handle);
                GameLog.Log("[ResourceManager] 全量远端资源下载完成");
                return true;
            }
            catch (Exception e)
            {
                GameLog.Error($"[ResourceManager] DownloadAllRemoteDependenciesAsync 异常: {e.Message}");
                return false;
            }
        }

        /// <summary>
        /// 下载指定 key 的所有依赖资源包（bundle）。
        /// onProgress: 0~1 进度回调，基于已下载字节数计算，比 PercentComplete 更线性。
        /// totalBytes: 已通过 GetDownloadSizeAsync 计算好的总大小，传 0 则退化为 PercentComplete。
        /// </summary>
        public async UniTask<bool> DownloadDependenciesAsync(
            object key,
            Action<float> onProgress = null,
            long totalBytes = 0)
        {
            GameLog.Log($"[ResourceManager] 开始下载资源依赖 [{key}]...");

            try
            {
                var handle = Addressables.DownloadDependenciesAsync(key, false);

                float lastReported = -1f;
                while (!handle.IsDone)
                {
                    float progress;
                    if (totalBytes > 0)
                    {
                        // 基于已下载字节数计算：进度更线性，不受操作数量影响
                        long downloaded = handle.GetDownloadStatus().DownloadedBytes;
                        // 最多到 0.99，留 0.01 给最后的写缓存/CRC 阶段
                        progress = Mathf.Clamp(downloaded / (float)totalBytes, 0f, 0.99f);
                    }
                    else
                    {
                        progress = handle.PercentComplete;
                    }

                    // 变化超过 1% 才回调，避免每帧都触发 UI 重绘
                    if (progress - lastReported >= 0.01f)
                    {
                        onProgress?.Invoke(progress);
                        lastReported = progress;
                    }

                    await UniTask.Yield();
                }

                if (handle.Status != AsyncOperationStatus.Succeeded)
                {
                    GameLog.Error($"[ResourceManager] 下载依赖失败 [{key}]: {handle.OperationException?.Message}");
                    Addressables.Release(handle);
                    return false;
                }

                onProgress?.Invoke(1f);
                Addressables.Release(handle);
                GameLog.Log($"[ResourceManager] 资源依赖下载完成 [{key}]");
                return true;
            }
            catch (Exception e)
            {
                GameLog.Error($"[ResourceManager] DownloadDependenciesAsync 异常 [{key}]: {e.Message}");
                return false;
            }
        }


        #endregion

        #region 异步资源加载

        /// <summary>
        /// 异步加载资源
        /// </summary>
        /// <typeparam name="T">资源类型</typeparam>
        /// <param name="address">资源地址</param>
        /// <returns>加载的资源</returns>
        public async UniTask<T> LoadAssetAsync<T>(string address) where T : UnityEngine.Object
        {
            if (string.IsNullOrEmpty(address))
            {
                GameLog.Error("LoadAssetAsync: address 不能为空");
                return null;
            }

            try
            {
                // 命中缓存（可能仍在加载中）：增计数并等待其完成，实现并发去重
                if (_handleCache.TryGetValue(address, out var cachedHandle))
                {
                    AddReference(address);
                    if (!cachedHandle.IsDone) await cachedHandle.Task;
                    T cached = cachedHandle.Result as T;
                    if (cached == null) RemoveReference(address); // 共享句柄加载失败，回滚本次计数
                    return cached;
                }

                // 未缓存：先创建句柄并立即写入缓存，供并发调用复用，避免重复加载导致句柄泄漏
                var handle = Addressables.LoadAssetAsync<T>(address);
                _handleCache[address] = handle;
                AddReference(address);

                await handle.Task;
                T asset = handle.Result;

                if (asset == null)
                {
                    GameLog.Error($"LoadAssetAsync: 加载资源失败 - {address}");
                    RollbackLoad(address, handle); // 回滚缓存/计数并释放失败句柄
                    return null;
                }

                GameLog.Log($"LoadAssetAsync: 加载资源成功 - {address}");
                return asset;
            }
            catch (Exception e)
            {
                GameLog.Error($"LoadAssetAsync: 加载资源异常 - {address}, 错误: {e.Message}");
                // 句柄已入缓存时回滚，避免异常路径泄漏
                if (_handleCache.TryGetValue(address, out var failed))
                {
                    RollbackLoad(address, failed);
                }
                return null;
            }
        }

        /// <summary>
        /// 异步加载资源（带进度回调）
        /// </summary>
        public async UniTask<T> LoadAssetAsync<T>(string address, Action<float> onProgress) where T : UnityEngine.Object
        {
            if (string.IsNullOrEmpty(address))
            {
                GameLog.Error("LoadAssetAsync: address 不能为空");
                return null;
            }

            try
            {
                // 命中缓存（可能仍在加载中）：增计数，轮询等待在途完成，实现并发去重
                if (_handleCache.TryGetValue(address, out var cachedHandle))
                {
                    AddReference(address);
                    while (!cachedHandle.IsDone)
                    {
                        onProgress?.Invoke(cachedHandle.PercentComplete);
                        await UniTask.Yield();
                    }
                    onProgress?.Invoke(1f);
                    T cached = cachedHandle.Result as T;
                    if (cached == null) RemoveReference(address); // 共享句柄加载失败，回滚本次计数
                    return cached;
                }

                // 未缓存：先创建句柄并立即写入缓存，供并发调用复用，避免重复加载导致句柄泄漏
                var handle = Addressables.LoadAssetAsync<T>(address);
                _handleCache[address] = handle;
                AddReference(address);

                // 监听进度
                while (!handle.IsDone)
                {
                    onProgress?.Invoke(handle.PercentComplete);
                    await UniTask.Yield();
                }

                T asset = handle.Result;
                if (asset == null)
                {
                    GameLog.Error($"LoadAssetAsync: 加载资源失败 - {address}");
                    RollbackLoad(address, handle); // 回滚缓存/计数并释放失败句柄
                    return null;
                }

                onProgress?.Invoke(1f);
                GameLog.Log($"LoadAssetAsync: 加载资源成功 - {address}");
                return asset;
            }
            catch (Exception e)
            {
                GameLog.Error($"LoadAssetAsync: 加载资源异常 - {address}, 错误: {e.Message}");
                if (_handleCache.TryGetValue(address, out var failed))
                {
                    RollbackLoad(address, failed);
                }
                return null;
            }
        }

        #endregion

        #region 同步资源加载

        /// <summary>
        /// 同步加载资源（仅用于已预加载的资源）
        /// </summary>
        public T LoadAsset<T>(string address) where T : UnityEngine.Object
        {
            if (string.IsNullOrEmpty(address))
            {
                GameLog.Error("LoadAsset: address 不能为空");
                return null;
            }

            // 检查缓存
            if (_handleCache.TryGetValue(address, out var cachedHandle))
            {
                AddReference(address);
                return cachedHandle.Result as T;
            }

            GameLog.Warning($"LoadAsset: 资源未预加载，建议使用 LoadAssetAsync - {address}");
            
            // 同步加载（会阻塞主线程，不推荐）
            var handle = Addressables.LoadAssetAsync<T>(address);
            T asset = handle.WaitForCompletion();

            if (asset != null)
            {
                _handleCache[address] = handle;
                AddReference(address);
            }

            return asset;
        }

        #endregion

        #region GameObject 实例化

        /// <summary>
        /// 异步实例化 GameObject
        /// </summary>
        public async UniTask<GameObject> InstantiateAsync(string address, Transform parent = null)
        {
            if (string.IsNullOrEmpty(address))
            {
                GameLog.Error("InstantiateAsync: address 不能为空");
                return null;
            }

            try
            {
                var handle = Addressables.InstantiateAsync(address, parent);
                await handle.Task;
                GameObject instance = handle.Result;

                if (instance == null)
                {
                    GameLog.Error($"InstantiateAsync: 实例化失败 - {address}");
                    return null;
                }

                // 记录实例到地址的映射
                _instanceToAddress[instance] = address;

                // 增加实例计数（与资源句柄计数分离）
                AddInstanceRef(address);

                GameLog.Log($"InstantiateAsync: 实例化成功 - {address}");
                return instance;
            }
            catch (Exception e)
            {
                GameLog.Error($"InstantiateAsync: 实例化异常 - {address}, 错误: {e.Message}");
                return null;
            }
        }

        /// <summary>
        /// 异步实例化 GameObject（指定位置和旋转）
        /// </summary>
        public async UniTask<GameObject> InstantiateAsync(
            string address, 
            Vector3 position, 
            Quaternion rotation, 
            Transform parent = null)
        {
            if (string.IsNullOrEmpty(address))
            {
                GameLog.Error("InstantiateAsync: address 不能为空");
                return null;
            }

            try
            {
                var handle = Addressables.InstantiateAsync(address, position, rotation, parent);
                await handle.Task;
                GameObject instance = handle.Result;

                if (instance == null)
                {
                    GameLog.Error($"InstantiateAsync: 实例化失败 - {address}");
                    return null;
                }

                _instanceToAddress[instance] = address;
                AddInstanceRef(address);

                GameLog.Log($"InstantiateAsync: 实例化成功 - {address}");
                return instance;
            }
            catch (Exception e)
            {
                GameLog.Error($"InstantiateAsync: 实例化异常 - {address}, 错误: {e.Message}");
                return null;
            }
        }

        #endregion

        #region 资源预加载

        /// <summary>
        /// 预加载资源列表
        /// </summary>
        public async UniTask PreloadAssetsAsync(List<string> addresses, Action<float> onProgress = null)
        {
            if (addresses == null || addresses.Count == 0)
            {
                GameLog.Warning("PreloadAssetsAsync: 预加载列表为空");
                return;
            }

            int totalCount = addresses.Count;
            int loadedCount = 0;

            foreach (var address in addresses)
            {
                await LoadAssetAsync<UnityEngine.Object>(address);
                loadedCount++;
                onProgress?.Invoke((float)loadedCount / totalCount);
            }

            GameLog.Log($"PreloadAssetsAsync: 预加载完成，共 {totalCount} 个资源");
        }

        /// <summary>
        /// 通过标签预加载资源
        /// </summary>
        public async UniTask PreloadAssetsByLabelAsync(string label, Action<float> onProgress = null)
        {
            if (string.IsNullOrEmpty(label))
            {
                GameLog.Error("PreloadAssetsByLabelAsync: label 不能为空");
                return;
            }

            // 已预加载过该标签：跳过，避免重复加载产生第二个无法释放的句柄
            if (_labelHandleCache.ContainsKey(label))
            {
                GameLog.Warning($"PreloadAssetsByLabelAsync: 标签 '{label}' 已预加载，跳过");
                onProgress?.Invoke(1f);
                return;
            }

            try
            {
                var handle = Addressables.LoadAssetsAsync<UnityEngine.Object>(label, null);

                while (!handle.IsDone)
                {
                    onProgress?.Invoke(handle.PercentComplete);
                    await UniTask.Yield();
                }

                if (handle.Status != AsyncOperationStatus.Succeeded)
                {
                    GameLog.Error($"PreloadAssetsByLabelAsync: 预加载标签失败 - {label}: {handle.OperationException?.Message}");
                    if (handle.IsValid()) Addressables.Release(handle);
                    return;
                }

                // 保留句柄供后续 ReleaseAssetsByLabel / OnShutdown 释放，避免永久泄漏
                _labelHandleCache[label] = handle;

                var assets = handle.Result;
                GameLog.Log($"PreloadAssetsByLabelAsync: 预加载标签 '{label}' 完成，共 {assets.Count} 个资源");

                onProgress?.Invoke(1f);
            }
            catch (Exception e)
            {
                GameLog.Error($"PreloadAssetsByLabelAsync: 预加载标签异常 - {label}, 错误: {e.Message}");
            }
        }

        #endregion

        #region 资源释放

        /// <summary>
        /// 释放资源
        /// </summary>
        public void ReleaseAsset(string address)
        {
            if (string.IsNullOrEmpty(address))
            {
                return;
            }

            // 减少引用计数
            if (!RemoveReference(address))
            {
                return;
            }

            // 引用计数为 0，释放资源
            if (_handleCache.TryGetValue(address, out var handle))
            {
                if (handle.IsValid())
                {
                    Addressables.Release(handle);
                }
                
                _handleCache.Remove(address);
                GameLog.Log($"ReleaseAsset: 释放资源 - {address}");
            }
        }

        /// <summary>
        /// 释放通过 <see cref="PreloadAssetsByLabelAsync"/> 预加载的某个标签的全部资源。
        /// </summary>
        /// <param name="label">资源标签。</param>
        public void ReleaseAssetsByLabel(string label)
        {
            if (string.IsNullOrEmpty(label))
            {
                return;
            }

            if (_labelHandleCache.TryGetValue(label, out var handle))
            {
                if (handle.IsValid())
                {
                    Addressables.Release(handle);
                }
                _labelHandleCache.Remove(label);
                GameLog.Log($"ReleaseAssetsByLabel: 释放标签资源 - {label}");
            }
        }

        /// <summary>
        /// 释放实例化的 GameObject
        /// </summary>
        public void ReleaseInstance(GameObject instance)
        {
            if (instance == null)
            {
                return;
            }

            // 查找对应的地址
            if (_instanceToAddress.TryGetValue(instance, out var address))
            {
                _instanceToAddress.Remove(instance);
                RemoveInstanceRef(address);

                // 释放实例（实例句柄由 Addressables 独立管理，与资源句柄无关）
                Addressables.ReleaseInstance(instance);

                GameLog.Log($"ReleaseInstance: 释放实例 - {address}");
            }
            else
            {
                GameLog.Warning($"ReleaseInstance: 实例不是通过 ResourceManager 创建的");
                UnityEngine.Object.Destroy(instance);
            }
        }

        #endregion

        #region 引用计数管理

        /// <summary>
        /// 增加引用计数
        /// </summary>
        private void AddReference(string address)
        {
            if (_referenceCount.ContainsKey(address))
            {
                _referenceCount[address]++;
            }
            else
            {
                _referenceCount[address] = 1;
            }
        }

        /// <summary>
        /// 减少引用计数
        /// </summary>
        /// <returns>引用计数是否为 0</returns>
        private bool RemoveReference(string address)
        {
            if (!_referenceCount.ContainsKey(address))
            {
                return false;
            }

            _referenceCount[address]--;
            
            if (_referenceCount[address] <= 0)
            {
                _referenceCount.Remove(address);
                return true;
            }

            return false;
        }

        /// <summary>
        /// 回滚一次失败/异常的加载：移除句柄缓存与本次引用计数；
        /// 仅当该地址再无任何引用时才释放句柄，避免拔掉并发调用方仍在等待的句柄。
        /// </summary>
        /// <param name="address">资源地址。</param>
        /// <param name="handle">本次加载创建的句柄。</param>
        private void RollbackLoad(string address, AsyncOperationHandle handle)
        {
            _handleCache.Remove(address);
            bool noRefsLeft = RemoveReference(address);
            if (noRefsLeft && handle.IsValid())
            {
                Addressables.Release(handle);
            }
        }

        /// <summary>
        /// 增加实例计数（仅用于 InstantiateAsync 创建的实例）
        /// </summary>
        private void AddInstanceRef(string address)
        {
            _instanceCountByAddress.TryGetValue(address, out int count);
            _instanceCountByAddress[address] = count + 1;
        }

        /// <summary>
        /// 减少实例计数（仅用于 ReleaseInstance）
        /// </summary>
        private void RemoveInstanceRef(string address)
        {
            if (!_instanceCountByAddress.TryGetValue(address, out int count))
            {
                return;
            }

            count--;
            if (count <= 0)
            {
                _instanceCountByAddress.Remove(address);
            }
            else
            {
                _instanceCountByAddress[address] = count;
            }
        }

        /// <summary>
        /// 获取资源引用计数（仅 LoadAsset/LoadAssetAsync 加载的资源句柄）
        /// </summary>
        public int GetReferenceCount(string address)
        {
            return _referenceCount.TryGetValue(address, out var count) ? count : 0;
        }

        /// <summary>
        /// 获取实例计数（仅 InstantiateAsync 创建、尚未 ReleaseInstance 的实例数）
        /// </summary>
        public int GetInstanceCount(string address)
        {
            return _instanceCountByAddress.TryGetValue(address, out var count) ? count : 0;
        }

        #endregion

        #region 资源作用域

        /// <summary>
        /// 创建资源作用域：按 场景/阶段/功能 划定生命周期，Dispose 时归还全部借出，
        /// 业务不再逐个对账 Release。用法见 Resource/RESOURCE_SCOPE_GUIDE.md。
        /// </summary>
        /// <param name="name">作用域名（诊断用，建议用阶段/场景名）。</param>
        public ResourceScope CreateScope(string name)
        {
            return new ResourceScope(this, name);
        }

        #endregion

        #region 诊断信息（性能 HUD / 泄漏排查用）

        /// <summary>存活的资源句柄数（LoadAsset 系加载、尚未释放到 0 的地址数）。</summary>
        public int LiveAssetHandleCount => _handleCache.Count;

        /// <summary>存活的实例数（InstantiateAsync 创建、尚未 ReleaseInstance）。</summary>
        public int LiveInstanceCount => _instanceToAddress.Count;

        /// <summary>存活的标签预加载句柄数。</summary>
        public int LiveLabelHandleCount => _labelHandleCache.Count;

        #endregion

        #region 调试信息

        /// <summary>
        /// 打印所有已加载资源的信息
        /// </summary>
        public void PrintLoadedAssets()
        {
            GameLog.Log("=== 已加载资源列表 ===");
            foreach (var kvp in _referenceCount)
            {
                GameLog.Log($"  [资源] {kvp.Key} - 引用计数: {kvp.Value}");
            }
            foreach (var kvp in _instanceCountByAddress)
            {
                GameLog.Log($"  [实例] {kvp.Key} - 实例计数: {kvp.Value}");
            }
            GameLog.Log($"总计: 资源 {_referenceCount.Count} 个 / 实例地址 {_instanceCountByAddress.Count} 个");
        }

        #endregion
    }
}
