using UnityEngine;
using UnityEditor;

namespace Editor
{
    /// <summary>
    /// Protobuf安装助手
    /// 提供protobuf-net包的安装指导
    /// </summary>
    public class ProtobufInstaller : EditorWindow
    {
        [MenuItem("Framework/Install Protobuf-net")]
        public static void ShowWindow()
        {
            var window = GetWindow<ProtobufInstaller>("Protobuf Installer");
            window.minSize = new Vector2(500, 400);
            window.Show();
        }

        private Vector2 scrollPosition;

        private void OnGUI()
        {
            scrollPosition = EditorGUILayout.BeginScrollView(scrollPosition);

            GUILayout.Label("Protobuf-net Installation Guide", EditorStyles.boldLabel);
            GUILayout.Space(10);

            EditorGUILayout.HelpBox(
                "The Framework requires protobuf-net for network message serialization. " +
                "Please follow one of the installation methods below.",
                MessageType.Info);

            GUILayout.Space(10);

            // Method 1: NuGet for Unity
            DrawInstallationMethod(
                "Method 1: NuGet for Unity (Recommended)",
                new string[]
                {
                    "1. Install 'NuGet for Unity' from Unity Asset Store or GitHub",
                    "2. Open: Window > NuGet > Manage NuGet Packages",
                    "3. Search for 'protobuf-net'",
                    "4. Install version 2.4.x (do NOT use 3.x: its repeated fields crash on IL2CPP/AOT)",
                    "5. Restart Unity Editor"
                },
                "https://github.com/GlitchEnzo/NuGetForUnity"
            );

            GUILayout.Space(10);

            // Method 2: Manual Installation
            DrawInstallationMethod(
                "Method 2: Manual Installation",
                new string[]
                {
                    "1. Download protobuf-net from GitHub releases",
                    "2. Extract the following DLL (2.4.x is a single assembly, there is no protobuf-net.Core):",
                    "   - protobuf-net.dll",
                    "3. Create folder: Assets/Plugins/protobuf-net/",
                    "4. Copy DLLs to the Plugins folder",
                    "5. Restart Unity Editor"
                },
                "https://github.com/protobuf-net/protobuf-net/releases"
            );

            GUILayout.Space(10);

            // Method 3: Package Manager
            DrawInstallationMethod(
                "Method 3: Unity Package Manager (Advanced)",
                new string[]
                {
                    "1. Open: Window > Package Manager",
                    "2. Click '+' > Add package from git URL",
                    "3. Enter: com.unity.nuget.protobuf-net",
                    "4. Note: This method may not work for all Unity versions"
                },
                null
            );

            GUILayout.Space(20);

            // Verification section
            GUILayout.Label("Verification", EditorStyles.boldLabel);
            EditorGUILayout.HelpBox(
                "After installation, verify that:\n" +
                "1. No compilation errors in Console\n" +
                "2. Framework/Network scripts compile successfully\n" +
                "3. You can create Protobuf messages with [ProtoContract] attribute",
                MessageType.Info);

            GUILayout.Space(10);

            if (GUILayout.Button("Open Setup Documentation", GUILayout.Height(30)))
            {
                var path = "Assets/Scripts/Framework/Network/PROTOBUF_SETUP.md";
                var asset = AssetDatabase.LoadAssetAtPath<TextAsset>(path);
                if (asset != null)
                {
                    Selection.activeObject = asset;
                    EditorGUIUtility.PingObject(asset);
                }
                else
                {
                    Debug.LogWarning($"Documentation not found at: {path}");
                }
            }

            GUILayout.Space(10);

            if (GUILayout.Button("Check Installation Status", GUILayout.Height(30)))
            {
                CheckInstallationStatus();
            }

            EditorGUILayout.EndScrollView();
        }

        private void DrawInstallationMethod(string title, string[] steps, string url)
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
                if (GUILayout.Button($"Open: {url}", GUILayout.Height(25)))
                {
                    Application.OpenURL(url);
                }
            }
        }

        private void CheckInstallationStatus()
        {
            // Try to find protobuf-net assembly
            var assemblies = System.AppDomain.CurrentDomain.GetAssemblies();
            bool found = false;

            foreach (var assembly in assemblies)
            {
                if (assembly.GetName().Name.Contains("protobuf-net"))
                {
                    found = true;
                    Debug.Log($"✓ Found: {assembly.GetName().Name} (Version: {assembly.GetName().Version})");
                }
            }

            if (found)
            {
                EditorUtility.DisplayDialog(
                    "Installation Check",
                    "✓ protobuf-net is installed!\n\n" +
                    "You can now use Protobuf serialization in your project.",
                    "OK");
            }
            else
            {
                EditorUtility.DisplayDialog(
                    "Installation Check",
                    "✗ protobuf-net is NOT installed.\n\n" +
                    "Please follow one of the installation methods above.",
                    "OK");
            }
        }
    }
}
