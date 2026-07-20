using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Framework.Foundation;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Framework.Editor.RedDot
{
    /// <summary>配置产物、Prefab 与 Build Scene 红点引用的构建门禁。</summary>
    public static class RedDotBuildValidator
    {
        public static bool ValidateForBuild(out string report)
        {
            var errors = new List<string>();
            var warnings = new List<string>();
            if (!RedDotConfigCompiler.TryCompile(out RedDotCatalog catalog, out string compileReport))
            {
                report = "RedDot.xlsx 非法：\n" + compileReport;
                return false;
            }

            ValidateGeneratedArtifact(
                RedDotConfigCompiler.GeneratedIdsPath,
                RedDotConfigCompiler.GenerateIdsCode(catalog),
                "业务 ID 常量",
                errors);

            var activeIds = new HashSet<int>(catalog.Nodes.Select(node => node.Id));
            var retiredIds = new HashSet<int>((catalog.RetiredIds ?? Array.Empty<RedDotRetiredIdDefinition>())
                .Select(item => item.Id));
            ValidatePrefabs(activeIds, retiredIds, errors, warnings);
            ValidateBuildScenes(activeIds, retiredIds, errors, warnings);

            var lines = new List<string>
            {
                $"配置：模块 {catalog.Modules.Length}，节点 {catalog.Nodes.Length}，关系 {catalog.Edges.Length}。"
            };
            if (warnings.Count > 0)
            {
                lines.Add("警告：");
                lines.AddRange(warnings.Select(value => "- " + value));
            }
            if (errors.Count > 0)
            {
                lines.Add("错误：");
                lines.AddRange(errors.Select(value => "- " + value));
            }
            report = string.Join(Environment.NewLine, lines);
            return errors.Count == 0;
        }

        private static void ValidateGeneratedArtifact(
            string path,
            string expected,
            string displayName,
            List<string> errors)
        {
            if (!File.Exists(path))
            {
                errors.Add($"缺少红点{displayName} {path}，请重新导入红点配置。");
                return;
            }

            string actual = File.ReadAllText(path);
            if (!string.Equals(NormalizeLineEndings(expected), NormalizeLineEndings(actual), StringComparison.Ordinal))
                errors.Add($"红点{displayName}与 RedDot.xlsx 不一致，请重新执行红点配置导入。");
        }

        private static string NormalizeLineEndings(string value)
            => (value ?? string.Empty).Replace("\r\n", "\n").Replace('\r', '\n');

        private static void ValidatePrefabs(
            HashSet<int> activeIds,
            HashSet<int> retiredIds,
            List<string> errors,
            List<string> warnings)
        {
            string[] guids = AssetDatabase.FindAssets("t:Prefab");
            for (int i = 0; i < guids.Length; i++)
            {
                string path = AssetDatabase.GUIDToAssetPath(guids[i]);
                GameObject prefab = AssetDatabase.LoadAssetAtPath<GameObject>(path);
                if (prefab == null) continue;
                RedDotBadge[] badges = prefab.GetComponentsInChildren<RedDotBadge>(true);
                for (int j = 0; j < badges.Length; j++) ValidateBadge(badges[j], path, activeIds, retiredIds, errors, warnings);
            }
        }

        private static void ValidateBuildScenes(
            HashSet<int> activeIds,
            HashSet<int> retiredIds,
            List<string> errors,
            List<string> warnings)
        {
            SceneSetup[] original = EditorSceneManager.GetSceneManagerSetup();
            try
            {
                foreach (EditorBuildSettingsScene buildScene in EditorBuildSettings.scenes)
                {
                    if (!buildScene.enabled || string.IsNullOrEmpty(buildScene.path)) continue;
                    Scene scene = default;
                    for (int loadedIndex = 0; loadedIndex < UnityEngine.SceneManagement.SceneManager.sceneCount; loadedIndex++)
                    {
                        Scene loaded = UnityEngine.SceneManagement.SceneManager.GetSceneAt(loadedIndex);
                        if (string.Equals(loaded.path, buildScene.path, StringComparison.OrdinalIgnoreCase))
                        {
                            scene = loaded;
                            break;
                        }
                    }
                    bool openedHere = !scene.IsValid() || !scene.isLoaded;
                    if (openedHere) scene = EditorSceneManager.OpenScene(buildScene.path, OpenSceneMode.Additive);
                    foreach (GameObject root in scene.GetRootGameObjects())
                    {
                        RedDotBadge[] badges = root.GetComponentsInChildren<RedDotBadge>(true);
                        for (int i = 0; i < badges.Length; i++)
                            ValidateBadge(badges[i], buildScene.path, activeIds, retiredIds, errors, warnings);
                    }
                    if (openedHere) EditorSceneManager.CloseScene(scene, true);
                }
            }
            catch (Exception ex)
            {
                errors.Add("扫描 Build Scene 红点引用失败：" + ex.Message);
            }
            finally
            {
                EditorSceneManager.RestoreSceneManagerSetup(original);
            }
        }

        private static void ValidateBadge(
            RedDotBadge badge,
            string assetPath,
            HashSet<int> activeIds,
            HashSet<int> retiredIds,
            List<string> errors,
            List<string> warnings)
        {
            string context = $"{assetPath} :: {GetHierarchyPath(badge.transform)}";
            int id = badge.RedDotId;
            if (id <= 0) errors.Add($"{context} 未配置红点 ID。");
            else if (retiredIds.Contains(id)) errors.Add($"{context} 仍引用退休红点 ID {id}。");
            else if (!activeIds.Contains(id)) errors.Add($"{context} 引用不存在的红点 ID {id}。");
            if (!string.IsNullOrWhiteSpace(badge.LegacyPath)) warnings.Add($"{context} 仍保留旧路径 {badge.LegacyPath}。");

            var serialized = new SerializedObject(badge);
            GameObject root = serialized.FindProperty("_badgeRoot")?.objectReferenceValue as GameObject;
            if (root == null) errors.Add($"{context} 未配置 Badge Root。");
            else if (root == badge.gameObject) errors.Add($"{context} 的 Badge Root 指向组件自身。");
        }

        private static string GetHierarchyPath(Transform transform)
        {
            string path = transform.name;
            while (transform.parent != null)
            {
                transform = transform.parent;
                path = transform.name + "/" + path;
            }
            return path;
        }
    }

    /// <summary>所有本地/CI Player Build 都执行相同红点门禁。</summary>
    public sealed class RedDotBuildPreprocessor : IPreprocessBuildWithReport
    {
        public int callbackOrder => 0;

        public void OnPreprocessBuild(BuildReport report)
        {
            if (!RedDotBuildValidator.ValidateForBuild(out string validationReport))
                throw new BuildFailedException("红点配置门禁未通过：\n" + validationReport);
            Debug.Log("[RedDotBuild] 红点配置门禁通过。\n" + validationReport);
        }
    }
}
