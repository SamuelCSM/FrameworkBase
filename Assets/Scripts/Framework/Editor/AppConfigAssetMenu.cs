using System.IO;
using Framework.Core;
using UnityEditor;
using UnityEngine;

namespace Framework.Editor
{
    /// <summary>
    /// 创建 / 迁移 AppConfig ScriptableObject。
    /// </summary>
    [InitializeOnLoad]
    public static class AppConfigAssetMenu
    {
        private const string AssetPath = "Assets/Resources/AppConfig.asset";
        private const string LegacyJsonPath = "Assets/Resources/AppConfig.json";

        static AppConfigAssetMenu()
        {
            EditorApplication.delayCall += EnsureAppConfigAssetExists;
        }

        private static void EnsureAppConfigAssetExists()
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode)
                return;
            if (AssetDatabase.LoadAssetAtPath<AppConfigAsset>(AssetPath) != null)
                return;

            CreateAppConfigAssetInternal(logSelection: false);
        }

        [MenuItem("Framework/App Config/Create AppConfig Asset")]
        public static void CreateAppConfigAsset()
        {
            CreateAppConfigAssetInternal(logSelection: true);
        }

        private static void CreateAppConfigAssetInternal(bool logSelection)
        {
            EnsureResourcesFolder();

            var existing = AssetDatabase.LoadAssetAtPath<AppConfigAsset>(AssetPath);
            if (existing != null)
            {
                if (logSelection)
                {
                    Selection.activeObject = existing;
                    EditorGUIUtility.PingObject(existing);
                    Debug.Log($"[AppConfig] 已存在: {AssetPath}");
                }
                return;
            }

            var asset = ScriptableObject.CreateInstance<AppConfigAsset>();
            TryMigrateFromLegacyJson(asset);

            AssetDatabase.CreateAsset(asset, AssetPath);
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();

            if (logSelection)
            {
                Selection.activeObject = asset;
                EditorGUIUtility.PingObject(asset);
            }
            Debug.Log($"[AppConfig] 已创建 {AssetPath}");
        }
        [MenuItem("Framework/App Config/Migrate From AppConfig.json")]
        public static void MigrateFromJsonMenu()
        {
            EnsureResourcesFolder();

            var asset = AssetDatabase.LoadAssetAtPath<AppConfigAsset>(AssetPath);
            if (asset == null)
            {
                asset = ScriptableObject.CreateInstance<AppConfigAsset>();
                AssetDatabase.CreateAsset(asset, AssetPath);
            }

            if (TryMigrateFromLegacyJson(asset))
            {
                EditorUtility.SetDirty(asset);
                AssetDatabase.SaveAssets();
                Debug.Log("[AppConfig] 已从 AppConfig.json 迁移到 ScriptableObject");
            }
            else
            {
                Debug.LogWarning("[AppConfig] 未找到 AppConfig.json 或迁移失败");
            }
        }

        private static void EnsureResourcesFolder()
        {
            if (!AssetDatabase.IsValidFolder("Assets/Resources"))
                AssetDatabase.CreateFolder("Assets", "Resources");
        }

        private static bool TryMigrateFromLegacyJson(AppConfigAsset asset)
        {
            if (!File.Exists(LegacyJsonPath))
                return false;

            try
            {
                string json = File.ReadAllText(LegacyJsonPath);
                var temp = JsonUtility.FromJson<LegacyAppConfigJson>(json);
                if (temp == null)
                    return false;

                if (!string.IsNullOrEmpty(temp.UpdateServerUrl))
                    asset.UpdateServerUrl = temp.UpdateServerUrl;
                if (!string.IsNullOrEmpty(temp.AppEnv))
                    asset.AppEnv = temp.AppEnv;
                if (temp.NetworkTimeoutSeconds > 0)
                    asset.NetworkTimeoutSeconds = temp.NetworkTimeoutSeconds;

                asset.UseNetworkLogin = true;
                asset.GameServerHost = "127.0.0.1";
                asset.GameServerPort = 9000;
                return true;
            }
            catch (System.Exception ex)
            {
                Debug.LogError($"[AppConfig] 迁移 JSON 失败: {ex.Message}");
                return false;
            }
        }

        [System.Serializable]
        private class LegacyAppConfigJson
        {
            // 字段由 JsonUtility.FromJson 反射赋值，编译器无法静态识别，故局部关闭 CS0649（字段从未赋值）误报。
#pragma warning disable CS0649
            public string UpdateServerUrl;
            public string AppEnv;
            public int NetworkTimeoutSeconds;
#pragma warning restore CS0649
        }
    }
}
