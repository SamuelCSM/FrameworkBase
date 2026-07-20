using System;
using System.Collections.Generic;

namespace Framework.Foundation
{
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
    /// Provider 注册与快照编排器。登录/重连调用 <see cref="RebuildAll"/>，正常运行由模块直接增量写 Signal。
    /// </summary>
    public sealed class RedDotCoordinator
    {
        /// <summary>接收最终 Provider 快照并负责 DAG 刷新的共享服务。</summary>
        private readonly RedDotService _service;
        /// <summary>按注册顺序保存 Provider，保证 RebuildAll 行为和诊断顺序稳定。</summary>
        private readonly List<IRedDotProvider> _providers = new List<IRedDotProvider>();
        /// <summary>owner 到 Provider 的唯一索引，用于事件驱动的模块级刷新。</summary>
        private readonly Dictionary<string, IRedDotProvider> _providerByOwner =
            new Dictionary<string, IRedDotProvider>(StringComparer.Ordinal);

        public RedDotCoordinator(RedDotService service)
        {
            _service = service ?? throw new ArgumentNullException(nameof(service));
        }

        /// <summary>注册 Provider 及其 Signal 所有权；相同 owner 不允许重复注册。</summary>
        public void Register(IRedDotProvider provider)
        {
            if (provider == null) throw new ArgumentNullException(nameof(provider));
            if (string.IsNullOrWhiteSpace(provider.Owner))
                throw new InvalidOperationException("RedDotProvider.Owner 不能为空。");
            string owner = provider.Owner.Trim();
            if (_providerByOwner.ContainsKey(owner))
                throw new InvalidOperationException($"RedDotProvider 重复注册：{owner}。");

            _service.RegisterProvider(owner, provider.OwnedSignalIds);
            _providerByOwner.Add(owner, provider);
            _providers.Add(provider);
        }

        /// <summary>原子收集全部已就绪 Provider；未就绪 Provider 清零并标记 NotReady。</summary>
        public void RebuildAll()
        {
            using (_service.BeginBatch())
            {
                for (int i = 0; i < _providers.Count; i++) Refresh(_providers[i]);
            }
        }

        /// <summary>刷新指定 owner 的完整快照；适合模块数据变化事件。</summary>
        public void Refresh(string owner)
        {
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
    }
}
