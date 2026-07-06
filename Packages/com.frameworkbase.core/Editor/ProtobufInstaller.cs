using UnityEngine;
using UnityEditor;

namespace Editor
{
    /// <summary>
    /// Google.Protobuf 安装助手。
    /// 提供 Google.Protobuf 运行时的安装指导，以及一键协议生成器 ProtoGen 的使用入口。
    /// </summary>
    public class ProtobufInstaller : EditorWindow
    {
        [MenuItem("Framework/Protobuf Setup")]
        public static void ShowWindow()
        {
            var window = GetWindow<ProtobufInstaller>("Protobuf Setup");
            window.minSize = new Vector2(520, 420);
            window.Show();
        }

        private Vector2 scrollPosition;

        private void OnGUI()
        {
            scrollPosition = EditorGUILayout.BeginScrollView(scrollPosition);

            GUILayout.Label("Google.Protobuf Setup", EditorStyles.boldLabel);
            GUILayout.Space(10);

            EditorGUILayout.HelpBox(
                "网络层序列化使用官方 Google.Protobuf（protoc 生成显式代码，IL2CPP/AOT 安全）。" +
                "协议消息由仓库根的一键生成器 ProtoGen 从 proto/*.proto 生成，请勿手写协议类。",
                MessageType.Info);

            GUILayout.Space(10);

            DrawSection(
                "1) 安装 Google.Protobuf 运行时（NuGetForUnity）",
                new string[]
                {
                    "本工程已内置 NuGetForUnity，且 Assets/packages.config 已声明 Google.Protobuf。",
                    "打开 Unity 时会自动还原到 Assets/Packages/Google.Protobuf.<版本>/。",
                    "如未自动还原：菜单 NuGet > Restore Packages，或 Manage NuGet Packages 搜索 Google.Protobuf 安装。",
                    "只需 Google.Protobuf.dll；若报缺 System.Memory/System.Buffers，再按提示补对应 NuGet 包。"
                },
                "https://github.com/GlitchEnzo/NuGetForUnity");

            GUILayout.Space(10);

            DrawSection(
                "2) 定义并生成协议",
                new string[]
                {
                    "在仓库根 proto/ 下编写 proto3 源，消息命名遵循 <方向>_<主号3位>_<子号3位>_<名称>。",
                    "双击仓库根 gen-proto.bat（或运行 Tools/ProtoGen），双端协议类 + 路由伴生 partial 一并生成。",
                    "生成物勿手改；使用说明见 Tools/ProtoGen/README.md。"
                },
                null);

            GUILayout.Space(20);

            if (GUILayout.Button("检查 Google.Protobuf 是否已安装", GUILayout.Height(30)))
            {
                CheckInstallationStatus();
            }

            EditorGUILayout.EndScrollView();
        }

        /// <summary>
        /// 绘制一个带标题、步骤与可选链接的区块。
        /// </summary>
        /// <param name="title">区块标题。</param>
        /// <param name="steps">步骤文本行。</param>
        /// <param name="url">可选的外部链接。</param>
        private void DrawSection(string title, string[] steps, string url)
        {
            GUILayout.Label(title, EditorStyles.boldLabel);

            EditorGUI.indentLevel++;
            foreach (var step in steps)
            {
                GUILayout.Label(step, EditorStyles.wordWrappedLabel);
            }
            EditorGUI.indentLevel--;

            if (!string.IsNullOrEmpty(url))
            {
                if (GUILayout.Button($"打开: {url}", GUILayout.Height(25)))
                {
                    Application.OpenURL(url);
                }
            }
        }

        /// <summary>
        /// 检查当前程序域内是否已加载 Google.Protobuf 运行时。
        /// </summary>
        private void CheckInstallationStatus()
        {
            var assemblies = System.AppDomain.CurrentDomain.GetAssemblies();
            bool found = false;

            foreach (var assembly in assemblies)
            {
                if (assembly.GetName().Name == "Google.Protobuf")
                {
                    found = true;
                    Debug.Log($"✓ Found: {assembly.GetName().Name} (Version: {assembly.GetName().Version})");
                }
            }

            EditorUtility.DisplayDialog(
                "安装检查",
                found
                    ? "✓ Google.Protobuf 已安装，可以正常收发协议。"
                    : "✗ 未检测到 Google.Protobuf。请先按步骤 1 通过 NuGetForUnity 还原。",
                "OK");
        }
    }
}
