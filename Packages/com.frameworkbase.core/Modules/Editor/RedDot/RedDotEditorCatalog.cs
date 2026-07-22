using System.Collections.Generic;
using System.IO;
using Framework.Foundation;
using UnityEngine;

namespace Framework.Editor.RedDot
{
    /// <summary>Editor 侧按需编译源工作簿，供 Badge 回显、搜索和拓扑工具复用。</summary>
    internal static class RedDotEditorCatalog
    {
        private static RedDotCatalog _catalog;
        private static Dictionary<int, RedDotNodeDefinition> _nodes;
        private static long _lastWriteTicks;

        public static RedDotCatalog Get()
        {
            if (!File.Exists(RedDotConfigCompiler.WorkbookPath)) return null;
            long ticks = File.GetLastWriteTimeUtc(RedDotConfigCompiler.WorkbookPath).Ticks;
            if (_catalog != null && ticks == _lastWriteTicks) return _catalog;
            if (!RedDotConfigCompiler.TryCompile(out _catalog, out string error))
            {
                Debug.LogError("[RedDotEditor] RedDot.xlsx 编译失败：" + error);
                _catalog = null;
                _nodes = null;
                return null;
            }

            _lastWriteTicks = ticks;
            _nodes = new Dictionary<int, RedDotNodeDefinition>();
            foreach (RedDotNodeDefinition node in _catalog.Nodes) _nodes[node.Id] = node;
            return _catalog;
        }

        public static bool TryGetNode(int id, out RedDotNodeDefinition node)
        {
            Get();
            node = null;
            return _nodes != null && _nodes.TryGetValue(id, out node);
        }
    }
}
