using System;
using System.Collections.Generic;
using System.Linq;
using Framework.Foundation;
using UnityEditor;
using UnityEngine;

namespace Framework.Editor.RedDot
{
    /// <summary>按模块号段分配下一个未使用且未退休的节点 ID；模块数量小，使用下拉选择。</summary>
    public sealed class RedDotIdAllocatorWindow : EditorWindow
    {
        private int _moduleIndex;
        private string _key = string.Empty;

        [MenuItem("Tools/Framework/Red Dot/Allocate ID")]
        private static void Open()
        {
            var window = GetWindow<RedDotIdAllocatorWindow>("Allocate Red Dot ID");
            window.minSize = new Vector2(500f, 220f);
        }

        private void OnGUI()
        {
            RedDotCatalog catalog = RedDotEditorCatalog.Get();
            if (catalog?.Modules == null || catalog.Modules.Length == 0)
            {
                EditorGUILayout.HelpBox("请先导入包含 red_dot_module_ref 的配置。", MessageType.Error);
                return;
            }

            RedDotModuleDefinition[] modules = catalog.Modules.OrderBy(value => value.Id).ToArray();
            _moduleIndex = Mathf.Clamp(_moduleIndex, 0, modules.Length - 1);
            string[] labels = modules.Select(module =>
                $"{module.Id} {module.Key} ({module.IdMin}~{module.IdMax})").ToArray();
            _moduleIndex = EditorGUILayout.Popup("Module", _moduleIndex, labels);
            _key = EditorGUILayout.TextField("Planned Key", _key);

            RedDotModuleDefinition selected = modules[_moduleIndex];
            int next = FindNextId(catalog, selected);
            EditorGUILayout.Space();
            if (next <= 0)
            {
                EditorGUILayout.HelpBox($"模块 {selected.Key} 号段已耗尽。", MessageType.Error);
                return;
            }

            EditorGUILayout.LabelField("Next ID", next.ToString(), EditorStyles.boldLabel);
            if (!string.IsNullOrWhiteSpace(_key) && !(_key == selected.Key || _key.StartsWith(selected.Key + ".", StringComparison.Ordinal)))
                EditorGUILayout.HelpBox($"Key 建议以 {selected.Key}. 开头。", MessageType.Warning);
            if (GUILayout.Button("复制 ID 到剪贴板"))
            {
                EditorGUIUtility.systemCopyBuffer = next.ToString();
                ShowNotification(new GUIContent($"已复制 {next}"));
            }
            EditorGUILayout.HelpBox("本工具只负责安全分配和复制；请把 ID 写入 red_dot_node_ref 后重新导出配置。", MessageType.Info);
        }

        private static int FindNextId(RedDotCatalog catalog, RedDotModuleDefinition module)
        {
            var used = new HashSet<int>(catalog.Nodes.Select(node => node.Id));
            foreach (RedDotRetiredIdDefinition item in catalog.RetiredIds ?? Array.Empty<RedDotRetiredIdDefinition>())
                used.Add(item.Id);
            for (int id = module.IdMin; id <= module.IdMax; id++)
                if (!used.Contains(id)) return id;
            return 0;
        }
    }
}
