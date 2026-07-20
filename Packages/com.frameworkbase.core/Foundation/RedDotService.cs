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

        /// <summary>Initialize 安装的不可变目录引用。</summary>
        private RedDotCatalog _catalog;
        /// <summary>当前嵌套批处理深度；为 0 时写入会立即刷新。</summary>
        private int _batchDepth;
        /// <summary>是否正在同步通知订阅者；通知栈内禁止重入修改服务。</summary>
        private bool _isNotifying;

        /// <summary>目录是否已成功校验并构建运行时 DAG。</summary>
        public bool IsInitialized { get; private set; }
        /// <summary>当前安装的只读语义目录；初始化前为空。</summary>
        public RedDotCatalog Catalog => _catalog;

        /// <summary>订阅者异常诊断出口；异常会被隔离，不影响其他订阅者。</summary>
        public Action<Exception> ObserverErrorSink { get; set; }

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
            return _nodes.Values.OrderBy(node => node.Definition.Id).Select(CreateSnapshot).ToArray();
        }

        /// <summary>返回使指定节点亮起的有效底层 Signal，供 reddot explain 使用。</summary>
        public IReadOnlyList<RedDotNodeSnapshot> GetActiveSignalSources(int id)
        {
            EnsureInitialized();
            Node node = GetNode(id);
            var ids = new HashSet<int>();
            CollectSignalIds(node, ids, new HashSet<int>());
            return ids.Select(signalId => _nodes[signalId])
                .Where(signal => signal.EffectiveCount > 0)
                .OrderBy(signal => signal.Definition.Id)
                .Select(CreateSnapshot)
                .ToArray();
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
            if (_batchDepth == 0) FlushDirty();
        }

        private void FlushIfNeeded()
        {
            if (_batchDepth == 0) FlushDirty();
        }

        private void FlushDirty()
        {
            if (_dirtySignals.Count == 0) return;
            var affected = new HashSet<int>(_dirtySignals);
            var queue = new Queue<int>(_dirtySignals);
            while (queue.Count > 0)
            {
                Node child = _nodes[queue.Dequeue()];
                for (int i = 0; i < child.Parents.Count; i++)
                {
                    int parentId = child.Parents[i].Definition.Id;
                    if (affected.Add(parentId)) queue.Enqueue(parentId);
                }
            }

            var changed = new List<Node>();
            foreach (Node node in affected.Select(id => _nodes[id]).OrderBy(value => value.TopologicalIndex))
            {
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
                if (node.FinalCount != old) changed.Add(node);
            }
            _dirtySignals.Clear();

            _isNotifying = true;
            try
            {
                for (int i = 0; i < changed.Count; i++) Notify(changed[i]);
            }
            finally
            {
                _isNotifying = false;
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
            if (!_subscriptions.TryGetValue(node.Definition.Id, out List<Subscription> list)) return;
            Subscription[] snapshot = list.ToArray();
            for (int i = 0; i < snapshot.Length; i++)
            {
                Action<int> handler = snapshot[i].Handler;
                if (handler != null) InvokeIsolated(handler, node.FinalCount);
            }
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
