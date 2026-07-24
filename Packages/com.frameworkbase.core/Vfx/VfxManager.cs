using System;
using System.Collections.Generic;
using Cysharp.Threading.Tasks;
using Framework.Performance;
using UnityEngine;

namespace Framework
{
    /// <summary>
    /// 特效调度纯核（不依赖 UnityEngine，可 EditMode 单测）：只管"预算准入 + 按时长过期"的账本，
    /// 不碰 GameObject/粒子。id ↔ 实例的映射与实际取还由 <see cref="VfxManager"/> 负责。
    /// <para>
    /// 预算超限即丢弃（返回 0）——表现降级优先于低端机卡顿/OOM，是 <see cref="DeviceTier"/> 削峰的落点。
    /// </para>
    /// </summary>
    public sealed class VfxScheduler
    {
        private struct Entry
        {
            public int Id;
            public float Remaining; // 剩余存活秒；Manual 项忽略
            public bool Manual;     // duration<0：手动特效，Stop 前不过期
        }

        private readonly List<Entry> _active = new List<Entry>();
        private int _nextId = 1;

        /// <summary>并发上限；&lt;=0 表示不限。</summary>
        public int Budget { get; set; }

        /// <summary>当前存活特效数。</summary>
        public int ActiveCount => _active.Count;

        /// <summary>构造调度器。</summary>
        /// <param name="budget">并发上限（&lt;=0 不限）。</param>
        public VfxScheduler(int budget)
        {
            Budget = budget;
        }

        /// <summary>
        /// 尝试登记一个特效。超预算返回 0（应丢弃）；否则返回 &gt;0 的 id。
        /// <paramref name="duration"/> &lt;0 视为手动特效（Stop 前不自动过期）。
        /// </summary>
        public int TryRegister(float duration)
        {
            if (Budget > 0 && _active.Count >= Budget)
                return 0;

            int id = _nextId++;
            _active.Add(new Entry { Id = id, Remaining = duration, Manual = duration < 0f });
            return id;
        }

        /// <summary>
        /// 推进 <paramref name="dt"/> 秒，把到期特效 id 填入 <paramref name="expired"/>（先清空）。手动特效不过期。
        /// </summary>
        public void Tick(float dt, List<int> expired)
        {
            expired.Clear();
            for (int i = _active.Count - 1; i >= 0; i--)
            {
                Entry e = _active[i];
                if (e.Manual)
                    continue;

                e.Remaining -= dt;
                if (e.Remaining <= 0f)
                {
                    expired.Add(e.Id);
                    _active.RemoveAt(i);
                }
                else
                {
                    _active[i] = e;
                }
            }
        }

        /// <summary>手动移除（Stop）；返回该 id 是否存在。</summary>
        public bool Remove(int id)
        {
            for (int i = 0; i < _active.Count; i++)
            {
                if (_active[i].Id == id)
                {
                    _active.RemoveAt(i);
                    return true;
                }
            }
            return false;
        }

        /// <summary>清空全部（Shutdown）。</summary>
        public void Clear() => _active.Clear();
    }

    /// <summary>特效播放选项。</summary>
    public struct VfxPlayOptions
    {
        /// <summary>存活时长：&gt;0 固定秒；0 自动（取粒子系统时长）；&lt;0 手动（Stop 前不回收，用于持续光环等）。</summary>
        public float Duration;

        /// <summary>跟随目标（可空）；非空时每帧把特效位置贴到目标 + <see cref="FollowOffset"/>。</summary>
        public Transform Follow;

        /// <summary>跟随偏移。</summary>
        public Vector3 FollowOffset;
    }

    /// <summary>
    /// 特效管理器（opt-in）：架在框架既有 <see cref="IGameObjectProvider"/>（池化取还）之上，补三件缺口——
    /// ① 按时长/粒子自动回收；② 跟随目标；③ 按 <see cref="DeviceTier"/> 的同屏并发上限削峰。
    /// <para>
    /// 刻意<b>不</b>做成 <c>FrameworkComponent</c> 进 GameEntry 管理清单——特效偏表现、按需接入，
    /// 由业务经 <see cref="Create"/> 创建（或直接挂组件）。访问点 <see cref="Manager"/>，未创建时为 null。
    /// 表现细节（特效资源怎么配、播不播由谁决定）仍属业务。
    /// </para>
    /// </summary>
    public sealed class VfxManager : MonoBehaviour
    {
        private struct ActiveVfx
        {
            public GameObject Go;
            public Transform Follow;
            public Vector3 Offset;
        }

        private IGameObjectProvider _provider;
        private bool _ownsProvider;
        private VfxScheduler _scheduler;
        // 档位派生的预算（玩家改档可变）；显式设 Budget 后转为固定，不再跟档位。
        private bool _autoBudgetFromTier = true;

        private readonly Dictionary<int, ActiveVfx> _instances = new Dictionary<int, ActiveVfx>();
        private readonly List<int> _expiredBuffer = new List<int>();

        /// <summary>当前特效管理器；未创建时为 null（特效功能未接入）。</summary>
        public static VfxManager Manager { get; private set; }

        /// <summary>同屏并发上限；读为当前值，写则固定为该值（不再跟设备档位）。</summary>
        public int Budget
        {
            get => _scheduler.Budget;
            set { _scheduler.Budget = value; _autoBudgetFromTier = false; }
        }

        /// <summary>当前存活特效数。</summary>
        public int ActiveCount => _scheduler != null ? _scheduler.ActiveCount : 0;

        /// <summary>
        /// 创建特效管理器（挂在新建的常驻 GameObject 上）。
        /// </summary>
        /// <param name="provider">实例提供者；null 则内部建一个池化 provider（本管理器持有并在销毁时释放）。</param>
        /// <param name="root">挂载父节点（可空，通常传跨场景常驻根）。</param>
        public static VfxManager Create(IGameObjectProvider provider = null, Transform root = null)
        {
            var go = new GameObject("[VfxManager]");
            if (root != null)
                go.transform.SetParent(root, false);
            var manager = go.AddComponent<VfxManager>();
            manager.EnsureInitialized(provider);
            return manager;
        }

        private void Awake()
        {
            // 直接挂组件（未走 Create）时也能自初始化。
            EnsureInitialized(null);
        }

        private void EnsureInitialized(IGameObjectProvider provider)
        {
            if (_scheduler != null)
                return;

            if (provider != null)
            {
                _provider = provider;
                _ownsProvider = false;
            }
            else
            {
                _provider = new PooledGameObjectProvider(transform);
                _ownsProvider = true;
            }
            _scheduler = new VfxScheduler(ResolveBudget());
            Manager = this;
        }

        private int ResolveBudget() => DeviceTierResourceTuning.MaxConcurrentEffects(DeviceTierService.Tier);

        /// <summary>
        /// 播放一个特效并返回句柄（&gt;0）。超并发预算或加载失败返回 0（被丢弃）。
        /// </summary>
        /// <param name="address">特效资源地址（Addressables）。</param>
        /// <param name="position">世界坐标。</param>
        /// <param name="rotation">朝向。</param>
        /// <param name="options">播放选项（时长/跟随）。</param>
        /// <returns>句柄（0 表示未播放）。持续特效（Duration&lt;0）须留句柄以便 <see cref="Stop"/>。</returns>
        public async UniTask<int> PlayAsync(string address, Vector3 position, Quaternion rotation,
            VfxPlayOptions options = default)
        {
            if (string.IsNullOrEmpty(address))
                return 0;

            if (_autoBudgetFromTier)
                _scheduler.Budget = ResolveBudget(); // 档位可能被玩家改，取最新

            // 预算预检：满了直接不加载（低端削峰，省下加载/实例化开销）。
            if (_scheduler.Budget > 0 && _scheduler.ActiveCount >= _scheduler.Budget)
                return 0;

            GameObject go = await _provider.GetAsync(address, transform);
            if (go == null)
                return 0;

            go.transform.SetPositionAndRotation(position, rotation);

            float duration = options.Duration;
            if (Mathf.Approximately(duration, 0f))
                duration = DetectDuration(go);

            int id = _scheduler.TryRegister(duration);
            if (id <= 0)
            {
                // await 期间并发把预算占满：二次确认失败，归还实例。
                _provider.Release(go);
                return 0;
            }

            if (options.Follow != null)
                go.transform.position = options.Follow.position + options.FollowOffset;

            _instances[id] = new ActiveVfx { Go = go, Follow = options.Follow, Offset = options.FollowOffset };
            return id;
        }

        /// <summary>主动停止并回收一个特效（持续特效必须用它收尾）。无效句柄安全忽略。</summary>
        public void Stop(int handle)
        {
            if (handle <= 0)
                return;

            _scheduler.Remove(handle);
            if (_instances.TryGetValue(handle, out ActiveVfx v))
            {
                _instances.Remove(handle);
                if (v.Go != null)
                    _provider.Release(v.Go);
            }
        }

        private void Update()
        {
            if (_scheduler == null || _instances.Count == 0)
                return;

            // 跟随目标（遍历中不改字典）。
            foreach (KeyValuePair<int, ActiveVfx> kv in _instances)
            {
                ActiveVfx v = kv.Value;
                if (v.Follow != null && v.Go != null)
                    v.Go.transform.position = v.Follow.position + v.Offset;
            }

            // 到期回收（特效随游戏暂停，用缩放时间）。
            _scheduler.Tick(Time.deltaTime, _expiredBuffer);
            for (int i = 0; i < _expiredBuffer.Count; i++)
            {
                int id = _expiredBuffer[i];
                if (_instances.TryGetValue(id, out ActiveVfx v))
                {
                    _instances.Remove(id);
                    if (v.Go != null)
                        _provider.Release(v.Go);
                }
            }
        }

        /// <summary>从粒子系统推导存活时长（取所有子粒子系统 duration+最大生命周期 的最大值）；无粒子兜底 2 秒。</summary>
        private static float DetectDuration(GameObject go)
        {
            float max = 0f;
            ParticleSystem[] systems = go.GetComponentsInChildren<ParticleSystem>();
            for (int i = 0; i < systems.Length; i++)
            {
                ParticleSystem.MainModule main = systems[i].main;
                float d = main.duration + main.startLifetime.constantMax;
                if (d > max)
                    max = d;
            }
            return max > 0f ? max : 2f;
        }

        private void OnDestroy()
        {
            foreach (KeyValuePair<int, ActiveVfx> kv in _instances)
            {
                if (kv.Value.Go != null)
                    _provider?.Release(kv.Value.Go);
            }
            _instances.Clear();
            _scheduler?.Clear();

            if (_ownsProvider)
                (_provider as IDisposable)?.Dispose();

            if (Manager == this)
                Manager = null;
        }
    }
}
