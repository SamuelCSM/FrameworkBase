using System;
using System.Collections.Generic;
using System.Linq;

namespace Framework.Foundation
{
    /// <summary>一条可持久化的弱提示已看版本。</summary>
    [Serializable]
    public sealed class RedDotSeenRecord
    {
        /// <summary>弱提示 Signal 的稳定 ID。</summary>
        public int SignalId;

        /// <summary>该保存范围内已确认过的最高内容版本。</summary>
        public int LastSeenVersion;

        public RedDotSeenRecord() { }

        public RedDotSeenRecord(int signalId, int lastSeenVersion)
        {
            SignalId = signalId;
            LastSeenVersion = lastSeenVersion;
        }
    }

    /// <summary>红点节点运行时诊断快照。</summary>
    public readonly struct RedDotNodeSnapshot
    {
        internal RedDotNodeSnapshot(
            RedDotNodeDefinition definition,
            int rawCount,
            int effectiveCount,
            int finalCount,
            int[] parentIds,
            int[] childIds,
            string provider,
            bool providerReady,
            RedDotSeenPolicyDefinition seenPolicy,
            int lastSeenVersion)
        {
            Id = definition.Id;
            Key = definition.Key;
            ModuleId = definition.ModuleId;
            Kind = definition.Kind;
            Aggregation = definition.Aggregation;
            RawCount = rawCount;
            EffectiveCount = effectiveCount;
            FinalCount = finalCount;
            ParentIds = parentIds;
            ChildIds = childIds;
            Provider = provider;
            ProviderReady = providerReady;
            SeenPolicy = seenPolicy;
            LastSeenVersion = lastSeenVersion;
        }

        /// <summary>节点稳定 ID。</summary>
        public int Id { get; }
        /// <summary>节点可读 Key。</summary>
        public string Key { get; }
        /// <summary>配置归属模块 ID。</summary>
        public int ModuleId { get; }
        /// <summary>Signal 或 Aggregate。</summary>
        public RedDotNodeKind Kind { get; }
        /// <summary>节点配置的聚合算法。</summary>
        public RedDotAggregation Aggregation { get; }
        /// <summary>业务写入 Signal 的原始值；Aggregate 恒为 0。</summary>
        public int RawCount { get; }
        /// <summary>应用已看策略后的 Signal 值，或 Aggregate 的计算值。</summary>
        public int EffectiveCount { get; }
        /// <summary>UI 与父节点实际读取的最终值。</summary>
        public int FinalCount { get; }
        /// <summary>直接依赖当前节点的父节点 ID。</summary>
        public int[] ParentIds { get; }
        /// <summary>当前节点直接依赖的子节点 ID。</summary>
        public int[] ChildIds { get; }
        /// <summary>Signal 所属 Provider；无 Provider 或 Aggregate 时为空。</summary>
        public string Provider { get; }
        /// <summary>所属 Provider 是否已经提交当前账号的有效快照。</summary>
        public bool ProviderReady { get; }
        /// <summary>弱提示已看策略；业务状态红点及 Aggregate 为空。</summary>
        public RedDotSeenPolicyDefinition SeenPolicy { get; }
        /// <summary>当前保存范围内已确认的最高版本。</summary>
        public int LastSeenVersion { get; }
    }

    /// <summary>
    /// 配置驱动的红点 DAG 服务。
    /// <para>
    /// 业务只写 Signal；Aggregate 按配置聚合。支持同一 Signal 连接多个入口、模块完整快照、
    /// 运行时增量更新、弱提示已看版本与同步订阅。线程约定：仅主线程访问。
    /// </para>
    /// </summary>
    public sealed class RedDotService
    {
        /// <summary>
        /// 单个配置节点的运行态。关系同时保存 Parents 与 Children，以便从脏 Signal 向上增量传播，
        /// 也能从 Aggregate 向下解释来源；所有实例只由 Initialize 构建并在主线程访问。
        /// </summary>
        private sealed class Node
        {
            /// <summary>不可变的配置定义；Id、Kind、Aggregation 等均从此读取。</summary>
            public RedDotNodeDefinition Definition;

            /// <summary>直接依赖当前节点的 Aggregate；一个节点可同时服务多个 UI 入口。</summary>
            public readonly List<Node> Parents = new List<Node>();

            /// <summary>当前 Aggregate 直接依赖的节点；Signal 的集合必须为空。</summary>
            public readonly List<Node> Children = new List<Node>();

            /// <summary>
            /// SumUniqueSignals 专用的去重叶子集合；初始化阶段预计算，其余聚合类型保持为空。
            /// </summary>
            public HashSet<int> UniqueSignalIds;

            /// <summary>叶子到根的拓扑序号，保证刷新时先算依赖节点、再算父节点。</summary>
            public int TopologicalIndex;

            /// <summary>业务或 Provider 写入 Signal 的绝对值；Aggregate 不直接写入。</summary>
            public int RawCount;

            /// <summary>Signal 应用 SeenPolicy 后的值；Aggregate 与 FinalCount 相同。</summary>
            public int EffectiveCount;

            /// <summary>订阅者、GetCount 与父节点聚合读取的最终可见值。</summary>
            public int FinalCount;

            /// <summary>
            /// 增量刷新的受影响标记；仅在单次 <see cref="FlushDirty"/> 内为 true，用于零分配去重，
            /// 替代旧实现每次 Flush 都新建的 HashSet。刷新结束后必须复位为 false。
            /// </summary>
            public bool Affected;
        }

        /// <summary>单个订阅的可释放句柄；Dispose 会从服务索引中解除回调。</summary>
        private sealed class Subscription : IDisposable
        {
            /// <summary>拥有该订阅的服务；Dispose 后置空，保证释放幂等。</summary>
            private RedDotService _owner;
            /// <summary>订阅的稳定红点 ID。</summary>
            public readonly int Id;
            /// <summary>最终值变化回调；Dispose 后置空，通知快照可安全跳过。</summary>
            public Action<int> Handler;

            public Subscription(RedDotService owner, int id, Action<int> handler)
            {
                _owner = owner;
                Id = id;
                Handler = handler;
            }

            public void Dispose()
            {
                RedDotService owner = _owner;
                _owner = null;
                Handler = null;
                owner?.RemoveSubscription(this);
            }
        }

        /// <summary>支持嵌套的批处理作用域；只由最外层 Dispose 触发一次刷新。</summary>
        private sealed class BatchScope : IDisposable
        {
            /// <summary>拥有作用域的服务；Dispose 后置空，保证释放幂等。</summary>
            private RedDotService _owner;

            public BatchScope(RedDotService owner)
            {
                _owner = owner;
                owner._batchDepth++;
            }

            public void Dispose()
            {
                RedDotService owner = _owner;
                if (owner == null) return;
                _owner = null;
                owner.EndBatch();
            }
        }

        /// <summary>稳定 ID 到全部运行时节点的主索引。</summary>
        private readonly Dictionary<int, Node> _nodes = new Dictionary<int, Node>();
        /// <summary>可读 Key 到稳定 ID 的诊断/迁移索引；运行时业务写入应使用生成 ID。</summary>
        private readonly Dictionary<string, int> _keyIndex = new Dictionary<string, int>(StringComparer.Ordinal);
        /// <summary>Signal ID 到弱提示已看策略的索引；未收录即为业务状态红点。</summary>
        private readonly Dictionary<int, RedDotSeenPolicyDefinition> _seenPolicies =
            new Dictionary<int, RedDotSeenPolicyDefinition>();
        /// <summary>红点 ID 到订阅句柄列表；允许在服务初始化前先登记。</summary>
        private readonly Dictionary<int, List<Subscription>> _subscriptions =
            new Dictionary<int, List<Subscription>>();
        /// <summary>Signal ID 到唯一 Provider owner 的反向所有权索引。</summary>
        private readonly Dictionary<int, string> _providerBySignal = new Dictionary<int, string>();
        /// <summary>Provider owner 到其完整 Signal 集合；用于快照缺项自动清零。</summary>
        private readonly Dictionary<string, HashSet<int>> _signalsByProvider =
            new Dictionary<string, HashSet<int>>(StringComparer.Ordinal);
        /// <summary>Provider 是否已为当前账号提交有效快照。</summary>
        private readonly Dictionary<string, bool> _providerReady =
            new Dictionary<string, bool>(StringComparer.Ordinal);
        /// <summary>仅当前业务会话有效的已看版本，不落盘。</summary>
        private readonly Dictionary<int, int> _sessionSeen = new Dictionary<int, int>();
        /// <summary>当前账号本地存档加载的已看版本。</summary>
        private readonly Dictionary<int, int> _localSeen = new Dictionary<int, int>();
        /// <summary>服务端账号范围的已看版本缓存；由业务同步后导入。</summary>
        private readonly Dictionary<int, int> _serverSeen = new Dictionary<int, int>();
        /// <summary>待重新计算的 Signal ID；Flush 时扩展为全部受影响祖先。</summary>
        private readonly HashSet<int> _dirtySignals = new HashSet<int>();
        /// <summary>从叶子到根稳定排序的全部节点，用于确定性增量求值。</summary>
        private readonly List<Node> _topologicalNodes = new List<Node>();

        // ── 增量刷新复用缓冲：全部按 Clear() 复用，使稳定态 FlushDirty/Notify 零 GC 分配。 ──
        /// <summary>本次 Flush 受影响的节点集合；按 TopologicalIndex 原地排序后确定性求值。</summary>
        private readonly List<Node> _affectedScratch = new List<Node>();
        /// <summary>脏节点向上传播用的复用队列；Clear() 保留内部数组，不重复分配。</summary>
        private readonly Queue<Node> _propagationQueue = new Queue<Node>();
        /// <summary>本次 Flush 中最终值发生变化、需要通知的节点。</summary>
        private readonly List<Node> _changedScratch = new List<Node>();
        /// <summary>通知前对单个 ID 订阅列表的复用快照，隔离回调内退订造成的列表变动。</summary>
        private readonly List<Subscription> _notifyScratch = new List<Subscription>();
        /// <summary>缓存的拓扑序比较委托，避免每次 Flush 的排序都新建闭包。</summary>
        private readonly Comparison<Node> _topologicalComparison =
            (a, b) => a.TopologicalIndex.CompareTo(b.TopologicalIndex);

        /// <summary>Initialize 安装的不可变目录引用。</summary>
        private RedDotCatalog _catalog;
        /// <summary>当前嵌套批处理深度；为 0 时写入会立即刷新。</summary>
        private int _batchDepth;
        /// <summary>是否正在同步通知订阅者；通知栈内禁止重入修改服务。</summary>
        private bool _isNotifying;
        /// <summary>
        /// 是否启用帧末合并：为 true 时写入只标脏，由帧驱动调用 <see cref="FlushPending"/> 统一刷新，
        /// 避免一帧内多次聚合与 UI 刷新；读接口按需先行结算，保证读到自己的写入。
        /// </summary>
        private bool _frameCoalescing;

        /// <summary>目录是否已成功校验并构建运行时 DAG。</summary>
        public bool IsInitialized { get; private set; }
        /// <summary>当前安装的只读语义目录；初始化前为空。</summary>
        public RedDotCatalog Catalog => _catalog;

        /// <summary>订阅者异常诊断出口；异常会被隔离，不影响其他订阅者。</summary>
        public Action<Exception> ObserverErrorSink { get; set; }

        /// <summary>
        /// ServerAccount 范围已看版本发生变化时触发（Acknowledge 或 MergeSeen 导致上升）。
        /// 供服务端同步后端按需上报；去抖、重试与协议由订阅方实现，框架不在此处发起网络。
        /// </summary>
        public event Action ServerSeenChanged;

        /// <summary>当前是否启用帧末合并模式。</summary>
        public bool IsFrameCoalescingEnabled => _frameCoalescing;

        /// <summary>存在已标脏但尚未结算的红点；帧驱动据此决定是否需要调用 <see cref="FlushPending"/>。</summary>
        public bool HasPendingUpdates => _dirtySignals.Count > 0;

        /// <summary>
        /// 启用/关闭帧末合并。启用后，批处理之外的写入不再逐笔立即刷新，而是攒到帧末由
        /// <see cref="FlushPending"/> 统一结算一次；关闭时会立即把已累积的脏结算掉，避免残留。
        /// 未启用时行为与历史一致（逐笔即时刷新）。
        /// </summary>
        public void SetFrameCoalescing(bool enabled)
        {
            EnsureInitialized();
            if (_frameCoalescing == enabled) return;
            _frameCoalescing = enabled;
            if (!enabled) FlushPending();
        }

        /// <summary>
        /// 由帧驱动（如 LateUpdate）调用：把本帧累积的脏红点统一结算并通知一次。
        /// 批处理进行中或正在通知回调栈内均跳过；返回是否真的发生了结算。
        /// </summary>
        public bool FlushPending()
        {
            if (_batchDepth > 0 || _isNotifying) return false;
            if (_dirtySignals.Count == 0) return false;
            FlushDirty();
            return true;
        }

        /// <summary>使用完整、已校验的目录一次性构建 DAG；活跃会话中不支持替换拓扑。</summary>
        public void Initialize(RedDotCatalog catalog)
        {
            if (IsInitialized)
                throw new InvalidOperationException("RedDotService 已初始化；活跃会话中不能替换拓扑。");

            RedDotCatalogValidationResult validation = RedDotCatalogValidator.Validate(catalog);
            if (!validation.IsValid)
                throw new InvalidOperationException("红点配置校验失败：" + Environment.NewLine + validation);

            _catalog = catalog;
            RedDotNodeDefinition[] definitions = catalog.Nodes ?? Array.Empty<RedDotNodeDefinition>();
            for (int i = 0; i < definitions.Length; i++)
            {
                RedDotNodeDefinition definition = definitions[i];
                var node = new Node { Definition = definition };
                _nodes.Add(definition.Id, node);
                _keyIndex.Add(definition.Key, definition.Id);
            }

            RedDotEdgeDefinition[] edges = catalog.Edges ?? Array.Empty<RedDotEdgeDefinition>();
            for (int i = 0; i < edges.Length; i++)
            {
                Node parent = _nodes[edges[i].ParentId];
                Node child = _nodes[edges[i].ChildId];
                parent.Children.Add(child);
                child.Parents.Add(parent);
            }

            RedDotSeenPolicyDefinition[] policies = catalog.SeenPolicies ?? Array.Empty<RedDotSeenPolicyDefinition>();
            for (int i = 0; i < policies.Length; i++)
                _seenPolicies.Add(policies[i].SignalId, policies[i]);

            BuildTopologicalOrder();
            PrecomputeUniqueSignalSets();
            IsInitialized = true;

            // 初始化前订阅是允许的（UI 与配置加载时序解耦）；此时再清理无效 ID 并上报。
            if (_subscriptions.Count > 0)
            {
                int[] ids = _subscriptions.Keys.ToArray();
                for (int i = 0; i < ids.Length; i++)
                {
                    int id = ids[i];
                    if (_nodes.ContainsKey(id)) continue;
                    _subscriptions.Remove(id);
                    ReportObserverError(new KeyNotFoundException($"红点订阅 ID 不存在：{id}。"));
                }
            }
        }

        public bool Contains(int id) => IsInitialized && _nodes.ContainsKey(id);

        public bool TryResolveId(string key, out int id)
        {
            id = 0;
            return IsInitialized && !string.IsNullOrEmpty(key) && _keyIndex.TryGetValue(key, out id);
        }

        public bool TryGetDefinition(int id, out RedDotNodeDefinition definition)
        {
            definition = null;
            if (!IsInitialized || !_nodes.TryGetValue(id, out Node node)) return false;
            definition = node.Definition;
            return true;
        }

        /// <summary>读取节点最终值；未初始化时返回 0，初始化后未知 ID 属配置错误并抛异常。</summary>
        public int GetCount(int id)
        {
            if (!IsInitialized) return 0;
            EnsureUpToDate();
            return GetNode(id).FinalCount;
        }

        /// <summary>设置业务 Signal 的绝对值；负数与 Aggregate 写入立即抛错。</summary>
        public void SetCount(int signalId, int count)
        {
            EnsureMutable();
            if (count < 0) throw new ArgumentOutOfRangeException(nameof(count), "红点计数不能为负。");

            Node signal = GetSignal(signalId);
            if (signal.RawCount == count) return;
            signal.RawCount = count;
            _dirtySignals.Add(signalId);
            FlushIfNeeded();
        }

        /// <summary>增量修改 Signal；结果小于 0 时按 0 截断。</summary>
        public void AddCount(int signalId, int delta)
        {
            EnsureMutable();
            Node signal = GetSignal(signalId);
            long next = (long)signal.RawCount + delta;
            SetCount(signalId, next <= 0 ? 0 : next >= int.MaxValue ? int.MaxValue : (int)next);
        }

        /// <summary>
        /// 注册模块 Provider 对 Signal 的唯一所有权。重复所有权、未知 ID、Aggregate 均立即抛错。
        /// </summary>
        public void RegisterProvider(string owner, IEnumerable<int> ownedSignalIds)
        {
            EnsureInitialized();
            if (string.IsNullOrWhiteSpace(owner)) throw new ArgumentException("Provider owner 不能为空。", nameof(owner));
            if (ownedSignalIds == null) throw new ArgumentNullException(nameof(ownedSignalIds));

            string normalizedOwner = owner.Trim();
            var owned = new HashSet<int>();
            foreach (int id in ownedSignalIds)
            {
                GetSignal(id);
                if (!owned.Add(id))
                    throw new InvalidOperationException($"Provider {normalizedOwner} 重复声明 Signal {id}。");
                if (_providerBySignal.TryGetValue(id, out string existing) &&
                    !string.Equals(existing, normalizedOwner, StringComparison.Ordinal))
                    throw new InvalidOperationException($"Signal {id} 已由 Provider {existing} 拥有，不能再分配给 {normalizedOwner}。");
            }

            // Provider 对象可随账号会话重建；相同 owner + 相同所有权属于幂等重注册。
            if (_signalsByProvider.TryGetValue(normalizedOwner, out HashSet<int> existingOwned))
            {
                if (!existingOwned.SetEquals(owned))
                    throw new InvalidOperationException($"Provider {normalizedOwner} 重注册时 Signal 所有权发生变化。");
                _providerReady[normalizedOwner] = false;
                return;
            }

            _signalsByProvider.Add(normalizedOwner, owned);
            _providerReady.Add(normalizedOwner, false);
            foreach (int id in owned)
                if (!_providerBySignal.ContainsKey(id)) _providerBySignal.Add(id, normalizedOwner);
        }

        /// <summary>
        /// 原子替换某 Provider 的完整快照：它拥有但本次未提交的 Signal 自动归零。
        /// </summary>
        public void ReplaceProviderSnapshot(string owner, IReadOnlyDictionary<int, int> values)
        {
            EnsureMutable();
            if (string.IsNullOrWhiteSpace(owner)) throw new ArgumentException("Provider owner 不能为空。", nameof(owner));
            string normalizedOwner = owner.Trim();
            if (!_signalsByProvider.TryGetValue(normalizedOwner, out HashSet<int> owned))
                throw new InvalidOperationException($"红点 Provider 未注册：{normalizedOwner}。");
            values = values ?? EmptyValues.Instance;

            foreach (KeyValuePair<int, int> pair in values)
            {
                if (!owned.Contains(pair.Key))
                    throw new InvalidOperationException($"Provider {normalizedOwner} 无权写 Signal {pair.Key}。");
                if (pair.Value < 0)
                    throw new ArgumentOutOfRangeException(nameof(values), $"Signal {pair.Key} 的计数不能为负。");
            }

            using (BeginBatch())
            {
                foreach (int id in owned)
                    SetCount(id, values.TryGetValue(id, out int count) ? count : 0);
                _providerReady[normalizedOwner] = true;
            }
        }

        /// <summary>标记 Provider 未就绪并清零其全部 Signal，避免残留旧账号/旧快照值。</summary>
        public void MarkProviderNotReady(string owner)
        {
            EnsureMutable();
            if (string.IsNullOrWhiteSpace(owner) || !_signalsByProvider.TryGetValue(owner.Trim(), out HashSet<int> owned))
                return;
            using (BeginBatch())
            {
                foreach (int id in owned) SetCount(id, 0);
                _providerReady[owner.Trim()] = false;
            }
        }

        public bool IsProviderReady(string owner)
            => !string.IsNullOrWhiteSpace(owner) &&
               _providerReady.TryGetValue(owner.Trim(), out bool ready) && ready;

        /// <summary>进入批处理；最外层 Dispose 时统一聚合和通知最终值。</summary>
        public IDisposable BeginBatch()
        {
            EnsureMutable();
            return new BatchScope(this);
        }

        /// <summary>
        /// 确认弱提示。策略不存在或触发时机不匹配时返回 false，业务状态红点不会被 UI 确认清除。
        /// </summary>
        public bool Acknowledge(int signalId, RedDotAcknowledgeTrigger trigger)
        {
            EnsureMutable();
            GetSignal(signalId);
            if (!_seenPolicies.TryGetValue(signalId, out RedDotSeenPolicyDefinition policy)) return false;
            if (policy.Trigger != trigger) return false;

            Dictionary<int, int> store = SeenStore(policy.SaveMode);
            if (store.TryGetValue(signalId, out int oldVersion) && oldVersion >= policy.Version) return false;
            store[signalId] = policy.Version;
            _dirtySignals.Add(signalId);
            FlushIfNeeded();
            if (policy.SaveMode == RedDotSeenSaveMode.ServerAccount) RaiseServerSeenChanged();
            return true;
        }

        /// <summary>导入当前账号的已看记录；只接收与策略 SaveMode 相符的有效 Signal。</summary>
        public void ImportSeen(RedDotSeenSaveMode mode, IEnumerable<RedDotSeenRecord> records)
        {
            EnsureMutable();
            Dictionary<int, int> store = SeenStore(mode);
            using (BeginBatch())
            {
                store.Clear();
                if (records != null)
                {
                    foreach (RedDotSeenRecord record in records)
                    {
                        if (record == null || record.SignalId <= 0 || record.LastSeenVersion <= 0) continue;
                        if (!_seenPolicies.TryGetValue(record.SignalId, out RedDotSeenPolicyDefinition policy) ||
                            policy.SaveMode != mode) continue;
                        if (!store.TryGetValue(record.SignalId, out int old) || record.LastSeenVersion > old)
                            store[record.SignalId] = record.LastSeenVersion;
                    }
                }

                foreach (KeyValuePair<int, RedDotSeenPolicyDefinition> pair in _seenPolicies)
                    if (pair.Value.SaveMode == mode) _dirtySignals.Add(pair.Key);
            }
        }

        /// <summary>
        /// 非破坏性合并已看记录：逐 Signal 取更高版本，不清空既有值。用于服务端拉取结果与本地已看
        /// 状态的冲突合并（约定"取 max 版本"）。返回本次实际使某条记录上升的合并结果，供调用方判断
        /// 是否需要回写。此方法不触发 <see cref="ServerSeenChanged"/>：合并多为服务端→本地方向，
        /// 由同步编排显式比较后决定回推，避免把刚拉取的数据原样回推。
        /// </summary>
        public bool MergeSeen(RedDotSeenSaveMode mode, IEnumerable<RedDotSeenRecord> records)
        {
            EnsureMutable();
            if (records == null) return false;
            Dictionary<int, int> store = SeenStore(mode);
            bool changed = false;
            using (BeginBatch())
            {
                foreach (RedDotSeenRecord record in records)
                {
                    if (record == null || record.SignalId <= 0 || record.LastSeenVersion <= 0) continue;
                    if (!_seenPolicies.TryGetValue(record.SignalId, out RedDotSeenPolicyDefinition policy) ||
                        policy.SaveMode != mode) continue;
                    if (store.TryGetValue(record.SignalId, out int old) && old >= record.LastSeenVersion) continue;
                    store[record.SignalId] = record.LastSeenVersion;
                    _dirtySignals.Add(record.SignalId);
                    changed = true;
                }
            }
            return changed;
        }

        private void RaiseServerSeenChanged()
        {
            try { ServerSeenChanged?.Invoke(); }
            catch (Exception ex) { ReportObserverError(ex); }
        }

        public IReadOnlyList<RedDotSeenRecord> ExportSeen(RedDotSeenSaveMode mode)
        {
            EnsureInitialized();
            Dictionary<int, int> store = SeenStore(mode);
            return store.OrderBy(pair => pair.Key)
                .Select(pair => new RedDotSeenRecord(pair.Key, pair.Value))
                .ToArray();
        }

        /// <summary>
        /// 清除账号运行态：全部 Signal 归零、Provider 变为未就绪、Session/Local/Server 已看缓存清空。
        /// 订阅与拓扑保留，现有 UI 会收到最终 0。
        /// </summary>
        public void ResetAccountState()
        {
            EnsureMutable();
            using (BeginBatch())
            {
                _sessionSeen.Clear();
                _localSeen.Clear();
                _serverSeen.Clear();
                foreach (Node node in _nodes.Values)
                {
                    if (node.Definition.Kind != RedDotNodeKind.Signal) continue;
                    if (node.RawCount != 0) node.RawCount = 0;
                    _dirtySignals.Add(node.Definition.Id);
                }
                string[] providers = _providerReady.Keys.ToArray();
                for (int i = 0; i < providers.Length; i++) _providerReady[providers[i]] = false;
            }
        }

        /// <summary>订阅最终值。可在目录初始化前调用；默认立即以 0/当前值回调一次。</summary>
        public IDisposable Subscribe(int id, Action<int> handler, bool notifyImmediately = true)
        {
            if (id <= 0) throw new ArgumentOutOfRangeException(nameof(id), "红点 ID 必须大于 0。");
            if (handler == null) throw new ArgumentNullException(nameof(handler));
            if (IsInitialized && !_nodes.ContainsKey(id)) throw new KeyNotFoundException($"红点 ID 不存在：{id}。");

            if (!_subscriptions.TryGetValue(id, out List<Subscription> list))
            {
                list = new List<Subscription>();
                _subscriptions.Add(id, list);
            }
            var subscription = new Subscription(this, id, handler);
            list.Add(subscription);
            if (notifyImmediately) InvokeIsolated(handler, GetCount(id));
            return subscription;
        }

        /// <summary>按 ID 稳定排序输出全部节点诊断快照。</summary>
        public IReadOnlyList<RedDotNodeSnapshot> Snapshot()
        {
            EnsureInitialized();
            EnsureUpToDate();
            return _nodes.Values.OrderBy(node => node.Definition.Id).Select(CreateSnapshot).ToArray();
        }

        /// <summary>返回使指定节点亮起的有效底层 Signal，供 reddot explain 使用。</summary>
        public IReadOnlyList<RedDotNodeSnapshot> GetActiveSignalSources(int id)
        {
            EnsureInitialized();
            EnsureUpToDate();
            Node node = GetNode(id);
            var ids = new HashSet<int>();
            CollectSignalIds(node, ids, new HashSet<int>());
            return ids.Select(signalId => _nodes[signalId])
                .Where(signal => signal.EffectiveCount > 0)
                .OrderBy(signal => signal.Definition.Id)
                .Select(CreateSnapshot)
                .ToArray();
        }

        /// <summary>
        /// 从入口节点沿"有值"的子边逐层深入，返回一条到最深亮起 Signal 的路径（含入口本身）。
        /// 供"点击红点跳转到具体来源"使用：UI 可据此把玩家一路带到点亮红点的叶子功能。
        /// 入口未点亮（FinalCount==0）时返回空列表。每层在多个亮起子节点中按
        /// FinalCount 降序、ID 升序确定性择一，因此同一状态下路径稳定可复现。
        /// </summary>
        public IReadOnlyList<RedDotNodeSnapshot> GetActivePath(int id)
        {
            EnsureInitialized();
            EnsureUpToDate();
            Node node = GetNode(id);
            var path = new List<RedDotNodeSnapshot>();
            if (node.FinalCount <= 0) return path;

            var visited = new HashSet<int>();
            while (node != null && visited.Add(node.Definition.Id))
            {
                path.Add(CreateSnapshot(node));
                if (node.Definition.Kind == RedDotNodeKind.Signal) break;

                Node best = null;
                for (int i = 0; i < node.Children.Count; i++)
                {
                    Node child = node.Children[i];
                    if (child.FinalCount <= 0) continue;
                    if (best == null ||
                        child.FinalCount > best.FinalCount ||
                        (child.FinalCount == best.FinalCount && child.Definition.Id < best.Definition.Id))
                        best = child;
                }
                node = best;
            }
            return path;
        }

        private RedDotNodeSnapshot CreateSnapshot(Node node)
        {
            _providerBySignal.TryGetValue(node.Definition.Id, out string provider);
            bool ready = provider != null && IsProviderReady(provider);
            _seenPolicies.TryGetValue(node.Definition.Id, out RedDotSeenPolicyDefinition policy);
            return new RedDotNodeSnapshot(
                node.Definition,
                node.RawCount,
                node.EffectiveCount,
                node.FinalCount,
                node.Parents.Select(parent => parent.Definition.Id).OrderBy(value => value).ToArray(),
                node.Children.Select(child => child.Definition.Id).OrderBy(value => value).ToArray(),
                provider,
                ready,
                policy,
                policy == null ? 0 : GetLastSeenVersion(policy));
        }

        private void EndBatch()
        {
            if (_batchDepth <= 0) throw new InvalidOperationException("红点 BatchScope 计数失衡。");
            _batchDepth--;
            // 帧末合并模式下，批处理结束也只保留脏标记，交由 FlushPending 统一结算。
            if (_batchDepth == 0 && !_frameCoalescing) FlushDirty();
        }

        private void FlushIfNeeded()
        {
            // 批处理中或帧末合并模式下都不即时刷新；后者由帧驱动 FlushPending 统一结算。
            if (_batchDepth == 0 && !_frameCoalescing) FlushDirty();
        }

        /// <summary>
        /// 读接口的"读到自己的写入"保障：仅在帧末合并模式、非批处理、非通知栈内且确有脏时提前结算一次。
        /// 非合并模式下脏在写入时已即时清空，此方法为零成本判空快速返回。
        /// </summary>
        private void EnsureUpToDate()
        {
            if (_frameCoalescing && _batchDepth == 0 && !_isNotifying && _dirtySignals.Count > 0)
                FlushDirty();
        }

        /// <summary>
        /// 增量结算脏 Signal 及其全部受影响祖先。使用节点上的 <see cref="Node.Affected"/> 标记与复用缓冲，
        /// 稳定态零 GC 分配；按拓扑序原地排序保证依赖先于父节点求值，结果确定。
        /// </summary>
        private void FlushDirty()
        {
            if (_dirtySignals.Count == 0) return;

            // 1) 以脏 Signal 为种子，沿父边向上收集受影响节点；Affected 标记去重，替代旧 HashSet。
            _affectedScratch.Clear();
            _propagationQueue.Clear();
            foreach (int id in _dirtySignals)
            {
                Node seed = _nodes[id];
                if (seed.Affected) continue;
                seed.Affected = true;
                _affectedScratch.Add(seed);
                _propagationQueue.Enqueue(seed);
            }
            _dirtySignals.Clear();

            while (_propagationQueue.Count > 0)
            {
                Node child = _propagationQueue.Dequeue();
                for (int i = 0; i < child.Parents.Count; i++)
                {
                    Node parent = child.Parents[i];
                    if (parent.Affected) continue;
                    parent.Affected = true;
                    _affectedScratch.Add(parent);
                    _propagationQueue.Enqueue(parent);
                }
            }

            // 2) 按拓扑序原地排序（复用委托），依赖节点先算；顺带复位 Affected 标记。
            _affectedScratch.Sort(_topologicalComparison);
            _changedScratch.Clear();
            for (int i = 0; i < _affectedScratch.Count; i++)
            {
                Node node = _affectedScratch[i];
                node.Affected = false;
                int old = node.FinalCount;
                if (node.Definition.Kind == RedDotNodeKind.Signal)
                {
                    node.EffectiveCount = EvaluateSignal(node);
                    node.FinalCount = node.EffectiveCount;
                }
                else
                {
                    node.FinalCount = EvaluateAggregate(node);
                    node.EffectiveCount = node.FinalCount;
                }
                if (node.FinalCount != old) _changedScratch.Add(node);
            }
            _affectedScratch.Clear();

            // 3) 通知发生变化的节点；回调栈内禁止再改服务，故复用缓冲不会被重入。
            _isNotifying = true;
            try
            {
                for (int i = 0; i < _changedScratch.Count; i++) Notify(_changedScratch[i]);
            }
            finally
            {
                _isNotifying = false;
                _changedScratch.Clear();
            }
        }

        private int EvaluateSignal(Node signal)
        {
            if (!_seenPolicies.TryGetValue(signal.Definition.Id, out RedDotSeenPolicyDefinition policy))
                return signal.RawCount;
            return policy.Version > GetLastSeenVersion(policy) ? signal.RawCount : 0;
        }

        private int EvaluateAggregate(Node node)
        {
            switch (node.Definition.Aggregation)
            {
                case RedDotAggregation.Any:
                    for (int i = 0; i < node.Children.Count; i++)
                        if (node.Children[i].FinalCount > 0) return 1;
                    return 0;

                case RedDotAggregation.SumChildren:
                    return ClampSum(node.Children.Select(child => child.FinalCount));

                case RedDotAggregation.MaxChildren:
                    int max = 0;
                    for (int i = 0; i < node.Children.Count; i++)
                        if (node.Children[i].FinalCount > max) max = node.Children[i].FinalCount;
                    return max;

                case RedDotAggregation.SumUniqueSignals:
                    return ClampSum(node.UniqueSignalIds.Select(id => _nodes[id].EffectiveCount));

                default:
                    throw new InvalidOperationException(
                        $"Aggregate {node.Definition.Id} [{node.Definition.Key}] 没有有效聚合方式。");
            }
        }

        private static int ClampSum(IEnumerable<int> values)
        {
            long sum = 0;
            foreach (int value in values)
            {
                sum += value;
                if (sum >= int.MaxValue) return int.MaxValue;
            }
            return (int)sum;
        }

        private int GetLastSeenVersion(RedDotSeenPolicyDefinition policy)
        {
            Dictionary<int, int> store = SeenStore(policy.SaveMode);
            return store.TryGetValue(policy.SignalId, out int version) ? version : 0;
        }

        private Dictionary<int, int> SeenStore(RedDotSeenSaveMode mode)
        {
            switch (mode)
            {
                case RedDotSeenSaveMode.Session: return _sessionSeen;
                case RedDotSeenSaveMode.LocalAccount: return _localSeen;
                case RedDotSeenSaveMode.ServerAccount: return _serverSeen;
                default: throw new ArgumentOutOfRangeException(nameof(mode), mode, "未知的已看保存方式。");
            }
        }

        private void BuildTopologicalOrder()
        {
            var remaining = _nodes.Values.ToDictionary(node => node.Definition.Id, node => node.Children.Count);
            var ready = new SortedSet<int>(_nodes.Values.Where(node => node.Children.Count == 0)
                .Select(node => node.Definition.Id));
            while (ready.Count > 0)
            {
                int id = ready.Min;
                ready.Remove(id);
                Node node = _nodes[id];
                node.TopologicalIndex = _topologicalNodes.Count;
                _topologicalNodes.Add(node);
                for (int i = 0; i < node.Parents.Count; i++)
                {
                    int parentId = node.Parents[i].Definition.Id;
                    remaining[parentId]--;
                    if (remaining[parentId] == 0) ready.Add(parentId);
                }
            }
            if (_topologicalNodes.Count != _nodes.Count)
                throw new InvalidOperationException("红点拓扑存在循环依赖。");
        }

        private void PrecomputeUniqueSignalSets()
        {
            foreach (Node node in _nodes.Values)
            {
                if (node.Definition.Aggregation != RedDotAggregation.SumUniqueSignals) continue;
                node.UniqueSignalIds = new HashSet<int>();
                CollectSignalIds(node, node.UniqueSignalIds, new HashSet<int>());
            }
        }

        private static void CollectSignalIds(Node node, HashSet<int> signals, HashSet<int> visited)
        {
            if (!visited.Add(node.Definition.Id)) return;
            if (node.Definition.Kind == RedDotNodeKind.Signal)
            {
                signals.Add(node.Definition.Id);
                return;
            }
            for (int i = 0; i < node.Children.Count; i++)
                CollectSignalIds(node.Children[i], signals, visited);
        }

        private void Notify(Node node)
        {
            if (!_subscriptions.TryGetValue(node.Definition.Id, out List<Subscription> list) || list.Count == 0)
                return;

            // 复用缓冲拷贝一份当前订阅，隔离回调内退订对原列表的修改；Notify 不会被回调重入，
            // 单个复用缓冲即安全，且稳定态零分配（回调禁止改服务，退订仅动 _subscriptions）。
            _notifyScratch.Clear();
            _notifyScratch.AddRange(list);
            int count = node.FinalCount;
            for (int i = 0; i < _notifyScratch.Count; i++)
            {
                Action<int> handler = _notifyScratch[i].Handler;
                if (handler != null) InvokeIsolated(handler, count);
            }
            _notifyScratch.Clear();
        }

        private void InvokeIsolated(Action<int> handler, int count)
        {
            try { handler(count); }
            catch (Exception ex) { ReportObserverError(ex); }
        }

        private void ReportObserverError(Exception exception)
        {
            try { ObserverErrorSink?.Invoke(exception); }
            catch { /* 诊断出口自身异常没有更下游的去处。 */ }
        }

        private void RemoveSubscription(Subscription subscription)
        {
            if (_subscriptions.TryGetValue(subscription.Id, out List<Subscription> list))
            {
                list.Remove(subscription);
                if (list.Count == 0) _subscriptions.Remove(subscription.Id);
            }
        }

        private Node GetNode(int id)
        {
            EnsureInitialized();
            if (!_nodes.TryGetValue(id, out Node node)) throw new KeyNotFoundException($"红点 ID 不存在：{id}。");
            return node;
        }

        private Node GetSignal(int id)
        {
            Node node = GetNode(id);
            if (node.Definition.Kind != RedDotNodeKind.Signal)
                throw new InvalidOperationException($"红点 {id} [{node.Definition.Key}] 是 Aggregate，业务只能写 Signal。");
            return node;
        }

        private void EnsureInitialized()
        {
            if (!IsInitialized) throw new InvalidOperationException("RedDotService 尚未初始化红点目录。");
        }

        private void EnsureMutable()
        {
            EnsureInitialized();
            if (_isNotifying)
                throw new InvalidOperationException("不能在红点订阅回调中修改红点；请由业务事件在回调栈之外写入。");
        }

        /// <summary>ReplaceProviderSnapshot 的共享空只读输入，避免为 null 快照重复分配字典。</summary>
        private sealed class EmptyValues : Dictionary<int, int>
        {
            public static readonly EmptyValues Instance = new EmptyValues();
        }
    }
}
