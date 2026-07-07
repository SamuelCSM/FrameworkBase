using System.IO;
using System.Text;
using Framework.Core;
using UnityEditor;
using UnityEngine;

namespace Framework.Editor
{
    /// <summary>
    /// 新项目初始化向导：从壳工程派生业务项目时，一屏完成
    /// PlayerSettings 三件套（产品名/公司名/Bundle ID）+ AppConfig 创建与核心字段填充，
    /// 并输出"框架管不到、必须人工确认"的派生清单——降低框架复用门槛，
    /// 避免新项目带着壳工程的包名/演示协议上架。
    ///
    /// 菜单：Framework → New Project Scaffold。
    /// </summary>
    public class ProjectScaffoldWindow : EditorWindow
    {
        private string _productName = "";
        private string _companyName = "";
        private string _bundleId = "";
        private string _appEnv = "dev";
        private string _gameServerHost = "127.0.0.1";
        private int _gameServerPort = 9000;
        private string _updateServerUrl = "";

        [MenuItem("Framework/New Project Scaffold")]
        public static void Open()
        {
            var window = GetWindow<ProjectScaffoldWindow>("New Project Scaffold");
            window.minSize = new Vector2(460, 380);
            window.LoadCurrent();
        }

        private void LoadCurrent()
        {
            _productName = PlayerSettings.productName;
            _companyName = PlayerSettings.companyName;
            _bundleId = PlayerSettings.GetApplicationIdentifier(BuildTargetGroup.Android);

            AppConfigAsset config = FindAppConfig();
            if (config != null)
            {
                _appEnv = config.AppEnv;
                _gameServerHost = config.GameServerHost;
                _gameServerPort = config.GameServerPort;
                _updateServerUrl = config.UpdateServerUrl;
            }
        }

        private void OnGUI()
        {
            EditorGUILayout.HelpBox(
                "从壳工程派生新项目：填完点 Apply，一次写入 PlayerSettings 与 AppConfig；" +
                "自动化覆盖不到的项会输出人工清单到 Console。", MessageType.Info);

            EditorGUILayout.LabelField("应用标识", EditorStyles.boldLabel);
            _productName = EditorGUILayout.TextField("产品名", _productName);
            _companyName = EditorGUILayout.TextField("公司名", _companyName);
            _bundleId = EditorGUILayout.TextField("Bundle ID", _bundleId);

            EditorGUILayout.Space(8);
            EditorGUILayout.LabelField("AppConfig（Resources/AppConfig.asset）", EditorStyles.boldLabel);
            _appEnv = EditorGUILayout.TextField("环境 (dev/staging/prod)", _appEnv);
            _gameServerHost = EditorGUILayout.TextField("游戏服 Host", _gameServerHost);
            _gameServerPort = EditorGUILayout.IntField("游戏服 Port", _gameServerPort);
            _updateServerUrl = EditorGUILayout.TextField("热更服务器 URL", _updateServerUrl);

            EditorGUILayout.Space(12);
            using (new EditorGUI.DisabledScope(!InputsValid()))
            {
                if (GUILayout.Button("Apply（写入设置并输出派生清单）", GUILayout.Height(32)))
                    Apply();
            }
        }

        private bool InputsValid()
        {
            return !string.IsNullOrWhiteSpace(_productName) &&
                   !string.IsNullOrWhiteSpace(_companyName) &&
                   !string.IsNullOrWhiteSpace(_bundleId) && _bundleId.Contains(".");
        }

        private void Apply()
        {
            // 1. PlayerSettings 三件套（双平台 Bundle ID 同步写）
            PlayerSettings.productName = _productName;
            PlayerSettings.companyName = _companyName;
            PlayerSettings.SetApplicationIdentifier(BuildTargetGroup.Android, _bundleId);
            PlayerSettings.SetApplicationIdentifier(BuildTargetGroup.iOS, _bundleId);

            // 2. AppConfig：找不到就在 Assets/Resources 下创建（运行时按该路径 Load）
            AppConfigAsset config = FindAppConfig();
            if (config == null)
            {
                if (!AssetDatabase.IsValidFolder("Assets/Resources"))
                    AssetDatabase.CreateFolder("Assets", "Resources");
                config = ScriptableObject.CreateInstance<AppConfigAsset>();
                AssetDatabase.CreateAsset(config, "Assets/Resources/AppConfig.asset");
                Debug.Log("[Scaffold] 已创建 Assets/Resources/AppConfig.asset");
            }

            config.AppEnv = _appEnv;
            config.GameServerHost = _gameServerHost;
            config.GameServerPort = _gameServerPort;
            config.UpdateServerUrl = _updateServerUrl;
            EditorUtility.SetDirty(config);
            AssetDatabase.SaveAssets();

            // 3. 派生清单：自动化覆盖不到、漏掉会出事故的项，逐条列给人工确认
            var sb = new StringBuilder();
            sb.AppendLine("[Scaffold] 设置已写入 ✓。以下为人工派生清单（做完一项划一项）：");
            sb.AppendLine("  □ 清理演示协议：proto/sample/ 与其生成代码（跑 gen-proto 重新生成协议目录）");
            sb.AppendLine("  □ 替换应用 icon / 启动图（Player Settings）");
            sb.AppendLine("  □ HybridCLR Settings：hotUpdateAssemblies 对齐新项目程序集组（或走 AppConfig.HotUpdateAssemblyFiles）");
            sb.AppendLine("  □ 生成热更清单签名密钥对：Framework → Hot Update Security → Generate Signing Key Pair（私钥存工程外）");
            sb.AppendLine("  □ AppConfig.UpdateManifestPublicKey 填入公钥；prod 环境 UpdateServerUrl 必须 HTTPS");
            sb.AppendLine("  □ CI：GitHub Secrets 配置 UNITY_LICENSE / UNITY_EMAIL / UNITY_PASSWORD");
            sb.AppendLine("  □ 事件字典：组合根注册业务埋点 schema（AnalyticsSchemaRegistry.Shared）");
            sb.AppendLine("  □ 错误码字典：组合根注册协议错误码段（ErrorCodeRegistry.Shared）");
            sb.AppendLine("  □ 合规市场：接 PrivacyConsent + CollectionEnabled 闸门（Core/Privacy/PRIVACY_GUIDE.md）");
            sb.AppendLine("  □ 跑一遍 Framework → Validate Addressables 与 run-ci.bat 确认门禁全绿");
            Debug.Log(sb.ToString());

            EditorUtility.DisplayDialog("New Project Scaffold",
                "设置已写入。人工派生清单已输出到 Console，逐项确认后再发首个版本。", "确定");
        }

        private static AppConfigAsset FindAppConfig()
        {
            string[] guids = AssetDatabase.FindAssets("t:AppConfigAsset");
            if (guids.Length == 0)
                return null;
            string path = AssetDatabase.GUIDToAssetPath(guids[0]);
            return AssetDatabase.LoadAssetAtPath<AppConfigAsset>(path);
        }
    }
}
