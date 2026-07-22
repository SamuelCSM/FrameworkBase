using System;
using System.Collections.Generic;
using System.Threading;
using Cysharp.Threading.Tasks;

namespace Framework.Foundation
{
    public enum ActionExecutionStatus
    {
        Succeeded = 0,
        Skipped = 1,
        Failed = 2,
        Cancelled = 3,
    }

    public readonly struct ActionExecutionResult
    {
        private ActionExecutionResult(ActionExecutionStatus status, string message, Exception error)
        {
            Status = status;
            Message = message;
            Error = error;
        }

        public ActionExecutionStatus Status { get; }
        public string Message { get; }
        public Exception Error { get; }
        public bool IsSuccess => Status == ActionExecutionStatus.Succeeded || Status == ActionExecutionStatus.Skipped;

        public static ActionExecutionResult Succeeded(string message = null)
            => new ActionExecutionResult(ActionExecutionStatus.Succeeded, message, null);
        public static ActionExecutionResult Skipped(string message = null)
            => new ActionExecutionResult(ActionExecutionStatus.Skipped, message, null);
        public static ActionExecutionResult Failed(string message, Exception error = null)
            => new ActionExecutionResult(ActionExecutionStatus.Failed, message, error);
        public static ActionExecutionResult Cancelled(string message = null)
            => new ActionExecutionResult(ActionExecutionStatus.Cancelled, message, null);
    }

    public readonly struct ActionContext
    {
        public ActionContext(object owner, object scope = null, object data = null)
        {
            Owner = owner;
            Scope = scope;
            Data = data;
        }

        public object Owner { get; }
        public object Scope { get; }
        public object Data { get; }
    }

    [Serializable]
    public sealed class ActionDefinition
    {
        public int Id;
        public string Key;
        public int TypeId;
        public object Payload;
        public string Description;
    }

    [Serializable]
    public sealed class ActionCatalog
    {
        public int SchemaVersion = 1;
        public ActionDefinition[] Actions = Array.Empty<ActionDefinition>();
    }

    public interface IActionExecutor<in TPayload>
    {
        UniTask<ActionExecutionResult> ExecuteAsync(
            TPayload payload,
            ActionContext context,
            CancellationToken cancellationToken);
    }

    /// <summary>通用异步动作服务。执行器异常被隔离成 Failed 结果，并送 ObserverErrorSink。</summary>
    public sealed class ActionService
    {
        private interface IExecutorAdapter
        {
            Type PayloadType { get; }
            UniTask<ActionExecutionResult> ExecuteAsync(
                object payload,
                ActionContext context,
                CancellationToken cancellationToken);
        }

        private sealed class ExecutorAdapter<TPayload> : IExecutorAdapter
        {
            private readonly IActionExecutor<TPayload> _executor;

            public ExecutorAdapter(IActionExecutor<TPayload> executor)
            {
                _executor = executor ?? throw new ArgumentNullException(nameof(executor));
            }

            public Type PayloadType => typeof(TPayload);

            public UniTask<ActionExecutionResult> ExecuteAsync(
                object payload,
                ActionContext context,
                CancellationToken cancellationToken)
                => _executor.ExecuteAsync((TPayload)payload, context, cancellationToken);
        }

        private readonly Dictionary<int, IExecutorAdapter> _executors =
            new Dictionary<int, IExecutorAdapter>();
        private readonly Dictionary<int, ActionDefinition> _definitions =
            new Dictionary<int, ActionDefinition>();

        public bool IsInitialized { get; private set; }
        public ActionCatalog Catalog { get; private set; }
        public Action<Exception> ObserverErrorSink { get; set; }

        public void Register<TPayload>(int typeId, IActionExecutor<TPayload> executor)
        {
            if (typeId <= 0) throw new ArgumentOutOfRangeException(nameof(typeId));
            if (IsInitialized)
                throw new InvalidOperationException("ActionService 已初始化，不能再注册 Executor。");
            if (_executors.ContainsKey(typeId))
                throw new InvalidOperationException($"Action Executor TypeId 重复：{typeId}。");
            _executors.Add(typeId, new ExecutorAdapter<TPayload>(executor));
        }

        public void Initialize(ActionCatalog catalog)
        {
            if (IsInitialized) throw new InvalidOperationException("ActionService 已初始化，不能替换 Catalog。");
            if (catalog == null) throw new ArgumentNullException(nameof(catalog));
            ActionDefinition[] definitions = catalog.Actions ?? Array.Empty<ActionDefinition>();
            for (int i = 0; i < definitions.Length; i++)
            {
                ActionDefinition definition = definitions[i]
                    ?? throw new InvalidOperationException($"Actions[{i}] 为空。");
                if (definition.Id <= 0) throw new InvalidOperationException("Action Id 必须大于 0。");
                if (_definitions.ContainsKey(definition.Id))
                    throw new InvalidOperationException($"Action Id 重复：{definition.Id}。");
                if (!_executors.TryGetValue(definition.TypeId, out IExecutorAdapter executor))
                    throw new InvalidOperationException(
                        $"Action {definition.Id} 引用了未注册 Executor TypeId={definition.TypeId}。");
                if (!PayloadMatches(executor.PayloadType, definition.Payload))
                    throw new InvalidOperationException(
                        $"Action {definition.Id} Payload 类型应为 {executor.PayloadType.FullName}，" +
                        $"实际为 {definition.Payload?.GetType().FullName ?? "null"}。");
                _definitions.Add(definition.Id, definition);
            }
            Catalog = catalog;
            IsInitialized = true;
        }

        public bool ContainsAction(int actionId) => IsInitialized && _definitions.ContainsKey(actionId);

        public async UniTask<ActionExecutionResult> ExecuteAsync(
            int actionId,
            ActionContext context,
            CancellationToken cancellationToken = default)
        {
            if (!IsInitialized) throw new InvalidOperationException("ActionService 尚未初始化。");
            if (!_definitions.TryGetValue(actionId, out ActionDefinition definition))
                throw new KeyNotFoundException($"Action ID 不存在：{actionId}。");

            if (cancellationToken.IsCancellationRequested)
                return ActionExecutionResult.Cancelled();

            try
            {
                return await _executors[definition.TypeId]
                    .ExecuteAsync(definition.Payload, context, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return ActionExecutionResult.Cancelled();
            }
            catch (Exception ex)
            {
                Report(ex);
                return ActionExecutionResult.Failed(
                    $"Action {actionId} [{definition.Key}] 执行异常：{ex.Message}", ex);
            }
        }

        private static bool PayloadMatches(Type expected, object payload)
            => payload != null ? expected.IsInstanceOfType(payload) : !expected.IsValueType;

        private void Report(Exception error)
        {
            try { ObserverErrorSink?.Invoke(error); }
            catch { }
        }
    }
}
