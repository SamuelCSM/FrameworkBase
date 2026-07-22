using System;
using System.Collections.Generic;
using System.Threading;

namespace Framework.Foundation
{
    [Serializable]
    public sealed class TriggerDefinition
    {
        public int Id;
        public string Key;
        public int TypeId;
        public object Payload;
        public string Description;
    }

    [Serializable]
    public sealed class TriggerCatalog
    {
        public int SchemaVersion = 1;
        public TriggerDefinition[] Triggers = Array.Empty<TriggerDefinition>();
    }

    public readonly struct TriggerContext
    {
        public TriggerContext(
            object owner,
            object scope = null,
            object data = null,
            CancellationToken cancellationToken = default)
        {
            Owner = owner;
            Scope = scope;
            Data = data;
            CancellationToken = cancellationToken;
        }

        public object Owner { get; }
        public object Scope { get; }
        public object Data { get; }
        public CancellationToken CancellationToken { get; }
    }

    public readonly struct TriggerSignal
    {
        public TriggerSignal(int triggerId, object data = null)
        {
            TriggerId = triggerId;
            Data = data;
        }

        public int TriggerId { get; }
        public object Data { get; }
    }

    /// <summary>业务或框架模块实现的强类型触发器绑定器。返回句柄统一管理监听生命周期。</summary>
    public interface ITriggerBinder<in TPayload>
    {
        IDisposable Bind(TPayload payload, TriggerContext context, Action<object> onTriggered);
    }

    /// <summary>通用触发器服务。Catalog 初始化后不可替换；Bind/BindOnce 均返回作用域句柄。</summary>
    public sealed class TriggerService
    {
        private interface IBinderAdapter
        {
            Type PayloadType { get; }
            IDisposable Bind(object payload, TriggerContext context, Action<object> onTriggered);
        }

        private sealed class BinderAdapter<TPayload> : IBinderAdapter
        {
            private readonly ITriggerBinder<TPayload> _binder;

            public BinderAdapter(ITriggerBinder<TPayload> binder)
            {
                _binder = binder ?? throw new ArgumentNullException(nameof(binder));
            }

            public Type PayloadType => typeof(TPayload);

            public IDisposable Bind(object payload, TriggerContext context, Action<object> onTriggered)
                => _binder.Bind((TPayload)payload, context, onTriggered);
        }

        private sealed class OnceSubscription : IDisposable
        {
            private TriggerService _owner;
            private Action<TriggerSignal> _handler;
            private IDisposable _inner;
            private bool _fired;
            private bool _disposed;

            public OnceSubscription(TriggerService owner, Action<TriggerSignal> handler)
            {
                _owner = owner;
                _handler = handler;
            }

            public void Attach(IDisposable inner)
            {
                if (inner == null) throw new InvalidOperationException("Trigger Binder 返回了 null 句柄。");
                if (_disposed || _fired)
                {
                    inner.Dispose();
                    return;
                }
                _inner = inner;
            }

            public void Fire(TriggerSignal signal)
            {
                if (_disposed || _fired) return;
                _fired = true;
                Action<TriggerSignal> handler = _handler;
                TriggerService owner = _owner;
                Dispose();
                owner?.InvokeIsolated(handler, signal);
            }

            public void Dispose()
            {
                if (_disposed) return;
                _disposed = true;
                IDisposable inner = _inner;
                _inner = null;
                _handler = null;
                _owner = null;
                inner?.Dispose();
            }
        }

        private sealed class Subscription : IDisposable
        {
            private IDisposable _inner;

            public Subscription(IDisposable inner)
            {
                _inner = inner ?? throw new InvalidOperationException("Trigger Binder 返回了 null 句柄。");
            }

            public void Dispose()
            {
                IDisposable inner = _inner;
                _inner = null;
                inner?.Dispose();
            }
        }

        private readonly Dictionary<int, IBinderAdapter> _binders =
            new Dictionary<int, IBinderAdapter>();
        private readonly Dictionary<int, TriggerDefinition> _definitions =
            new Dictionary<int, TriggerDefinition>();

        public bool IsInitialized { get; private set; }
        public TriggerCatalog Catalog { get; private set; }
        public Action<Exception> ObserverErrorSink { get; set; }

        public void Register<TPayload>(int typeId, ITriggerBinder<TPayload> binder)
        {
            if (typeId <= 0) throw new ArgumentOutOfRangeException(nameof(typeId));
            if (IsInitialized)
                throw new InvalidOperationException("TriggerService 已初始化，不能再注册 Binder。");
            if (_binders.ContainsKey(typeId))
                throw new InvalidOperationException($"Trigger Binder TypeId 重复：{typeId}。");
            _binders.Add(typeId, new BinderAdapter<TPayload>(binder));
        }

        public void Initialize(TriggerCatalog catalog)
        {
            if (IsInitialized) throw new InvalidOperationException("TriggerService 已初始化，不能替换 Catalog。");
            if (catalog == null) throw new ArgumentNullException(nameof(catalog));
            TriggerDefinition[] definitions = catalog.Triggers ?? Array.Empty<TriggerDefinition>();
            for (int i = 0; i < definitions.Length; i++)
            {
                TriggerDefinition definition = definitions[i]
                    ?? throw new InvalidOperationException($"Triggers[{i}] 为空。");
                if (definition.Id <= 0) throw new InvalidOperationException("Trigger Id 必须大于 0。");
                if (_definitions.ContainsKey(definition.Id))
                    throw new InvalidOperationException($"Trigger Id 重复：{definition.Id}。");
                if (!_binders.TryGetValue(definition.TypeId, out IBinderAdapter binder))
                    throw new InvalidOperationException(
                        $"Trigger {definition.Id} 引用了未注册 Binder TypeId={definition.TypeId}。");
                if (!PayloadMatches(binder.PayloadType, definition.Payload))
                    throw new InvalidOperationException(
                        $"Trigger {definition.Id} Payload 类型应为 {binder.PayloadType.FullName}，" +
                        $"实际为 {definition.Payload?.GetType().FullName ?? "null"}。");
                _definitions.Add(definition.Id, definition);
            }
            Catalog = catalog;
            IsInitialized = true;
        }

        public bool ContainsTrigger(int triggerId) => IsInitialized && _definitions.ContainsKey(triggerId);

        public IDisposable Bind(
            int triggerId,
            TriggerContext context,
            Action<TriggerSignal> onTriggered)
        {
            if (onTriggered == null) throw new ArgumentNullException(nameof(onTriggered));
            TriggerDefinition definition = GetDefinition(triggerId);
            try
            {
                IDisposable inner = _binders[definition.TypeId].Bind(
                    definition.Payload,
                    context,
                    data => InvokeIsolated(onTriggered, new TriggerSignal(triggerId, data)));
                return new Subscription(inner);
            }
            catch (Exception ex)
            {
                Report(ex);
                throw;
            }
        }

        public IDisposable BindOnce(
            int triggerId,
            TriggerContext context,
            Action<TriggerSignal> onTriggered)
        {
            if (onTriggered == null) throw new ArgumentNullException(nameof(onTriggered));
            TriggerDefinition definition = GetDefinition(triggerId);
            var once = new OnceSubscription(this, onTriggered);
            try
            {
                IDisposable inner = _binders[definition.TypeId].Bind(
                    definition.Payload,
                    context,
                    data => once.Fire(new TriggerSignal(triggerId, data)));
                once.Attach(inner);
                return once;
            }
            catch (Exception ex)
            {
                once.Dispose();
                Report(ex);
                throw;
            }
        }

        private TriggerDefinition GetDefinition(int triggerId)
        {
            if (!IsInitialized) throw new InvalidOperationException("TriggerService 尚未初始化。");
            if (!_definitions.TryGetValue(triggerId, out TriggerDefinition definition))
                throw new KeyNotFoundException($"Trigger ID 不存在：{triggerId}。");
            return definition;
        }

        private static bool PayloadMatches(Type expected, object payload)
            => payload != null ? expected.IsInstanceOfType(payload) : !expected.IsValueType;

        private void InvokeIsolated(Action<TriggerSignal> handler, TriggerSignal signal)
        {
            try { handler?.Invoke(signal); }
            catch (Exception ex) { Report(ex); }
        }

        private void Report(Exception error)
        {
            try { ObserverErrorSink?.Invoke(error); }
            catch { }
        }
    }
}
