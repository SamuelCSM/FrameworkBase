using System;
using System.Collections.Generic;
using Framework.Foundation;

namespace Framework.RedDot
{
    /// <summary>
    /// 红点亮起路径导航器：把 <see cref="RedDotService.GetActivePath"/> 的结果接到业务跳转处理器，
    /// 实现"点击入口红点 → 逐级跳转到点亮来源"。处理器只负责打开对应页面/切到对应页签，
    /// 路径解析交给服务。纯逻辑、无 Unity 依赖，便于单测；仅主线程使用。
    /// </summary>
    public sealed class RedDotNavigator
    {
        private readonly RedDotService _service;
        private readonly Dictionary<int, Action> _handlers = new Dictionary<int, Action>();

        public RedDotNavigator(RedDotService service)
        {
            _service = service ?? throw new ArgumentNullException(nameof(service));
        }

        /// <summary>为某节点注册跳转处理器（如打开对应页面）；同一节点重复注册覆盖。</summary>
        public void Register(int nodeId, Action onNavigate)
        {
            if (nodeId <= 0) throw new ArgumentOutOfRangeException(nameof(nodeId), "红点 ID 必须大于 0。");
            _handlers[nodeId] = onNavigate ?? throw new ArgumentNullException(nameof(onNavigate));
        }

        /// <summary>移除某节点的跳转处理器；返回是否存在并被移除。</summary>
        public bool Unregister(int nodeId) => _handlers.Remove(nodeId);

        /// <summary>是否已为某节点注册跳转处理器。</summary>
        public bool HasHandler(int nodeId) => _handlers.ContainsKey(nodeId);

        /// <summary>返回入口到最深亮起来源的节点 ID 路径；入口未点亮返回空数组。</summary>
        public IReadOnlyList<int> GetPath(int entryId)
        {
            IReadOnlyList<RedDotNodeSnapshot> path = _service.GetActivePath(entryId);
            if (path.Count == 0) return Array.Empty<int>();
            var ids = new int[path.Count];
            for (int i = 0; i < path.Count; i++) ids[i] = path[i].Id;
            return ids;
        }

        /// <summary>
        /// 沿亮起路径从入口到来源依次调用命中的跳转处理器，模拟逐级钻取到点亮功能
        /// （如先打开邮件页、再切到未读页签）。返回实际触发跳转的节点数；入口未点亮或无处理器命中返回 0。
        /// 单个处理器异常被隔离、经 <paramref name="errorSink"/> 上报，不阻断其余跳转。
        /// </summary>
        public int Navigate(int entryId, Action<Exception> errorSink = null)
        {
            IReadOnlyList<RedDotNodeSnapshot> path = _service.GetActivePath(entryId);
            int invoked = 0;
            for (int i = 0; i < path.Count; i++)
            {
                if (!_handlers.TryGetValue(path[i].Id, out Action handler)) continue;
                try
                {
                    handler();
                    invoked++;
                }
                catch (Exception ex)
                {
                    try { errorSink?.Invoke(ex); }
                    catch { /* 诊断出口自身异常没有更下游的去处。 */ }
                }
            }
            return invoked;
        }
    }
}
