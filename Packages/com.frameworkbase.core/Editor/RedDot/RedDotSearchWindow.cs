using System;
using System.Collections.Generic;
using System.Linq;
using Framework.Foundation;
using UnityEditor;
using UnityEngine;

namespace Framework.Editor.RedDot
{
    internal sealed class RedDotSearchWindow : EditorWindow
    {
        private Action<int> _selected;
        private string _query = string.Empty;
        private Vector2 _scroll;

        public static void Open(Action<int> selected)
        {
            var window = CreateInstance<RedDotSearchWindow>();
            window.titleContent = new GUIContent("选择红点 ID");
            window.minSize = new Vector2(620f, 420f);
            window._selected = selected;
            window.ShowUtility();
        }

        private void OnGUI()
        {
            RedDotCatalog catalog = RedDotEditorCatalog.Get();
            if (catalog == null)
            {
                EditorGUILayout.HelpBox("红点目录不存在或非法，请先导入 RedDot.xlsx。", MessageType.Error);
                if (GUILayout.Button("导入配置")) RedDotConfigCompiler.ImportMenu();
                return;
            }

            EditorGUILayout.LabelField("按 ID、Key、描述或模块 ID 搜索", EditorStyles.boldLabel);
            GUI.SetNextControlName("Search");
            _query = EditorGUILayout.TextField(_query);
            if (Event.current.type == EventType.Repaint) EditorGUI.FocusTextInControl("Search");

            string query = (_query ?? string.Empty).Trim();
            IEnumerable<RedDotNodeDefinition> nodes = catalog.Nodes.OrderBy(node => node.Id);
            if (query.Length > 0)
            {
                nodes = nodes.Where(node =>
                    node.Id.ToString().Contains(query) ||
                    (node.Key?.IndexOf(query, StringComparison.OrdinalIgnoreCase) ?? -1) >= 0 ||
                    (node.Description?.IndexOf(query, StringComparison.OrdinalIgnoreCase) ?? -1) >= 0 ||
                    node.ModuleId.ToString().Contains(query));
            }

            _scroll = EditorGUILayout.BeginScrollView(_scroll);
            foreach (RedDotNodeDefinition node in nodes.Take(500))
            {
                EditorGUILayout.BeginHorizontal("box");
                if (GUILayout.Button(node.Id.ToString(), GUILayout.Width(88f)))
                {
                    _selected?.Invoke(node.Id);
                    Close();
                }
                EditorGUILayout.LabelField(node.Key, GUILayout.Width(250f));
                EditorGUILayout.LabelField(node.Kind.ToString(), GUILayout.Width(80f));
                EditorGUILayout.LabelField(node.Description ?? string.Empty);
                EditorGUILayout.EndHorizontal();
            }
            EditorGUILayout.EndScrollView();
        }
    }
}
