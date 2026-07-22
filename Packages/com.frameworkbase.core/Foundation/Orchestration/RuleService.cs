using System;
using System.Collections.Generic;

namespace Framework.Foundation
{
    /// <summary>规则节点种类：叶子交给业务 Evaluator，组合节点由框架求值。</summary>
    public enum RuleNodeKind
    {
        Predicate = 0,
        All = 1,
        Any = 2,
        Not = 3,
    }

    /// <summary>规则求值状态。NotReady 与 Failed 分离，避免把尚未装载的数据永久判成不满足。</summary>
    public enum RuleStatus
    {
        Passed = 0,
        Failed = 1,
        NotReady = 2,
        Error = 3,
    }

    /// <summary>一次规则求值结果；普通路径不构造诊断树，保持零额外集合分配。</summary>
    public readonly struct RuleResult
    {
        private RuleResult(RuleStatus status, string reason, Exception error)
        {
            Status = status;
            Reason = reason;
            Error = error;
        }

        public RuleStatus Status { get; }
        public string Reason { get; }
        public Exception Error { get; }
        public bool IsPassed => Status == RuleStatus.Passed;

        public static RuleResult Passed() => new RuleResult(RuleStatus.Passed, null, null);
        public static RuleResult Failed(string reason = null) => new RuleResult(RuleStatus.Failed, reason, null);
        public static RuleResult NotReady(string reason = null) => new RuleResult(RuleStatus.NotReady, reason, null);
        public static RuleResult FromError(Exception error, string reason = null)
            => new RuleResult(RuleStatus.Error, reason ?? error?.Message, error);
    }

    /// <summary>规则求值上下文。领域依赖应注入 Evaluator；Owner/Scope/Data 只携带本次编排作用域。</summary>
    public readonly struct RuleContext
    {
        public RuleContext(object owner, object scope = null, object data = null)
        {
            Owner = owner;
            Scope = scope;
            Data = data;
        }

        public object Owner { get; }
        public object Scope { get; }
        public object Data { get; }
    }

    /// <summary>一条可被配置引用的规则。</summary>
    [Serializable]
    public sealed class RuleDefinition
    {
        public int Id;
        public string Key;
        public int RootNodeId;
        public string Description;
    }

    /// <summary>规则节点。Predicate 使用 TypeId + Payload；组合节点忽略这两个字段。</summary>
    [Serializable]
    public sealed class RuleNodeDefinition
    {
        public int Id;
        public int RuleId;
        public RuleNodeKind Kind;
        public int TypeId;
        public object Payload;
        public string Description;
    }

    /// <summary>规则父子关系；Order 决定短路求值的稳定顺序。</summary>
    [Serializable]
    public sealed class RuleEdgeDefinition
    {
        public int ParentNodeId;
        public int ChildNodeId;
        public int Order;
    }

    [Serializable]
    public sealed class RuleCatalog
    {
        public int SchemaVersion = 1;
        public RuleDefinition[] Rules = Array.Empty<RuleDefinition>();
        public RuleNodeDefinition[] Nodes = Array.Empty<RuleNodeDefinition>();
        public RuleEdgeDefinition[] Edges = Array.Empty<RuleEdgeDefinition>();
    }

    /// <summary>业务模块实现的强类型叶子条件；同一实现可消费任意多条配置实例。</summary>
    public interface IRuleEvaluator<in TPayload>
    {
        RuleResult Evaluate(TPayload payload, RuleContext context);
    }

    /// <summary>
    /// 通用规则服务。配置只描述组合关系与强类型 Payload；框架负责 All/Any/Not，业务只注册叶子 Evaluator。
    /// Catalog 初始化后不可替换；仅主线程访问。
    /// </summary>
    public sealed class RuleService
    {
        private interface IEvaluatorAdapter
        {
            Type PayloadType { get; }
            RuleResult Evaluate(object payload, RuleContext context);
        }

        private sealed class EvaluatorAdapter<TPayload> : IEvaluatorAdapter
        {
            private readonly IRuleEvaluator<TPayload> _evaluator;

            public EvaluatorAdapter(IRuleEvaluator<TPayload> evaluator)
            {
                _evaluator = evaluator ?? throw new ArgumentNullException(nameof(evaluator));
            }

            public Type PayloadType => typeof(TPayload);

            public RuleResult Evaluate(object payload, RuleContext context)
                => _evaluator.Evaluate((TPayload)payload, context);
        }

        private sealed class Node
        {
            public RuleNodeDefinition Definition;
            public readonly List<OrderedChild> Children = new List<OrderedChild>();
        }

        private readonly struct OrderedChild
        {
            public OrderedChild(Node node, int order)
            {
                Node = node;
                Order = order;
            }

            public Node Node { get; }
            public int Order { get; }
        }

        private sealed class Rule
        {
            public RuleDefinition Definition;
            public Node Root;
        }

        private readonly Dictionary<int, IEvaluatorAdapter> _evaluators =
            new Dictionary<int, IEvaluatorAdapter>();
        private readonly Dictionary<int, Rule> _rules = new Dictionary<int, Rule>();
        private readonly Dictionary<int, Node> _nodes = new Dictionary<int, Node>();

        public bool IsInitialized { get; private set; }
        public RuleCatalog Catalog { get; private set; }
        public Action<Exception> ObserverErrorSink { get; set; }

        public void Register<TPayload>(int typeId, IRuleEvaluator<TPayload> evaluator)
        {
            if (typeId <= 0) throw new ArgumentOutOfRangeException(nameof(typeId));
            if (IsInitialized)
                throw new InvalidOperationException("RuleService 已初始化，不能再注册 Evaluator。");
            if (_evaluators.ContainsKey(typeId))
                throw new InvalidOperationException($"Rule Evaluator TypeId 重复：{typeId}。");
            _evaluators.Add(typeId, new EvaluatorAdapter<TPayload>(evaluator));
        }

        public void Initialize(RuleCatalog catalog)
        {
            if (IsInitialized) throw new InvalidOperationException("RuleService 已初始化，不能替换 Catalog。");
            if (catalog == null) throw new ArgumentNullException(nameof(catalog));

            BuildNodes(catalog);
            BuildEdges(catalog);
            ValidateAndBuildRules(catalog);

            Catalog = catalog;
            IsInitialized = true;
        }

        public bool ContainsRule(int ruleId) => IsInitialized && _rules.ContainsKey(ruleId);

        public RuleResult Evaluate(int ruleId, RuleContext context = default)
        {
            EnsureInitialized();
            if (!_rules.TryGetValue(ruleId, out Rule rule))
                throw new KeyNotFoundException($"规则 ID 不存在：{ruleId}。");
            return EvaluateNode(rule.Root, context);
        }

        private void BuildNodes(RuleCatalog catalog)
        {
            RuleNodeDefinition[] definitions = catalog.Nodes ?? Array.Empty<RuleNodeDefinition>();
            for (int i = 0; i < definitions.Length; i++)
            {
                RuleNodeDefinition definition = definitions[i]
                    ?? throw new InvalidOperationException($"Rule Nodes[{i}] 为空。");
                if (definition.Id <= 0)
                    throw new InvalidOperationException($"Rule Node Id 必须大于 0：索引 {i}。");
                if (!Enum.IsDefined(typeof(RuleNodeKind), definition.Kind))
                    throw new InvalidOperationException($"Rule Node {definition.Id} Kind 非法：{definition.Kind}。");
                if (_nodes.ContainsKey(definition.Id))
                    throw new InvalidOperationException($"Rule Node Id 重复：{definition.Id}。");

                if (definition.Kind == RuleNodeKind.Predicate)
                {
                    if (!_evaluators.TryGetValue(definition.TypeId, out IEvaluatorAdapter evaluator))
                        throw new InvalidOperationException(
                            $"Rule Node {definition.Id} 引用了未注册 Evaluator TypeId={definition.TypeId}。");
                    if (!PayloadMatches(evaluator.PayloadType, definition.Payload))
                        throw new InvalidOperationException(
                            $"Rule Node {definition.Id} Payload 类型应为 {evaluator.PayloadType.FullName}，" +
                            $"实际为 {definition.Payload?.GetType().FullName ?? "null"}。");
                }
                else if (definition.TypeId != 0 || definition.Payload != null)
                {
                    throw new InvalidOperationException($"组合 Rule Node {definition.Id} 不得配置 TypeId/Payload。");
                }

                _nodes.Add(definition.Id, new Node { Definition = definition });
            }
        }

        private void BuildEdges(RuleCatalog catalog)
        {
            RuleEdgeDefinition[] edges = catalog.Edges ?? Array.Empty<RuleEdgeDefinition>();
            var duplicates = new HashSet<long>();
            for (int i = 0; i < edges.Length; i++)
            {
                RuleEdgeDefinition edge = edges[i]
                    ?? throw new InvalidOperationException($"Rule Edges[{i}] 为空。");
                if (!_nodes.TryGetValue(edge.ParentNodeId, out Node parent))
                    throw new InvalidOperationException($"Rule Edge ParentNodeId 不存在：{edge.ParentNodeId}。");
                if (!_nodes.TryGetValue(edge.ChildNodeId, out Node child))
                    throw new InvalidOperationException($"Rule Edge ChildNodeId 不存在：{edge.ChildNodeId}。");
                if (parent.Definition.RuleId != child.Definition.RuleId)
                    throw new InvalidOperationException(
                        $"Rule Edge 不得跨规则：{edge.ParentNodeId} -> {edge.ChildNodeId}。");
                if (parent.Definition.Kind == RuleNodeKind.Predicate)
                    throw new InvalidOperationException($"Predicate Node {edge.ParentNodeId} 不能拥有子节点。");
                if (edge.ParentNodeId == edge.ChildNodeId)
                    throw new InvalidOperationException($"Rule Node {edge.ParentNodeId} 不能依赖自身。");

                long key = ((long)edge.ParentNodeId << 32) | (uint)edge.ChildNodeId;
                if (!duplicates.Add(key))
                    throw new InvalidOperationException(
                        $"Rule Edge 重复：{edge.ParentNodeId} -> {edge.ChildNodeId}。");
                parent.Children.Add(new OrderedChild(child, edge.Order));
            }

            foreach (Node node in _nodes.Values)
                node.Children.Sort(CompareOrderedChildren);
        }

        private void ValidateAndBuildRules(RuleCatalog catalog)
        {
            RuleDefinition[] definitions = catalog.Rules ?? Array.Empty<RuleDefinition>();
            for (int i = 0; i < definitions.Length; i++)
            {
                RuleDefinition definition = definitions[i]
                    ?? throw new InvalidOperationException($"Rules[{i}] 为空。");
                if (definition.Id <= 0) throw new InvalidOperationException("Rule Id 必须大于 0。");
                if (_rules.ContainsKey(definition.Id))
                    throw new InvalidOperationException($"Rule Id 重复：{definition.Id}。");
                if (!_nodes.TryGetValue(definition.RootNodeId, out Node root))
                    throw new InvalidOperationException(
                        $"Rule {definition.Id} RootNodeId 不存在：{definition.RootNodeId}。");
                if (root.Definition.RuleId != definition.Id)
                    throw new InvalidOperationException(
                        $"Rule {definition.Id} 的根节点 {definition.RootNodeId} 归属 RuleId={root.Definition.RuleId}。");

                ValidateNodeShape(root);
                ValidateAcyclic(root);
                _rules.Add(definition.Id, new Rule { Definition = definition, Root = root });
            }

            foreach (Node node in _nodes.Values)
                if (!_rules.ContainsKey(node.Definition.RuleId))
                    throw new InvalidOperationException(
                        $"Rule Node {node.Definition.Id} 引用了不存在的 RuleId={node.Definition.RuleId}。");
        }

        private static void ValidateNodeShape(Node node)
        {
            switch (node.Definition.Kind)
            {
                case RuleNodeKind.Predicate:
                    if (node.Children.Count != 0)
                        throw new InvalidOperationException($"Predicate Node {node.Definition.Id} 不能有子节点。");
                    break;
                case RuleNodeKind.All:
                case RuleNodeKind.Any:
                    if (node.Children.Count == 0)
                        throw new InvalidOperationException($"组合 Rule Node {node.Definition.Id} 至少需要一个子节点。");
                    break;
                case RuleNodeKind.Not:
                    if (node.Children.Count != 1)
                        throw new InvalidOperationException($"Not Node {node.Definition.Id} 必须且只能有一个子节点。");
                    break;
            }
        }

        private static void ValidateAcyclic(Node root)
        {
            var visiting = new HashSet<int>();
            var visited = new HashSet<int>();
            Visit(root, visiting, visited, 0);
        }

        private static void Visit(Node node, HashSet<int> visiting, HashSet<int> visited, int depth)
        {
            if (depth > 128)
                throw new InvalidOperationException($"Rule {node.Definition.RuleId} 深度超过 128，拒绝初始化。");
            int id = node.Definition.Id;
            if (visited.Contains(id)) return;
            if (!visiting.Add(id))
                throw new InvalidOperationException($"Rule {node.Definition.RuleId} 存在环，涉及 Node {id}。");
            ValidateNodeShape(node);
            for (int i = 0; i < node.Children.Count; i++)
                Visit(node.Children[i].Node, visiting, visited, depth + 1);
            visiting.Remove(id);
            visited.Add(id);
        }

        private RuleResult EvaluateNode(Node node, RuleContext context)
        {
            switch (node.Definition.Kind)
            {
                case RuleNodeKind.Predicate:
                    try
                    {
                        return _evaluators[node.Definition.TypeId].Evaluate(node.Definition.Payload, context);
                    }
                    catch (Exception ex)
                    {
                        Report(ex);
                        return RuleResult.FromError(ex, $"Rule Node {node.Definition.Id} Evaluator 异常：{ex.Message}");
                    }
                case RuleNodeKind.Not:
                    return Negate(EvaluateNode(node.Children[0].Node, context));
                case RuleNodeKind.All:
                    return EvaluateAll(node, context);
                case RuleNodeKind.Any:
                    return EvaluateAny(node, context);
                default:
                    return RuleResult.FromError(
                        new InvalidOperationException($"未知 RuleNodeKind：{node.Definition.Kind}。"));
            }
        }

        private RuleResult EvaluateAll(Node node, RuleContext context)
        {
            RuleResult deferred = RuleResult.Passed();
            for (int i = 0; i < node.Children.Count; i++)
            {
                RuleResult current = EvaluateNode(node.Children[i].Node, context);
                if (current.Status == RuleStatus.Failed || current.Status == RuleStatus.Error)
                    return current;
                if (current.Status == RuleStatus.NotReady)
                    deferred = current;
            }
            return deferred;
        }

        private RuleResult EvaluateAny(Node node, RuleContext context)
        {
            RuleResult deferred = RuleResult.Failed();
            for (int i = 0; i < node.Children.Count; i++)
            {
                RuleResult current = EvaluateNode(node.Children[i].Node, context);
                if (current.Status == RuleStatus.Passed) return current;
                if (current.Status == RuleStatus.Error) return current;
                if (current.Status == RuleStatus.NotReady)
                    deferred = current;
            }
            return deferred;
        }

        private static RuleResult Negate(RuleResult value)
        {
            switch (value.Status)
            {
                case RuleStatus.Passed: return RuleResult.Failed(value.Reason);
                case RuleStatus.Failed: return RuleResult.Passed();
                default: return value;
            }
        }

        private static int CompareOrderedChildren(OrderedChild left, OrderedChild right)
        {
            int byOrder = left.Order.CompareTo(right.Order);
            return byOrder != 0
                ? byOrder
                : left.Node.Definition.Id.CompareTo(right.Node.Definition.Id);
        }

        private static bool PayloadMatches(Type expected, object payload)
            => payload != null ? expected.IsInstanceOfType(payload) : !expected.IsValueType;

        private void EnsureInitialized()
        {
            if (!IsInitialized) throw new InvalidOperationException("RuleService 尚未初始化。");
        }

        private void Report(Exception error)
        {
            try { ObserverErrorSink?.Invoke(error); }
            catch { }
        }
    }
}
