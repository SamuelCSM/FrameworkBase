using System;
using System.Collections.Generic;
using System.Threading;
using UnityEngine;
using UnityEngine.AddressableAssets;
using UnityEngine.ResourceManagement.AsyncOperations;
using UnityEngine.SceneManagement;
using Cysharp.Threading.Tasks;
using Framework.Performance;

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
    public partial class ResourceManager : Core.FrameworkComponent<ResourceManager>, IResourceService
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


        #region 异步资源加载

        /// <summary>
        /// 异步借用资源并返回显式所有权 Lease。调用方必须 Dispose 返回值；取消某一等待者
        /// 不会中止同地址的共享底层加载，迟到结果会自动归还该等待者预占的引用。
        /// </summary>
        public UniTask<AssetLease<T>> LoadLeaseAsync<T>(
            string address,
            CancellationToken cancellationToken = default) where T : UnityEngine.Object
        {
            cancellationToken.ThrowIfCancellationRequested();
            return AssetLeaseCoordinator.AcquireStartedAsync(
                address,
                LoadAssetAsync<T>(address),
                ReleaseAsset,
                cancellationToken);
        }

        /// <summary>
        /// 查询地址是否存在可加载的资源位置。只查 Catalog 不加载资源本体、不进引用计数，
        /// 供「按候选链探测」类场景（如本地化资源回退）使用。
        /// 注意结果反映当前 Catalog：Catalog 热更后同一地址的存在性可能变化，调用方缓存需自行失效。
        /// </summary>
        public async UniTask<bool> ExistsAsync(string address)
        {
            if (string.IsNullOrEmpty(address))
                return false;

            var handle = Addressables.LoadResourceLocationsAsync(address);
            try
            {
                var locations = await handle.Task;
                return locations != null && locations.Count > 0;
            }
            catch (Exception e)
            {
                GameLog.Error($"ExistsAsync: 查询资源位置异常 - {address}, 错误: {e.Message}");
                return false;
            }
            finally
            {
                if (handle.IsValid())
                    Addressables.Release(handle);
            }
        }

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
                    // 共享句柄加载失败：与创建者的 RollbackLoad 一致地回滚——谁把引用减到 0 谁释放底层句柄。
                    // 修复"创建者已移缓存并把引用减到非零、命中者随后减到 0 却只减引用不释放"的并发失败句柄泄漏。
                    if (cached == null) RollbackSharedReference(address, cachedHandle);
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
                    // 共享句柄加载失败：同上，交由 RollbackSharedReference 统一回收，避免并发失败漏释放句柄。
                    if (cached == null) RollbackSharedReference(address, cachedHandle);
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
                // 命中的共享句柄可能仍在途（由某次 LoadAssetAsync 创建但未完成）：同步 API 无法 await，
                // 只能阻塞其完成再取值。原实现直接读 Result，在途时得到 null 却已 AddReference，造成引用超计泄漏。
                T cached = cachedHandle.IsDone
                    ? cachedHandle.Result as T
                    : cachedHandle.WaitForCompletion() as T;
                // 仅在拿到可用资源时才计一次引用；共享句柄失败不为其增引用（回收交由创建者路径）。
                if (cached == null)
                    return null;
                AddReference(address);
                return cached;
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
            else
            {
                // 加载失败：句柄不入缓存，必须就地释放，否则同步失败路径会永久泄漏该句柄。
                if (handle.IsValid())
                    Addressables.Release(handle);
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
        /// 预加载批量并发度覆盖：&gt;0 时优先于设备分级映射直接生效；≤0 回退按
        /// <see cref="DeviceTierService.Tier"/> 经 <see cref="DeviceTierResourceTuning.PreloadConcurrency"/> 取值。
        /// 用于压测或项目已有自己的档位判断时手动钉死并发度。
        /// </summary>
        public int PreloadConcurrencyOverride { get; set; } = 0;

        /// <summary>
        /// 解析本次预加载的实际并发度：覆盖优先，否则按当前设备档位映射；
        /// 收敛到 [1, itemCount]——超过任务数只会空转 worker，低于 1 退化为串行。
        /// </summary>
        /// <param name="itemCount">本次待加载资源数（&gt;0）。</param>
        /// <returns>并发度，范围 [1, itemCount]。</returns>
        private int ResolvePreloadConcurrency(int itemCount)
        {
            int degree = PreloadConcurrencyOverride > 0
                ? PreloadConcurrencyOverride
                : DeviceTierResourceTuning.PreloadConcurrency(DeviceTierService.Tier);

            if (degree < 1) degree = 1;
            if (degree > itemCount) degree = itemCount;
            return degree;
        }

        /// <summary>
        /// 预加载资源列表，按设备档位限流并行：低端窄路保内存、高端多路提吞吐。
        /// 并发度由 <see cref="ResolvePreloadConcurrency"/> 决定（可经 <see cref="PreloadConcurrencyOverride"/> 覆盖）；
        /// 同一地址的并发请求由 <see cref="LoadAssetAsync{T}(string)"/> 的缓存去重兜底，不会重复加载。
        /// </summary>
        /// <param name="addresses">待预加载地址列表。</param>
        /// <param name="onProgress">进度回调（0~1），按已完成数递增，主线程回调。</param>
        public async UniTask PreloadAssetsAsync(List<string> addresses, Action<float> onProgress = null)
        {
            if (addresses == null || addresses.Count == 0)
            {
                GameLog.Warning("PreloadAssetsAsync: 预加载列表为空");
                return;
            }

            int totalCount = addresses.Count;
            int concurrency = ResolvePreloadConcurrency(totalCount);

            if (concurrency <= 1)
            {
                // 低端档或单资源：串行加载，内存峰值最小、无并发歧义（保持原语义）。
                int loadedCount = 0;
                foreach (var address in addresses)
                {
                    await LoadAssetAsync<UnityEngine.Object>(address);
                    loadedCount++;
                    onProgress?.Invoke((float)loadedCount / totalCount);
                }
            }
            else
            {
                // 按设备档位限流的并行预加载：concurrency 个 worker 共享游标领取地址，
                // 同时在途的 Addressables 加载不超过 concurrency，兼顾吞吐与内存/IO 峰值。
                // UniTask 为主线程协作式调度：worker 仅在 await 处交错，游标领取（读 nextIndex→自增）
                // 之间无 await、不会被抢占，故无需锁；真正并行的是底层 Addressables 异步操作。
                int nextIndex = 0;
                int completedCount = 0;

                async UniTask RunWorkerAsync()
                {
                    while (true)
                    {
                        int i = nextIndex;
                        if (i >= totalCount) return;
                        nextIndex = i + 1;

                        await LoadAssetAsync<UnityEngine.Object>(addresses[i]);

                        completedCount++;
                        onProgress?.Invoke((float)completedCount / totalCount);
                    }
                }

                var workers = new List<UniTask>(concurrency);
                for (int w = 0; w < concurrency; w++)
                    workers.Add(RunWorkerAsync());
                await UniTask.WhenAll(workers);
            }

            GameLog.Log($"PreloadAssetsAsync: 预加载完成，共 {totalCount} 个资源（并发 {concurrency}）");
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
        /// 命中共享句柄却未取得可用资源（加载失败）时的回收，与 <see cref="RollbackLoad"/> 语义统一：
        /// 谁把引用减到 0，谁负责移除缓存并释放底层句柄。修复并发失败下"创建者已移缓存、命中者减到 0
        /// 却只减引用不释放句柄"的泄漏。必须传入调用方自己 await 的句柄，因为缓存此刻可能已被创建者回滚移除，
        /// 从缓存重新查会取不到而漏释放。
        /// </summary>
        /// <param name="address">资源地址。</param>
        /// <param name="sharedHandle">调用方等待过的共享句柄。</param>
        private void RollbackSharedReference(string address, AsyncOperationHandle sharedHandle)
        {
            bool noRefsLeft = RemoveReference(address);
            if (!noRefsLeft)
            {
                return;
            }

            // 本次调用把引用清零：缓存可能仍指向该失败句柄（无人回滚过），也可能已被创建者移除，
            // 或已被新一轮加载替换成别的句柄。仅当仍指向同一句柄时才由此处移除，避免误删新句柄的缓存项。
            if (_handleCache.TryGetValue(address, out var cached) && cached.Equals(sharedHandle))
            {
                _handleCache.Remove(address);
            }

            // 释放与缓存是否命中无关：只要本次把引用清零且句柄有效，就必须归还，避免并发失败漏释放。
            if (sharedHandle.IsValid())
            {
                Addressables.Release(sharedHandle);
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
