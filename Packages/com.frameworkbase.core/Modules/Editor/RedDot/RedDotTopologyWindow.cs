using System.Linq;
using Framework.Foundation;
using UnityEditor;
using UnityEngine;

namespace Framework.Editor.RedDot
{
    /// <summary>轻量只读拓扑查看器：配置仍以 Excel 为唯一事实源。</summary>
    public sealed class RedDotTopologyWindow : EditorWindow
    {
        private int _selectedId;
        private Vector2 _scroll;

        [MenuItem("Tools/Framework/Red Dot/Topology")]
        private static void Open()
        {
            var window = GetWindow<RedDotTopologyWindow>("Red Dot Topology");
            window.minSize = new Vector2(620f, 420f);
        }

        private void OnGUI()
        {
            RedDotCatalog catalog = RedDotEditorCatalog.Get();
            if (catalog == null)
            {
                EditorGUILayout.HelpBox("请先导入 RedDot.xlsx。", MessageType.Error);
                return;
            }

            EditorGUILayout.BeginHorizontal();
            _selectedId = EditorGUILayout.IntField("Node ID", _selectedId);
            if (GUILayout.Button("搜索", GUILayout.Width(58f)))
                RedDotSearchWindow.Open(id => { _selectedId = id; Repaint(); });
            EditorGUILayout.EndHorizontal();

            RedDotNodeDefinition node = catalog.Nodes.FirstOrDefault(value => value.Id == _selectedId);
            if (node == null)
            {
                EditorGUILayout.HelpBox("输入或搜索一个节点 ID。", MessageType.Info);
                return;
            }

            EditorGUILayout.LabelField($"{node.Id}  {node.Key}", EditorStyles.boldLabel);
            EditorGUILayout.LabelField($"Module={node.ModuleId}  Type={node.Kind}  Aggregation={node.Aggregation}");
            EditorGUILayout.LabelField(node.Description ?? string.Empty, EditorStyles.wordWrappedLabel);
            _scroll = EditorGUILayout.BeginScrollView(_scroll);
            DrawRelations("父节点（受当前节点影响）", catalog.Edges.Where(edge => edge.ChildId == node.Id)
                .Select(edge => edge.ParentId), catalog);
            DrawRelations("子节点（当前节点依赖）", catalog.Edges.Where(edge => edge.ParentId == node.Id)
                .Select(edge => edge.ChildId), catalog);
            EditorGUILayout.EndScrollView();
        }

        private void DrawRelations(string title, System.Collections.Generic.IEnumerable<int> ids, RedDotCatalog catalog)
        {
            EditorGUILayout.Space();
            EditorGUILayout.LabelField(title, EditorStyles.boldLabel);
            int count = 0;
            foreach (int id in ids.OrderBy(value => value))
            {
                RedDotNodeDefinition relation = catalog.Nodes.FirstOrDefault(value => value.Id == id);
                if (relation == null) continue;
                count++;
                if (GUILayout.Button($"{relation.Id}  {relation.Key}", EditorStyles.miniButton)) _selectedId = relation.Id;
            }
            if (count == 0) EditorGUILayout.LabelField("（无）");
        }
    }
}
