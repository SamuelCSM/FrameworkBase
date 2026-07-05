using UnityEditor;
using UnityEngine;

namespace Editor.ExcelTool
{
    /// <summary>
    /// ExcelDataReader安装助手
    /// </summary>
    public class ExcelDataReaderInstaller
    {
        [MenuItem("Tools/Excel/Install ExcelDataReader")]
        public static void ShowInstallInstructions()
        {
            string message = @"ExcelDataReader安装指南

ExcelReader需要以下NuGet包：
• ExcelDataReader (3.7.0)
• ExcelDataReader.DataSet (3.7.0)
• System.Text.Encoding.CodePages (7.0.0)

这些包已添加到 Assets/packages.config 中。

安装步骤：

方法1：使用NuGet for Unity（推荐）
1. 安装 NuGet for Unity 插件
   - 从 GitHub 下载: https://github.com/GlitchEnzo/NuGetForUnity
   - 或从 Unity Asset Store 安装
2. 打开 Window > NuGet > Manage NuGet Packages
3. 搜索并安装以下包：
   - ExcelDataReader (3.7.0)
   - ExcelDataReader.DataSet (3.7.0)
   - System.Text.Encoding.CodePages (7.0.0)
4. 重启Unity编辑器

方法2：手动安装
1. 从 NuGet.org 下载以下包：
   - https://www.nuget.org/packages/ExcelDataReader/3.7.0
   - https://www.nuget.org/packages/ExcelDataReader.DataSet/3.7.0
   - https://www.nuget.org/packages/System.Text.Encoding.CodePages/7.0.0
2. 解压 .nupkg 文件（重命名为 .zip）
3. 将 lib/netstandard2.0 或 lib/netstandard2.1 中的 DLL 复制到 Assets/Plugins
4. 重启Unity编辑器

验证安装：
安装完成后，运行 Tools > Excel > Test Read Excel 来测试功能。

注意事项：
• 确保Unity版本支持 .NET Standard 2.1
• 如果遇到编译错误，检查DLL是否正确放置在Plugins文件夹
• ExcelDataReader仅支持 .xlsx 格式（Excel 2007及以上）";

            EditorUtility.DisplayDialog(
                "ExcelDataReader安装指南",
                message,
                "确定"
            );

            // 打开GitHub页面
            if (EditorUtility.DisplayDialog(
                "打开NuGet for Unity",
                "是否打开NuGet for Unity的GitHub页面？",
                "是",
                "否"))
            {
                Application.OpenURL("https://github.com/GlitchEnzo/NuGetForUnity");
            }
        }

        [MenuItem("Tools/Excel/Check ExcelDataReader Installation")]
        public static void CheckInstallation()
        {
            bool isInstalled = CheckExcelDataReaderInstalled();

            if (isInstalled)
            {
                EditorUtility.DisplayDialog(
                    "安装检查",
                    "✓ ExcelDataReader已正确安装！\n\n您可以使用 Tools > Excel > Test Read Excel 来测试功能。",
                    "确定"
                );
            }
            else
            {
                EditorUtility.DisplayDialog(
                    "安装检查",
                    "✗ ExcelDataReader未安装或安装不完整。\n\n请运行 Tools > Excel > Install ExcelDataReader 查看安装指南。",
                    "确定"
                );
            }
        }

        private static bool CheckExcelDataReaderInstalled()
        {
            try
            {
                // 尝试加载ExcelDataReader类型
                var type = System.Type.GetType("ExcelDataReader.ExcelReaderFactory, ExcelDataReader");
                return type != null;
            }
            catch
            {
                return false;
            }
        }
    }
}
