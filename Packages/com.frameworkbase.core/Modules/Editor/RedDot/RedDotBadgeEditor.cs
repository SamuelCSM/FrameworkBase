using Framework.Foundation;
using UnityEditor;
using UnityEngine;

namespace Framework.Editor.RedDot
{
    [CustomEditor(typeof(RedDotBadge))]
    [CanEditMultipleObjects]
    public sealed class RedDotBadgeEditor : UnityEditor.Editor
    {
        private SerializedProperty _id;
        private SerializedProperty _legacyPath;
        private SerializedProperty _badgeRoot;
        private SerializedProperty _countText;
        private SerializedProperty _displayMode;
        private SerializedProperty _maxDisplayCount;
        private SerializedProperty _styleVariants;

        private void OnEnable()
        {
            _id = serializedObject.FindProperty("_redDotId");
            _legacyPath = serializedObject.FindProperty("_legacyPath");
            _badgeRoot = serializedObject.FindProperty("_badgeRoot");
            _countText = serializedObject.FindProperty("_countText");
            _displayMode = serializedObject.FindProperty("_displayMode");
            _maxDisplayCount = serializedObject.FindProperty("_maxDisplayCount");
            _styleVariants = serializedObject.FindProperty("_styleVariants");
        }

        public override void OnInspectorGUI()
        {
            serializedObject.Update();
            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.PropertyField(_id, new GUIContent("Red Dot ID"));
            if (GUILayout.Button("搜索", GUILayout.Width(58f)))
            {
                RedDotSearchWindow.Open(id =>
                {
                    serializedObject.Update();
                    _id.intValue = id;
                    serializedObject.ApplyModifiedProperties();
                });
            }
            EditorGUILayout.EndHorizontal();

            if (_id.hasMultipleDifferentValues)
            {
                EditorGUILayout.HelpBox("当前多选对象使用不同的红点 ID。", MessageType.Info);
            }
            else if (_id.intValue <= 0)
            {
                EditorGUILayout.HelpBox("请输入或搜索一个有效红点 ID。", MessageType.Error);
            }
            else if (RedDotEditorCatalog.TryGetNode(_id.intValue, out RedDotNodeDefinition node))
            {
                EditorGUILayout.HelpBox(
                    $"{node.Id}  {node.Key}\n{node.Description}\nModule={node.ModuleId}  Type={node.Kind}  Aggregation={node.Aggregation}",
                    MessageType.None);
            }
            else
            {
                EditorGUILayout.HelpBox($"红点 ID {_id.intValue} 不存在。", MessageType.Error);
            }

            if (!string.IsNullOrWhiteSpace(_legacyPath.stringValue))
            {
                EditorGUILayout.HelpBox($"检测到旧路径：{_legacyPath.stringValue}", MessageType.Warning);
                RedDotCatalog catalog = RedDotEditorCatalog.Get();
                RedDotNodeDefinition match = null;
                if (catalog?.Nodes != null)
                {
                    foreach (RedDotNodeDefinition candidate in catalog.Nodes)
                    {
                        if (candidate.Key == _legacyPath.stringValue)
                        {
                            match = candidate;
                            break;
                        }
                    }
                }
                using (new EditorGUI.DisabledScope(match == null))
                {
                    if (GUILayout.Button(match == null ? "目录中无同 Key 节点" : $"迁移到 ID {match.Id}"))
                    {
                        _id.intValue = match.Id;
                        _legacyPath.stringValue = string.Empty;
                    }
                }
            }

            EditorGUILayout.Space();
            EditorGUILayout.PropertyField(_badgeRoot);
            EditorGUILayout.PropertyField(_countText);
            EditorGUILayout.PropertyField(_displayMode);
            EditorGUILayout.PropertyField(_maxDisplayCount);
            EditorGUILayout.PropertyField(_styleVariants, new GUIContent("Style Variants (图标变体)"), true);
            if (_badgeRoot.objectReferenceValue == (target as RedDotBadge)?.gameObject)
                EditorGUILayout.HelpBox("Badge Root 不能指向组件自身；隐藏自身会触发 OnDisable 并退订。", MessageType.Error);
            serializedObject.ApplyModifiedProperties();
        }
    }
}
