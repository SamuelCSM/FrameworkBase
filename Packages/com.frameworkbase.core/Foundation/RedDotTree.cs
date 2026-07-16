using System;
using System.Collections.Generic;

namespace Framework.Foundation
{
    /// <summary>红点节点调试信息（<see cref="RedDotTree.Snapshot"/> 输出）。</summary>
    public readonly struct RedDotNodeInfo
    {
        internal RedDotNodeInfo(string path, int ownCount, int totalCount, bool hasChildren)
        {
            Path = path;
            OwnCount = ownCount;
            TotalCount = totalCount;
            HasChildren = hasChildren;
        }

        /// <summary>节点全路径。</summary>
        public string Path { get; }

        /// <summary>自身计数（仅叶子非零）。</summary>
        public int OwnCount { get; }

        /// <summary>聚合计数（自身 + 子树全部叶子）。</summary>
        public int TotalCount { get; }

        /// <summary>是否有子节点。</summary>
        public bool HasChildren { get; }
    }

    /// <summary>
    /// 红点/Badge 树：路径寻址（如 "Mail/System"）的计数聚合树。
    /// <para>
    /// <b>值语义</b>：计数只能写在叶子（<see cref="SetCount"/>），父节点值 = 子树叶子计数之和，
    /// 由增量聚合维护、O(深度) 更新。对非叶子写计数、或对已持有计数的叶子挂子节点，
    /// 都是结构性错误（会造成双重计数的歧义），一律抛异常在开发期炸出——与本框架
    /// 状态机拓扑校验、命令总线重名注册同款 fail-loud 约定。
    /// </para>
    /// <para>
    /// <b>通知</b>：叶子计数变化沿祖先链传播，路径上每个值发生变化的节点都会通知其订阅者
    /// （参数为该节点新的聚合计数）；值未变化不通知。订阅默认立即回调当前值，UI 绑定
    /// 无需关心「先订阅还是先写数」的时序。订阅者异常被隔离，可经 <see cref="ObserverErrorSink"/> 上报。
    /// </para>
    /// <para>
    /// 纯 C# 零 Unity 依赖，可自由实例化（业务可为独立玩法建局部树）；框架共享默认树经
    /// 组合根 <c>GameEntry.RedDots</c> 暴露。线程约定：仅主线程访问（红点是 UI 语义，
    /// 不为不存在的并发场景付锁的代价）。
    /// </para>
    /// </summary>
    public sealed class RedDotTree
    {
        /// <summary>路径分隔符。</summary>
        public const char Separator = '/';

        private sealed class Node
        {
            public string Path;
            public Node Parent;
            public Dictionary<string, Node> Children;   // 惰性创建
            public List<Subscription> Subscriptions;    // 惰性创建
            public int OwnCount;
            public int TotalCount;

            public bool HasChildren => Children != null && Children.Count > 0;
        }

        /// <summary>订阅句柄：Dispose 即退订（幂等）。</summary>
        private sealed class Subscription : IDisposable
        {
            public Action<int> Handler;
            private Node _node;

            public Subscription(Node node, Action<int> handler)
            {
                _node = node;
                Handler = handler;
            }

            public void Dispose()
            {
                Node node = _node;
                _node = null;
                Handler = null;
                node?.Subscriptions?.Remove(this);
            }
        }

        private readonly Node _root = new Node { Path = string.Empty };
        private readonly Dictionary<string, Node> _index = new Dictionary<string, Node>(StringComparer.Ordinal);

        /// <summary>订阅者异常的诊断出口；为 null 时静默隔离（与 AsyncStateMachine 同款约定）。</summary>
        public Action<Exception> ObserverErrorSink { get; set; }

        /// <summary>整树聚合计数（全部叶子之和）。</summary>
        public int TotalCount => _root.TotalCount;

        /// <summary>读取节点聚合计数；路径不存在返回 0（读不创建节点）。</summary>
        public int GetCount(string path)
        {
            ValidatePath(path);
            return _index.TryGetValue(path, out Node node) ? node.TotalCount : 0;
        }

        /// <summary>
        /// 设置叶子计数（绝对值，>= 0）。目标节点不存在时按需创建整条路径。
        /// 对已有子节点的节点写计数抛 <see cref="InvalidOperationException"/>（结构性错误）。
        /// </summary>
        public void SetCount(string path, int count)
        {
            if (count < 0)
                throw new ArgumentOutOfRangeException(nameof(count), "红点计数不能为负。");

            Node node = GetOrCreateNode(path);
            if (node.HasChildren)
            {
                throw new InvalidOperationException(
                    $"红点节点 '{path}' 已有子节点，计数只能写在叶子（父节点值由子树聚合）。");
            }

            int delta = count - node.OwnCount;
            if (delta == 0)
                return;

            node.OwnCount = count;
            ApplyDelta(node, delta);
        }

        /// <summary>增量修改叶子计数；结果为负按 0 截断（多次消费同一红点不至于把计数打穿）。</summary>
        public void AddCount(string path, int delta)
        {
            Node node = GetOrCreateNode(path);
            SetCount(path, Math.Max(0, node.OwnCount + delta));
        }

        /// <summary>
        /// 清零子树全部叶子计数（一键已读）。路径不存在为 no-op。
        /// 子树内值变化的节点与其上方祖先都会收到通知（子先于父）。
        /// </summary>
        public void ClearSubtree(string path)
        {
            ValidatePath(path);
            if (!_index.TryGetValue(path, out Node node))
                return;

            int removed = node.TotalCount;
            if (removed == 0)
                return;

            ClearRecursive(node);
            for (Node cur = node.Parent; cur != null; cur = cur.Parent)
            {
                cur.TotalCount -= removed;
                NotifyNode(cur);
            }
        }

        /// <summary>
        /// 订阅节点聚合计数变化。节点不存在时按需创建（UI 可先绑定、业务后写数）。
        /// </summary>
        /// <param name="path">节点路径。</param>
        /// <param name="handler">回调，参数为该节点新的聚合计数。</param>
        /// <param name="notifyImmediately">订阅成功后是否立即以当前值回调一次（默认 true）。</param>
        /// <returns>退订句柄，Dispose 幂等。</returns>
        public IDisposable Subscribe(string path, Action<int> handler, bool notifyImmediately = true)
        {
            if (handler == null) throw new ArgumentNullException(nameof(handler));

            Node node = GetOrCreateNode(path);
            node.Subscriptions = node.Subscriptions ?? new List<Subscription>();
            var subscription = new Subscription(node, handler);
            node.Subscriptions.Add(subscription);

            if (notifyImmediately)
                InvokeIsolated(handler, node.TotalCount);
            return subscription;
        }

        /// <summary>
        /// 全部节点的调试快照（DFS 先序、兄弟按名排序，输出稳定），供调试命令 / 排查用。
        /// </summary>
        public IReadOnlyList<RedDotNodeInfo> Snapshot()
        {
            var result = new List<RedDotNodeInfo>(_index.Count);
            AppendSnapshot(_root, result);
            return result;
        }

        // ── 内部实现 ─────────────────────────────────────────────────────────

        /// <summary>叶子计数变化后：自身与祖先链增量更新聚合值并逐节点通知（root 不通知）。</summary>
        private void ApplyDelta(Node node, int delta)
        {
            for (Node cur = node; cur != null; cur = cur.Parent)
            {
                cur.TotalCount += delta;
                NotifyNode(cur);
            }
        }

        /// <summary>递归清零子树（子先于父通知，保证父节点通知时子孙已一致）。</summary>
        private void ClearRecursive(Node node)
        {
            if (node.Children != null)
            {
                foreach (KeyValuePair<string, Node> pair in node.Children)
                    ClearRecursive(pair.Value);
            }

            if (node.TotalCount == 0)
                return;
            node.OwnCount = 0;
            node.TotalCount = 0;
            NotifyNode(node);
        }

        private void NotifyNode(Node node)
        {
            // root 是内部聚合点，无路径不可订阅
            if (ReferenceEquals(node, _root) || node.Subscriptions == null)
                return;

            // 快照遍历：回调内订阅/退订会改列表，直接遍历会跳项或越界。
            // 通知只在计数变化时发生（低频），快照分配可接受；被退订者 Handler 已置空自然跳过。
            Subscription[] snapshot = node.Subscriptions.ToArray();
            for (int i = 0; i < snapshot.Length; i++)
            {
                Action<int> handler = snapshot[i].Handler;
                if (handler != null)
                    InvokeIsolated(handler, node.TotalCount);
            }
        }

        private void InvokeIsolated(Action<int> handler, int count)
        {
            try
            {
                handler(count);
            }
            catch (Exception ex)
            {
                try { ObserverErrorSink?.Invoke(ex); }
                catch { /* 诊断出口自身的异常没有更下游的去处，只能吞。 */ }
            }
        }

        private Node GetOrCreateNode(string path)
        {
            ValidatePath(path);
            if (_index.TryGetValue(path, out Node existing))
                return existing;

            Node cur = _root;
            int segmentStart = 0;
            for (int i = 0; i <= path.Length; i++)
            {
                if (i != path.Length && path[i] != Separator)
                    continue;

                string segment = path.Substring(segmentStart, i - segmentStart);
                segmentStart = i + 1;

                cur.Children = cur.Children ?? new Dictionary<string, Node>(StringComparer.Ordinal);
                if (!cur.Children.TryGetValue(segment, out Node child))
                {
                    // 持有计数的叶子不能再挂子节点：它的计数会与新子树聚合产生双重计数歧义
                    if (cur.OwnCount > 0)
                    {
                        throw new InvalidOperationException(
                            $"红点节点 '{cur.Path}' 已作为叶子持有计数 {cur.OwnCount}，不能在其下创建 '{path}'；" +
                            "请把该计数改挂到它的子叶子上。");
                    }

                    child = new Node
                    {
                        Path = cur == _root ? segment : cur.Path + Separator + segment,
                        Parent = cur,
                    };
                    cur.Children.Add(segment, child);
                    _index.Add(child.Path, child);
                }
                cur = child;
            }
            return cur;
        }

        private static void ValidatePath(string path)
        {
            if (string.IsNullOrEmpty(path))
                throw new ArgumentException("红点路径不能为空。", nameof(path));
            if (path[0] == Separator || path[path.Length - 1] == Separator)
                throw new ArgumentException($"红点路径 '{path}' 不能以分隔符开头或结尾。", nameof(path));

            bool segmentHasContent = false;
            for (int i = 0; i < path.Length; i++)
            {
                char c = path[i];
                if (c == Separator)
                {
                    if (!segmentHasContent)
                        throw new ArgumentException($"红点路径 '{path}' 含空段。", nameof(path));
                    segmentHasContent = false;
                    continue;
                }
                if (char.IsWhiteSpace(c))
                    throw new ArgumentException($"红点路径 '{path}' 不能含空白字符。", nameof(path));
                segmentHasContent = true;
            }
        }

        private void AppendSnapshot(Node node, List<RedDotNodeInfo> result)
        {
            if (node.Children == null)
                return;

            var keys = new List<string>(node.Children.Keys);
            keys.Sort(StringComparer.Ordinal);
            foreach (string key in keys)
            {
                Node child = node.Children[key];
                result.Add(new RedDotNodeInfo(child.Path, child.OwnCount, child.TotalCount, child.HasChildren));
                AppendSnapshot(child, result);
            }
        }
    }
}
