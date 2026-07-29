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
    /// <see cref="ResourceManager"/> 的「Addressables 初始化 / 远端 Catalog 热更」分部。
    /// <para>
    /// 与运行期取还资源（load/instantiate/release/引用计数）正交：这里负责引导 Addressables 运行时、
    /// 检查并更新远端资源目录、按组/标签下载资源包，属启动/热更阶段的一次性系统装配，
    /// 与主分部共享句柄缓存等状态。按分部类拆文件组织，零间接零行为风险。
    /// </para>
    /// </summary>
    public partial class ResourceManager
    {
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

        // Catalog 检查/更新编排：底层能力经 IAddressablesCatalogService 适配，失败路径可在 EditMode 注入复现。
        // 所有权边界：flow 无状态、service 无状态，由本组件持有并复用，不对外暴露可变引用。
        private CatalogUpdateFlow _catalogFlow;

        private CatalogUpdateFlow CatalogFlow =>
            _catalogFlow ??= new CatalogUpdateFlow(new AddressablesCatalogService(), GameLog.Log, GameLog.Error);

        /// <summary>
        /// 检查远端是否有新 Catalog，并自动更新。
        /// <para>
        /// 返回 <see cref="CatalogUpdateResult"/>，显式区分"无需更新 / 更新成功 / 检查失败 / 更新失败 /
        /// 被取消 / 无效结果"六种终态。调用方（LaunchFlow）必须检查 <see cref="CatalogUpdateResult.Succeeded"/>：
        /// 只有"检查成功且无更新"或"更新成功"允许继续资源下载；任何失败必须中止本次启动更新，
        /// 绝不允许提交 ResourceVersion（否则失败会被永久固化，后续启动不再重试真正的资源更新）。
        /// </para>
        /// </summary>
        /// <param name="cancellationToken">启动流程取消令牌；取消返回 Canceled 终态而非抛异常。</param>
        /// <param name="expectedCatalog">已验签清单声明的资源 Catalog 内容身份（ADR-009）；非 null 时应用前验签，
        /// 失败关闭。资源版本未增长（纯代码更新/老项目）传 null，保持原行为。</param>
        public async UniTask<CatalogUpdateResult> CheckAndUpdateCatalogsAsync(
            CancellationToken cancellationToken = default,
            HotUpdate.ResourceCatalogFile expectedCatalog = null)
        {
            GameLog.Log("[ResourceManager] 检查 Catalog 更新...");
            CatalogUpdateResult result = await CatalogFlow.CheckAndUpdateAsync(cancellationToken, expectedCatalog);
            if (result.Status == CatalogUpdateStatus.UpToDate)
            {
                GameLog.Log("[ResourceManager] Catalog 已是最新（Editor Play Mode 下属正常，" +
                            "如需强制测试下载流程请先调用 ClearCache）");
            }
            else if (result.CatalogChanged)
            {
                // Catalog 已换：本地化资源候选链的存在性可能已变（新增语言变体 / 原缺资源现已上架），
                // 失效解析缓存，否则启动期记下的负结果会把热更后的新变体一直挡在原始地址上。
                // 框架自持缓存自负一致性，不指望业务在热更完成处记得手动 ClearCache。
                LocalizedAssets.ClearCache();
            }
            return result;
        }

        /// <summary>
        /// 查询指定 key 的待下载字节数（结果化版本）。
        /// 与旧的 <see cref="GetDownloadSizeAsync(object)"/> 不同：查询失败返回
        /// <see cref="DownloadSizeStatus.Failed"/>，与"无需下载（0 字节）"严格区分。
        /// 启动更新链路（LaunchFlow）必须使用本方法，禁止使用把失败吞成 0 的旧接口。
        /// </summary>
        /// <param name="key">Address / Label / AssetReference。</param>
        /// <param name="cancellationToken">启动流程取消令牌。</param>
        public UniTask<DownloadSizeResult> TryGetDownloadSizeAsync(object key, CancellationToken cancellationToken = default)
        {
            return CatalogFlow.GetDownloadSizeAsync(key, cancellationToken);
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
        /// <para>
        /// 注意：本方法把"查询失败"也吞成 0，只适用于非关键的展示型查询（如设置页容量预估）。
        /// 启动更新链路必须使用 <see cref="TryGetDownloadSizeAsync"/>，否则查询失败会被误判为"无需下载"。
        /// </para>
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
        public async UniTask<bool> DownloadAllRemoteDependenciesAsync(
            Action<float> onProgress = null,
            CancellationToken cancellationToken = default)
        {
            GameLog.Log("[ResourceManager] 开始全量下载远端资源依赖...");

            // 句柄在 finally 统一释放，覆盖成功/失败/异常/取消四条出口（同 DownloadDependenciesAsync）。
            AsyncOperationHandle handle = default;
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

                handle = Addressables.DownloadDependenciesAsync((IEnumerable<object>)keys, false);

                while (!handle.IsDone)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    onProgress?.Invoke(handle.PercentComplete);
                    await UniTask.Yield();
                }

                if (handle.Status != AsyncOperationStatus.Succeeded)
                {
                    GameLog.Error($"[ResourceManager] 全量下载失败: {handle.OperationException?.Message}");
                    return false;
                }

                onProgress?.Invoke(1f);
                GameLog.Log("[ResourceManager] 全量远端资源下载完成");
                return true;
            }
            catch (OperationCanceledException)
            {
                GameLog.Log("[ResourceManager] 全量远端资源下载被取消。");
                return false;
            }
            catch (Exception e)
            {
                GameLog.Error($"[ResourceManager] DownloadAllRemoteDependenciesAsync 异常: {e.Message}");
                return false;
            }
            finally
            {
                if (handle.IsValid())
                    Addressables.Release(handle);
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
            long totalBytes = 0,
            CancellationToken cancellationToken = default)
        {
            GameLog.Log($"[ResourceManager] 开始下载资源依赖 [{key}]...");

            // 句柄在 finally 统一释放：覆盖成功、失败、异常、取消四条出口，catch 路径极易漏释放。
            // default 句柄的 IsValid() 为 false，异常发生在创建之前时 finally 安全跳过。
            AsyncOperationHandle handle = default;
            try
            {
                handle = Addressables.DownloadDependenciesAsync(key, false);

                float lastReported = -1f;
                while (!handle.IsDone)
                {
                    // 启动下载阶段可能很长：应用退出 / 强更跳转 / 用户重试时经令牌干净中止在途下载。
                    cancellationToken.ThrowIfCancellationRequested();

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
                    return false;
                }

                onProgress?.Invoke(1f);
                GameLog.Log($"[ResourceManager] 资源依赖下载完成 [{key}]");
                return true;
            }
            catch (OperationCanceledException)
            {
                GameLog.Log($"[ResourceManager] 资源依赖下载被取消 [{key}]。");
                return false;
            }
            catch (Exception e)
            {
                GameLog.Error($"[ResourceManager] DownloadDependenciesAsync 异常 [{key}]: {e.Message}");
                return false;
            }
            finally
            {
                // 释放下载句柄不会卸载已落缓存的 bundle，是必需的清理；不释放则句柄常驻泄漏。
                if (handle.IsValid())
                    Addressables.Release(handle);
            }
        }


        #endregion
    }
}
