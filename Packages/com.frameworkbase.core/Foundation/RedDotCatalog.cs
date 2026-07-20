using System;
using System.Collections.Generic;

namespace Framework.Foundation
{
    /// <summary>红点节点类型：业务只可写 Signal，Aggregate 完全由依赖节点聚合。</summary>
    public enum RedDotNodeKind
    {
        Signal = 0,
        Aggregate = 1,
    }

    /// <summary>聚合节点如何把直接/间接依赖转换为自身计数。</summary>
    public enum RedDotAggregation
    {
        None = 0,
        Any = 1,
        SumChildren = 2,
        MaxChildren = 3,
        SumUniqueSignals = 4,
    }

    /// <summary>弱提示被确认的业务时机；Badge 自身只展示，不自动触发。</summary>
    public enum RedDotAcknowledgeTrigger
    {
        None = 0,
        Enter = 1,
        Expose = 2,
        Click = 3,
        Manual = 4,
    }

    /// <summary>弱提示“已看版本”的保存位置。</summary>
    public enum RedDotSeenSaveMode
    {
        Session = 0,
        LocalAccount = 1,
        ServerAccount = 2,
    }

    /// <summary>
    /// 业务模块定义。模块是配置归属/维护责任，不代表红点层级或唯一主入口。
    /// </summary>
    [Serializable]
    public sealed class RedDotModuleDefinition
    {
        /// <summary>稳定模块 ID；节点通过 ModuleId 引用，发布后不可复用或改号。</summary>
        public int Id;

        /// <summary>模块唯一键；用于生成业务侧 RedDotModuleId 枚举成员与分组名称。</summary>
        public string Key;

        /// <summary>模块职责说明，仅供配置维护、搜索和诊断显示。</summary>
        public string Description;

        /// <summary>该模块可分配的红点节点 ID 下界（包含）。</summary>
        public int IdMin;

        /// <summary>该模块可分配的红点节点 ID 上界（包含）。</summary>
        public int IdMax;
    }

    /// <summary>单个有效红点节点定义。</summary>
    [Serializable]
    public sealed class RedDotNodeDefinition
    {
        /// <summary>稳定红点 ID；UI、业务代码、边关系和已看记录均以它为主键。</summary>
        public int Id;

        /// <summary>全局唯一可读键；只用于搜索、日志和生成常量，不表达父子关系。</summary>
        public string Key;

        /// <summary>配置归属模块 ID；用于号段和维护责任校验，不代表唯一 UI 入口。</summary>
        public int ModuleId;

        /// <summary>节点职责：Signal 由业务写入，Aggregate 只能由子节点计算。</summary>
        public RedDotNodeKind Kind;

        /// <summary>Aggregate 的聚合算法；Signal 必须配置为 None。</summary>
        public RedDotAggregation Aggregation;

        /// <summary>策划可读的业务含义说明，不参与运行时计算。</summary>
        public string Description;
    }

    /// <summary>一条 DAG 依赖边：ParentId 的结果依赖 ChildId。</summary>
    [Serializable]
    public sealed class RedDotEdgeDefinition
    {
        /// <summary>依赖方 Aggregate ID；其最终值会使用 ChildId 对应节点参与计算。</summary>
        public int ParentId;

        /// <summary>被依赖节点 ID；同一子节点可以被多个 ParentId 复用。</summary>
        public int ChildId;

        /// <summary>该依赖关系的业务说明，仅用于维护和诊断。</summary>
        public string Description;
    }

    /// <summary>
    /// 可选的弱提示策略。没有策略的 Signal 是业务状态驱动，不能通过 Acknowledge 清除。
    /// </summary>
    [Serializable]
    public sealed class RedDotSeenPolicyDefinition
    {
        /// <summary>应用弱提示已看语义的 Signal ID；Aggregate 不允许配置。</summary>
        public int SignalId;

        /// <summary>业务在何种交互发生时可调用 Acknowledge 确认已看。</summary>
        public RedDotAcknowledgeTrigger Trigger;

        /// <summary>已看版本的生命周期与持久化范围。</summary>
        public RedDotSeenSaveMode SaveMode;

        /// <summary>内容版本；递增后旧的已看记录失效，弱提示可重新出现。</summary>
        public int Version;
    }

    /// <summary>已退出使用的 ID 台账；不进入运行时图，仅用于校验禁止 ID 复用。</summary>
    [Serializable]
    public sealed class RedDotRetiredIdDefinition
    {
        /// <summary>已退出使用且永久禁止复用的红点 ID。</summary>
        public int Id;

        /// <summary>退休前的节点 Key，便于历史引用排查。</summary>
        public string FormerKey;

        /// <summary>执行退休的客户端或内容版本。</summary>
        public string RetiredVersion;

        /// <summary>退休原因及迁移说明。</summary>
        public string Reason;
    }

    /// <summary>由 ConfigData 五张表组装的运行时红点目录；数组保证校验和遍历顺序确定。</summary>
    [Serializable]
    public sealed class RedDotCatalog
    {
        /// <summary>目录结构版本；用于未来不兼容格式升级。</summary>
        public int SchemaVersion = 1;

        /// <summary>模块及其稳定 ID 号段定义。</summary>
        public RedDotModuleDefinition[] Modules = Array.Empty<RedDotModuleDefinition>();

        /// <summary>全部当前有效的 Signal 与 Aggregate 节点。</summary>
        public RedDotNodeDefinition[] Nodes = Array.Empty<RedDotNodeDefinition>();

        /// <summary>DAG 依赖边；ParentId 依赖 ChildId，允许一个 Child 拥有多个 Parent。</summary>
        public RedDotEdgeDefinition[] Edges = Array.Empty<RedDotEdgeDefinition>();

        /// <summary>需要“看过后消失”语义的 Signal 策略。</summary>
        public RedDotSeenPolicyDefinition[] SeenPolicies = Array.Empty<RedDotSeenPolicyDefinition>();

        /// <summary>永久禁止复用的历史 ID 台账；不参与运行时图计算。</summary>
        public RedDotRetiredIdDefinition[] RetiredIds = Array.Empty<RedDotRetiredIdDefinition>();
    }

    /// <summary>配置错误集合；编辑器与运行时初始化共享同一套拓扑规则。</summary>
    public sealed class RedDotCatalogValidationResult
    {
        private readonly List<string> _errors = new List<string>();
        private readonly List<string> _warnings = new List<string>();

        /// <summary>阻止目录初始化或构建的结构错误。</summary>
        public IReadOnlyList<string> Errors => _errors;
        /// <summary>允许继续使用、但通常需要配置维护者确认的问题。</summary>
        public IReadOnlyList<string> Warnings => _warnings;
        /// <summary>是否不存在任何阻断错误。</summary>
        public bool IsValid => _errors.Count == 0;

        internal void Error(string message) => _errors.Add(message);
        internal void Warning(string message) => _warnings.Add(message);

        public override string ToString()
        {
            var lines = new List<string>(_errors.Count + _warnings.Count);
            for (int i = 0; i < _errors.Count; i++) lines.Add("ERROR: " + _errors[i]);
            for (int i = 0; i < _warnings.Count; i++) lines.Add("WARN: " + _warnings[i]);
            return string.Join(Environment.NewLine, lines);
        }
    }

    /// <summary>红点配置与 DAG 完整性校验器。</summary>
    public static class RedDotCatalogValidator
    {
        public static RedDotCatalogValidationResult Validate(RedDotCatalog catalog)
        {
            var result = new RedDotCatalogValidationResult();
            if (catalog == null)
            {
                result.Error("红点目录不能为空。");
                return result;
            }

            if (catalog.SchemaVersion <= 0)
                result.Error("SchemaVersion 必须大于 0。");

            RedDotModuleDefinition[] modules = catalog.Modules ?? Array.Empty<RedDotModuleDefinition>();
            RedDotNodeDefinition[] nodes = catalog.Nodes ?? Array.Empty<RedDotNodeDefinition>();
            RedDotEdgeDefinition[] edges = catalog.Edges ?? Array.Empty<RedDotEdgeDefinition>();
            RedDotSeenPolicyDefinition[] policies = catalog.SeenPolicies ?? Array.Empty<RedDotSeenPolicyDefinition>();
            RedDotRetiredIdDefinition[] retired = catalog.RetiredIds ?? Array.Empty<RedDotRetiredIdDefinition>();

            var modulesById = new Dictionary<int, RedDotModuleDefinition>();
            var moduleKeys = new HashSet<string>(StringComparer.Ordinal);
            for (int i = 0; i < modules.Length; i++)
            {
                RedDotModuleDefinition module = modules[i];
                if (module == null)
                {
                    result.Error($"Modules[{i}] 为空。");
                    continue;
                }
                if (module.Id <= 0) result.Error($"模块 '{module.Key}' 的 Id 必须大于 0。");
                else if (!modulesById.TryAdd(module.Id, module)) result.Error($"模块 Id 重复：{module.Id}。");
                if (string.IsNullOrWhiteSpace(module.Key)) result.Error($"模块 {module.Id} 的 Key 不能为空。");
                else if (!moduleKeys.Add(module.Key.Trim())) result.Error($"模块 Key 重复：{module.Key}。");
                if (module.IdMin <= 0 || module.IdMax < module.IdMin)
                    result.Error($"模块 {module.Id} [{module.Key}] 的 ID 号段非法：{module.IdMin}~{module.IdMax}。");
            }

            for (int i = 0; i < modules.Length; i++)
            {
                RedDotModuleDefinition left = modules[i];
                if (left == null || left.IdMin <= 0 || left.IdMax < left.IdMin) continue;
                for (int j = i + 1; j < modules.Length; j++)
                {
                    RedDotModuleDefinition right = modules[j];
                    if (right == null || right.IdMin <= 0 || right.IdMax < right.IdMin) continue;
                    if (left.IdMin <= right.IdMax && right.IdMin <= left.IdMax)
                        result.Error($"模块号段重叠：{left.Key} {left.IdMin}~{left.IdMax} 与 {right.Key} {right.IdMin}~{right.IdMax}。");
                }
            }

            var nodesById = new Dictionary<int, RedDotNodeDefinition>();
            var nodeKeys = new HashSet<string>(StringComparer.Ordinal);
            for (int i = 0; i < nodes.Length; i++)
            {
                RedDotNodeDefinition node = nodes[i];
                if (node == null)
                {
                    result.Error($"Nodes[{i}] 为空。");
                    continue;
                }
                if (node.Id <= 0) result.Error($"节点 '{node.Key}' 的 Id 必须大于 0。");
                else if (!nodesById.TryAdd(node.Id, node)) result.Error($"节点 Id 重复：{node.Id}。");
                if (string.IsNullOrWhiteSpace(node.Key)) result.Error($"节点 {node.Id} 的 Key 不能为空。");
                else
                {
                    string key = node.Key.Trim();
                    if (!nodeKeys.Add(key)) result.Error($"节点 Key 重复：{key}。");
                    for (int c = 0; c < key.Length; c++)
                        if (char.IsWhiteSpace(key[c]))
                        {
                            result.Error($"节点 Key 不能含空白字符：{key}。");
                            break;
                        }
                }

                if (!modulesById.TryGetValue(node.ModuleId, out RedDotModuleDefinition module))
                {
                    result.Error($"节点 {node.Id} [{node.Key}] 引用了不存在的 ModuleId={node.ModuleId}。");
                }
                else if (node.Id < module.IdMin || node.Id > module.IdMax)
                {
                    result.Error($"节点 {node.Id} [{node.Key}] 不在模块 {module.Key} 号段 {module.IdMin}~{module.IdMax} 内。");
                }

                if (node.Kind == RedDotNodeKind.Signal && node.Aggregation != RedDotAggregation.None)
                    result.Error($"Signal {node.Id} [{node.Key}] 的 Aggregation 必须为 None。");
                if (node.Kind == RedDotNodeKind.Aggregate && node.Aggregation == RedDotAggregation.None)
                    result.Error($"Aggregate {node.Id} [{node.Key}] 必须配置聚合方式。");
                if (!Enum.IsDefined(typeof(RedDotNodeKind), node.Kind))
                    result.Error($"节点 {node.Id} [{node.Key}] 的 Kind 非法：{node.Kind}。");
                if (!Enum.IsDefined(typeof(RedDotAggregation), node.Aggregation))
                    result.Error($"节点 {node.Id} [{node.Key}] 的 Aggregation 非法：{node.Aggregation}。");
            }

            var retiredIds = new HashSet<int>();
            for (int i = 0; i < retired.Length; i++)
            {
                RedDotRetiredIdDefinition item = retired[i];
                if (item == null) continue;
                if (item.Id <= 0) result.Error($"RetiredIds[{i}] 的 Id 必须大于 0。");
                else if (!retiredIds.Add(item.Id)) result.Error($"退休 ID 重复：{item.Id}。");
                if (nodesById.ContainsKey(item.Id)) result.Error($"退休 ID {item.Id} 仍被有效节点使用。");
            }

            var childrenByParent = new Dictionary<int, List<int>>();
            var parentsByChild = new Dictionary<int, List<int>>();
            var edgeSet = new HashSet<long>();
            for (int i = 0; i < edges.Length; i++)
            {
                RedDotEdgeDefinition edge = edges[i];
                if (edge == null)
                {
                    result.Error($"Edges[{i}] 为空。");
                    continue;
                }
                if (edge.ParentId == edge.ChildId)
                    result.Error($"节点 {edge.ParentId} 不能依赖自身。");
                if (!nodesById.TryGetValue(edge.ParentId, out RedDotNodeDefinition parent))
                    result.Error($"Edge[{i}] ParentId={edge.ParentId} 不存在。");
                else if (parent.Kind != RedDotNodeKind.Aggregate)
                    result.Error($"Signal {parent.Id} [{parent.Key}] 不能作为 ParentId。");
                if (!nodesById.ContainsKey(edge.ChildId))
                    result.Error($"Edge[{i}] ChildId={edge.ChildId} 不存在。");

                long edgeKey = ((long)edge.ParentId << 32) | (uint)edge.ChildId;
                if (!edgeSet.Add(edgeKey)) result.Error($"依赖边重复：{edge.ParentId} <- {edge.ChildId}。");
                Add(childrenByParent, edge.ParentId, edge.ChildId);
                Add(parentsByChild, edge.ChildId, edge.ParentId);
            }

            foreach (KeyValuePair<int, RedDotNodeDefinition> pair in nodesById)
            {
                RedDotNodeDefinition node = pair.Value;
                bool hasChildren = childrenByParent.TryGetValue(node.Id, out List<int> childList) && childList.Count > 0;
                if (node.Kind == RedDotNodeKind.Aggregate && !hasChildren)
                    result.Error($"Aggregate {node.Id} [{node.Key}] 没有任何子依赖。");
                if (node.Kind == RedDotNodeKind.Signal && hasChildren)
                    result.Error($"Signal {node.Id} [{node.Key}] 不能拥有子依赖。");
                if (!hasChildren && !parentsByChild.ContainsKey(node.Id) && node.Kind == RedDotNodeKind.Signal)
                    result.Warning($"Signal {node.Id} [{node.Key}] 是孤立节点；若 UI 直接订阅它可忽略此警告。");
            }

            ValidateAcyclic(nodesById, childrenByParent, parentsByChild, result);

            var policySignals = new HashSet<int>();
            for (int i = 0; i < policies.Length; i++)
            {
                RedDotSeenPolicyDefinition policy = policies[i];
                if (policy == null)
                {
                    result.Error($"SeenPolicies[{i}] 为空。");
                    continue;
                }
                if (!policySignals.Add(policy.SignalId)) result.Error($"Signal {policy.SignalId} 重复配置 SeenPolicy。");
                if (!nodesById.TryGetValue(policy.SignalId, out RedDotNodeDefinition signal))
                    result.Error($"SeenPolicy[{i}] SignalId={policy.SignalId} 不存在。");
                else if (signal.Kind != RedDotNodeKind.Signal)
                    result.Error($"SeenPolicy 只能绑定 Signal：{signal.Id} [{signal.Key}]。");
                if (policy.Trigger == RedDotAcknowledgeTrigger.None ||
                    !Enum.IsDefined(typeof(RedDotAcknowledgeTrigger), policy.Trigger))
                    result.Error($"Signal {policy.SignalId} 的 SeenPolicy Trigger 非法：{policy.Trigger}。");
                if (!Enum.IsDefined(typeof(RedDotSeenSaveMode), policy.SaveMode))
                    result.Error($"Signal {policy.SignalId} 的 SeenPolicy SaveMode 非法：{policy.SaveMode}。");
                if (policy.Version <= 0) result.Error($"Signal {policy.SignalId} 的 SeenPolicy Version 必须大于 0。");
            }

            return result;
        }

        private static void ValidateAcyclic(
            Dictionary<int, RedDotNodeDefinition> nodes,
            Dictionary<int, List<int>> childrenByParent,
            Dictionary<int, List<int>> parentsByChild,
            RedDotCatalogValidationResult result)
        {
            var remainingDependencies = new Dictionary<int, int>(nodes.Count);
            var queue = new Queue<int>();
            foreach (int id in nodes.Keys)
            {
                int dependencyCount = childrenByParent.TryGetValue(id, out List<int> children) ? children.Count : 0;
                remainingDependencies[id] = dependencyCount;
                if (dependencyCount == 0) queue.Enqueue(id);
            }

            int visited = 0;
            while (queue.Count > 0)
            {
                int childId = queue.Dequeue();
                visited++;
                if (!parentsByChild.TryGetValue(childId, out List<int> parents)) continue;
                for (int i = 0; i < parents.Count; i++)
                {
                    int parentId = parents[i];
                    int remaining = remainingDependencies[parentId] - 1;
                    remainingDependencies[parentId] = remaining;
                    if (remaining == 0) queue.Enqueue(parentId);
                }
            }

            if (visited != nodes.Count)
                result.Error("红点拓扑存在循环依赖；请检查 red_dot_edge_ref。");
        }

        private static void Add(Dictionary<int, List<int>> map, int key, int value)
        {
            if (!map.TryGetValue(key, out List<int> list))
            {
                list = new List<int>();
                map.Add(key, list);
            }
            list.Add(value);
        }
    }
}
