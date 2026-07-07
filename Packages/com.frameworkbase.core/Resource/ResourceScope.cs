using System;
using System.Collections.Generic;
using Cysharp.Threading.Tasks;
using UnityEngine;

namespace Framework
{
    /// <summary>
    /// 资源作用域的宿主抽象：作用域只依赖这四个能力，不绑死 ResourceManager 具体类，
    /// 借出/归还的记账逻辑因此可以脱离 Addressables 离线单测。
    /// </summary>
    public interface IResourceScopeHost
    {
        UniTask<T> LoadAssetAsync<T>(string address) where T : UnityEngine.Object;
        UniTask<GameObject> InstantiateAsync(string address, Transform parent = null);
        void ReleaseAsset(string address);
        void ReleaseInstance(GameObject instance);
    }

    /// <summary>
    /// 资源作用域：按 场景/阶段/功能 划定资源生命周期，退出时一次性归还全部借出。
    ///
    /// Addressables 最大的坑是句柄漏放——谁借的、什么时候还，散落在各业务里没人对账。
    /// 作用域把"归还"从 N 处 Release 调用收敛成一处 Dispose：
    ///
    /// <code>
    /// using (var scope = GameEntry.Resource.CreateScope("BattleStage"))
    /// {
    ///     var cfg  = await scope.LoadAssetAsync&lt;BattleConfig&gt;("Battle/config");
    ///     var unit = await scope.InstantiateAsync("Battle/unit_01", root);
    ///     ...   // 阶段运行
    /// }         // 离开作用域：实例与资源引用全部自动归还
    /// </code>
    ///
    /// 约定：
    ///   · 主线程使用（与 ResourceManager 一致），不做线程安全；
    ///   · 通过作用域借的资源应通过作用域还（提前还调 scope.ReleaseAsset/ReleaseInstance，
    ///     直接找 ResourceManager 还会造成 Dispose 时二次归还、计数错乱）；
    ///   · Editor / Development Build 下作用域被 GC 回收却没 Dispose 会告警并带创建堆栈，
    ///     正式包零开销（不采堆栈、无终结器逻辑）。
    /// </summary>
    public sealed class ResourceScope : IDisposable
    {
        private readonly IResourceScopeHost _host;
        private readonly string _name;

        // 地址 → 本作用域持有的引用次数（同地址可多次借出，归还按次数逐一对账）
        private readonly Dictionary<string, int> _assetRefs = new Dictionary<string, int>();
        private readonly HashSet<GameObject> _instances = new HashSet<GameObject>();
        private bool _disposed;

#if UNITY_EDITOR || DEVELOPMENT_BUILD
        // 泄漏取证：作用域在哪创建的（终结器告警时带上，否则只知道漏了不知道谁漏的）
        private readonly string _creationStack;
#endif

        /// <summary>作用域名（诊断/日志用，建议用阶段或场景名）。</summary>
        public string Name => _name;

        /// <summary>是否已释放。</summary>
        public bool IsDisposed => _disposed;

        /// <summary>当前持有的资源引用总次数（诊断用）。</summary>
        public int AssetRefCount
        {
            get
            {
                int total = 0;
                foreach (int count in _assetRefs.Values)
                    total += count;
                return total;
            }
        }

        /// <summary>当前持有的存活实例数（诊断用）。</summary>
        public int InstanceCount => _instances.Count;

        internal ResourceScope(IResourceScopeHost host, string name)
        {
            _host = host ?? throw new ArgumentNullException(nameof(host));
            _name = string.IsNullOrEmpty(name) ? "unnamed" : name;
#if UNITY_EDITOR || DEVELOPMENT_BUILD
            _creationStack = Environment.StackTrace;
#endif
        }

#if UNITY_EDITOR || DEVELOPMENT_BUILD
        /// <summary>
        /// 泄漏哨兵：没 Dispose 就被 GC 说明借出的资源永远没人还了。
        /// 终结器线程不能碰 Addressables（主线程约束），只告警不代还——修复方式是补 Dispose。
        /// </summary>
        ~ResourceScope()
        {
            if (!_disposed)
            {
                GameLog.Error($"[ResourceScope] 作用域 \"{_name}\" 未 Dispose 即被回收，" +
                              $"其借出的 {_assetRefs.Count} 个地址 / {_instances.Count} 个实例已泄漏。创建位置：\n{_creationStack}");
            }
        }
#endif

        // ── 借出 ─────────────────────────────────────────────────────────────

        /// <summary>经作用域加载资源（成功即登记，Dispose 时归还该次引用）。</summary>
        public async UniTask<T> LoadAssetAsync<T>(string address) where T : UnityEngine.Object
        {
            if (_disposed)
            {
                GameLog.Error($"[ResourceScope] \"{_name}\" 已 Dispose，拒绝加载 {address}");
                return null;
            }

            T asset = await _host.LoadAssetAsync<T>(address);
            if (asset == null)
                return null; // 加载失败宿主已回滚计数，作用域不记账

            // await 期间作用域可能已被 Dispose（阶段被打断）：立即归还，避免这笔引用无人对账
            if (_disposed)
            {
                _host.ReleaseAsset(address);
                GameLog.Warning($"[ResourceScope] \"{_name}\" 在加载 {address} 完成前已 Dispose，已自动归还");
                return null;
            }

            _assetRefs.TryGetValue(address, out int count);
            _assetRefs[address] = count + 1;
            return asset;
        }

        /// <summary>经作用域实例化（成功即登记，Dispose 时自动 ReleaseInstance）。</summary>
        public async UniTask<GameObject> InstantiateAsync(string address, Transform parent = null)
        {
            if (_disposed)
            {
                GameLog.Error($"[ResourceScope] \"{_name}\" 已 Dispose，拒绝实例化 {address}");
                return null;
            }

            GameObject instance = await _host.InstantiateAsync(address, parent);
            if (instance == null)
                return null;

            if (_disposed)
            {
                _host.ReleaseInstance(instance);
                GameLog.Warning($"[ResourceScope] \"{_name}\" 在实例化 {address} 完成前已 Dispose，已自动归还");
                return null;
            }

            _instances.Add(instance);
            return instance;
        }

        // ── 作用域内提前归还 ─────────────────────────────────────────────────

        /// <summary>提前归还一次资源引用（Dispose 时不再重复归还这一笔）。</summary>
        public void ReleaseAsset(string address)
        {
            if (_disposed || string.IsNullOrEmpty(address))
                return;

            if (!_assetRefs.TryGetValue(address, out int count) || count <= 0)
            {
                GameLog.Warning($"[ResourceScope] \"{_name}\" 未持有 {address} 的引用，忽略归还（借还必须走同一个作用域）");
                return;
            }

            if (count == 1)
                _assetRefs.Remove(address);
            else
                _assetRefs[address] = count - 1;

            _host.ReleaseAsset(address);
        }

        /// <summary>提前归还一个实例（Dispose 时不再重复归还它）。</summary>
        public void ReleaseInstance(GameObject instance)
        {
            if (_disposed || instance == null)
                return;

            if (!_instances.Remove(instance))
            {
                GameLog.Warning($"[ResourceScope] \"{_name}\" 未持有该实例，忽略归还（借还必须走同一个作用域）");
                return;
            }

            _host.ReleaseInstance(instance);
        }

        // ── 收口 ─────────────────────────────────────────────────────────────

        /// <summary>归还本作用域持有的全部实例与资源引用。幂等：重复调用无副作用。</summary>
        public void Dispose()
        {
            if (_disposed)
                return;
            _disposed = true;

            // 先还实例（实例可能依赖资源），再按次数逐笔还资源引用
            int releasedInstances = 0;
            foreach (GameObject instance in _instances)
            {
                if (instance == null)
                    continue; // 外部已 Destroy 的实例跳过（Addressables 实例句柄随对象销毁已失效）
                _host.ReleaseInstance(instance);
                releasedInstances++;
            }
            _instances.Clear();

            int releasedRefs = 0;
            foreach (KeyValuePair<string, int> pair in _assetRefs)
            {
                for (int i = 0; i < pair.Value; i++)
                {
                    _host.ReleaseAsset(pair.Key);
                    releasedRefs++;
                }
            }
            _assetRefs.Clear();

            GameLog.Log($"[ResourceScope] \"{_name}\" 已收口：归还实例 {releasedInstances} 个 / 资源引用 {releasedRefs} 次");
            GC.SuppressFinalize(this);
        }
    }
}
