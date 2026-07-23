using System;
using System.Collections.Generic;

namespace Framework.Foundation
{
    /// <summary>
    /// Provider 的模块作用域写入器。只允许写入该 Provider 在
    /// <see cref="IRedDotProvider.OwnedSignalIds"/> 中声明的 Signal。
    /// </summary>
    public interface IRedDotWriter
    {
        /// <summary>拥有当前写入作用域的 Provider 名称。</summary>
        string Owner { get; }

        /// <summary>写入一个 Signal 的非负绝对计数。</summary>
        void SetCount(int signalId, int count);

        /// <summary>按显隐语义写入 0/1。</summary>
        void SetBool(int signalId, bool visible);

        /// <summary>批量更新多个已声明 Signal；最外层释放时统一聚合并通知。</summary>
        IDisposable BeginBatch();

        /// <summary>重新收集当前 Provider 的完整快照；仅用于整包数据替换、重连校准等低频事件。</summary>
        void RefreshSnapshot();
    }

    /// <summary>业务模块红点来源：拥有固定 Signal 集合，并可产出该集合的完整快照。</summary>
    public interface IRedDotProvider
    {
        /// <summary>模块级唯一所有者名称，用于注册、刷新和诊断。</summary>
        string Owner { get; }

        /// <summary>该 Provider 独占写入的完整 Signal 集合；注册后不可改变。</summary>
        IReadOnlyCollection<int> OwnedSignalIds { get; }

        /// <summary>当前账号依赖数据是否已加载完成；未就绪时框架会清零其旧快照。</summary>
        bool IsReady { get; }

        /// <summary>向缓冲写入当前模块的完整 Signal 快照；遗漏项会在替换时自动归零。</summary>
        void Collect(RedDotUpdateBuffer buffer);
    }

    /// <summary>
    /// 可选的响应式 Provider：在完整快照之外监听 Model 领域事件，并通过模块作用域 Writer
    /// 精确更新受影响 Signal。返回值代表整组监听的统一释放句柄，而非只允许一条订阅。
    /// </summary>
    public interface IReactiveRedDotProvider
    {
        /// <summary>
        /// 建立当前账号会话的领域事件监听。复杂模块可在一次 Bind 中组合任意数量的订阅；
        /// Coordinator 退出时统一释放返回句柄。
        /// </summary>
        IDisposable Bind(IRedDotWriter writer);
    }

    /// <summary>把多条响应式监听组合为一个幂等释放句柄。</summary>
    public sealed class RedDotBindingGroup : IDisposable
    {
        private readonly List<IDisposable> _bindings = new List<IDisposable>();
        private bool _disposed;

        /// <summary>加入一条监听；若组已释放，则立即释放传入句柄。</summary>
        public T Add<T>(T binding) where T : IDisposable
        {
            if (binding == null) return default;
            if (_disposed)
            {
                binding.Dispose();
                return binding;
            }
            _bindings.Add(binding);
            return binding;
        }

        /// <summary>按注册逆序释放全部监听；即使其中一条失败也会继续清理其余句柄。</summary>
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            List<Exception> errors = null;
            for (int i = _bindings.Count - 1; i >= 0; i--)
            {
                try
                {
                    _bindings[i]?.Dispose();
                }
                catch (Exception ex)
                {
                    if (errors == null) errors = new List<Exception>();
                    errors.Add(ex);
                }
            }
            _bindings.Clear();
            if (errors != null) throw new AggregateException("红点响应式监听释放失败。", errors);
        }
    }

    /// <summary>
    /// Provider 写入缓冲。只允许写声明拥有的 Signal，同一 ID 写两次立即抛错。
    /// </summary>
    public sealed class RedDotUpdateBuffer
    {
        /// <summary>Provider 声明拥有的 Signal 集合，用于阻止跨模块写入。</summary>
        private readonly HashSet<int> _owned;
        /// <summary>本次 Collect 已写入的 Signal 绝对值；每个 ID 只允许出现一次。</summary>
        private readonly Dictionary<int, int> _values = new Dictionary<int, int>();

        internal RedDotUpdateBuffer(string owner, IReadOnlyCollection<int> ownedSignalIds)
        {
            Owner = owner;
            _owned = new HashSet<int>(ownedSignalIds ?? throw new ArgumentNullException(nameof(ownedSignalIds)));
        }

        /// <summary>创建该快照的 Provider owner。</summary>
        public string Owner { get; }
        /// <summary>本次已收集值的只读视图，交由服务原子替换。</summary>
        public IReadOnlyDictionary<int, int> Values => _values;

        /// <summary>写入一个 Signal 的非负绝对计数。</summary>
        public void Set(int signalId, int count)
        {
            if (!_owned.Contains(signalId))
                throw new InvalidOperationException($"Provider {Owner} 无权写 Signal {signalId}。");
            if (count < 0)
                throw new ArgumentOutOfRangeException(nameof(count), "红点计数不能为负。");
            if (_values.ContainsKey(signalId))
                throw new InvalidOperationException($"Provider {Owner} 在同一快照中重复写 Signal {signalId}。");
            _values.Add(signalId, count);
        }

        /// <summary>按显隐语义写入 0/1。</summary>
        public void SetBool(int signalId, bool visible) => Set(signalId, visible ? 1 : 0);
    }

    /// <summary>
    /// Provider 注册与账号会话编排器。登录/重连调用 <see cref="RebuildAll"/>；响应式 Provider
    /// 自动绑定领域事件并通过模块作用域 Writer 精确更新 Signal。
    /// </summary>
    public sealed class RedDotCoordinator : IDisposable
    {
        /// <summary>限制响应式 Provider 只能写自身声明 Signal 的作用域实现。</summary>
        private sealed class ScopedWriter : IRedDotWriter
        {
            private readonly RedDotService _service;
            private readonly HashSet<int> _ownedSignalIds;
            private readonly Action _refreshSnapshot;
            private bool _active = true;

            public ScopedWriter(
                RedDotService service,
                string owner,
                HashSet<int> ownedSignalIds,
                Action refreshSnapshot)
            {
                _service = service;
                Owner = owner;
                _ownedSignalIds = ownedSignalIds;
                _refreshSnapshot = refreshSnapshot;
            }

            public string Owner { get; }

            public void SetCount(int signalId, int count)
            {
                EnsureActive();
                EnsureOwned(signalId);
                _service.SetCount(signalId, count);
            }

            public void SetBool(int signalId, bool visible) => SetCount(signalId, visible ? 1 : 0);

            public IDisposable BeginBatch()
            {
                EnsureActive();
                return _service.BeginBatch();
            }

            public void RefreshSnapshot()
            {
                EnsureActive();
                _refreshSnapshot();
            }

            public void Deactivate() => _active = false;

            private void EnsureActive()
            {
                if (!_active)
                    throw new ObjectDisposedException(
                        nameof(IRedDotWriter), $"RedDotProvider {Owner} 的账号会话已经结束。");
            }

            private void EnsureOwned(int signalId)
            {
                if (!_ownedSignalIds.Contains(signalId))
                    throw new InvalidOperationException(
                        $"RedDotProvider {Owner} 无权写 Signal {signalId}；请先在 OwnedSignalIds 中声明。");
            }
        }

        /// <summary>接收最终 Provider 快照并负责 DAG 刷新的共享服务。</summary>
        private readonly RedDotService _service;
        /// <summary>按注册顺序保存 Provider，保证 RebuildAll 行为和诊断顺序稳定。</summary>
        private readonly List<IRedDotProvider> _providers = new List<IRedDotProvider>();
        /// <summary>owner 到 Provider 的唯一索引，用于事件驱动的模块级刷新。</summary>
        private readonly Dictionary<string, IRedDotProvider> _providerByOwner =
            new Dictionary<string, IRedDotProvider>(StringComparer.Ordinal);
        /// <summary>所有响应式 Provider 的会话级监听；Coordinator 释放时统一解绑。</summary>
        private readonly RedDotBindingGroup _reactiveBindings = new RedDotBindingGroup();
        /// <summary>已发放的模块作用域 Writer；退出时失效，阻止 Provider 持有旧引用继续写入。</summary>
        private readonly List<ScopedWriter> _scopedWriters = new List<ScopedWriter>();
        /// <summary>Coordinator 是否已经结束当前业务会话。</summary>
        private bool _disposed;

        public RedDotCoordinator(RedDotService service)
        {
            _service = service ?? throw new ArgumentNullException(nameof(service));
        }

        /// <summary>注册 Provider 及其 Signal 所有权；相同 owner 不允许重复注册。</summary>
        public void Register(IRedDotProvider provider)
        {
            EnsureActive();
            if (provider == null) throw new ArgumentNullException(nameof(provider));
            if (string.IsNullOrWhiteSpace(provider.Owner))
                throw new InvalidOperationException("RedDotProvider.Owner 不能为空。");
            string owner = provider.Owner.Trim();
            if (_providerByOwner.ContainsKey(owner))
                throw new InvalidOperationException($"RedDotProvider 重复注册：{owner}。");

            if (provider.OwnedSignalIds == null)
                throw new InvalidOperationException($"RedDotProvider {owner} 的 OwnedSignalIds 不能为空。");
            var ownedSignalIds = new HashSet<int>(provider.OwnedSignalIds);
            if (ownedSignalIds.Count == 0)
                throw new InvalidOperationException($"RedDotProvider {owner} 必须至少声明一个 Signal。");

            _service.RegisterProvider(owner, ownedSignalIds);
            _providerByOwner.Add(owner, provider);
            _providers.Add(provider);

            ScopedWriter scopedWriter = null;
            try
            {
                if (provider is IReactiveRedDotProvider reactive)
                {
                    scopedWriter = new ScopedWriter(
                        _service, owner, ownedSignalIds, () => Refresh(provider));
                    IDisposable binding = reactive.Bind(scopedWriter);
                    if (binding != null) _reactiveBindings.Add(binding);
                    _scopedWriters.Add(scopedWriter);
                }
            }
            catch
            {
                scopedWriter?.Deactivate();
                _providerByOwner.Remove(owner);
                _providers.Remove(provider);
                throw;
            }
        }

        /// <summary>原子收集全部已就绪 Provider；未就绪 Provider 清零并标记 NotReady。</summary>
        public void RebuildAll()
        {
            EnsureActive();
            using (_service.BeginBatch())
            {
                for (int i = 0; i < _providers.Count; i++) Refresh(_providers[i]);
            }
        }

        /// <summary>刷新指定 owner 的完整快照；适合整包数据替换、重连校准等低频场景。</summary>
        public void Refresh(string owner)
        {
            EnsureActive();
            if (string.IsNullOrWhiteSpace(owner) || !_providerByOwner.TryGetValue(owner.Trim(), out IRedDotProvider provider))
                throw new KeyNotFoundException($"RedDotProvider 未注册：{owner}。");
            Refresh(provider);
        }

        private void Refresh(IRedDotProvider provider)
        {
            if (!provider.IsReady)
            {
                _service.MarkProviderNotReady(provider.Owner);
                return;
            }

            var buffer = new RedDotUpdateBuffer(provider.Owner, provider.OwnedSignalIds);
            provider.Collect(buffer);
            _service.ReplaceProviderSnapshot(provider.Owner, buffer.Values);
        }

        /// <summary>
        /// 结束当前业务会话：先解绑全部领域事件，再把已注册 Provider 标记为未就绪并清零其 Signal。
        /// </summary>
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            Exception bindingError = null;
            try
            {
                _reactiveBindings.Dispose();
            }
            catch (Exception ex)
            {
                bindingError = ex;
            }

            for (int i = 0; i < _scopedWriters.Count; i++) _scopedWriters[i].Deactivate();
            _scopedWriters.Clear();

            using (_service.BeginBatch())
            {
                for (int i = 0; i < _providers.Count; i++)
                    _service.MarkProviderNotReady(_providers[i].Owner);
            }
            _providers.Clear();
            _providerByOwner.Clear();

            if (bindingError != null) throw bindingError;
        }

        private void EnsureActive()
        {
            if (_disposed) throw new ObjectDisposedException(nameof(RedDotCoordinator));
        }
    }
}
