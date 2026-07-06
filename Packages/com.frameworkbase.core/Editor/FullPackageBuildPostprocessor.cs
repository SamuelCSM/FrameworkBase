using System;
using System.IO;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEngine;

namespace Framework.Editor
{
    /// <summary>
    /// 整包构建完成后的收尾：自动清理不可发布的调试文件夹（可开关），并按需回切 Addressables Profile。
    /// </summary>
    public class FullPackageBuildPostprocessor : IPostprocessBuildWithReport
    {
        /// <summary>“构建后自动清理调试文件夹”开关的 EditorPrefs 键（默认开）。</summary>
        private const string CleanupDebugFoldersPrefsKey = "ClientBase.FullPackage.CleanupDebugFoldersAfterBuild";

        /// <summary>菜单项路径，用于在编辑器里开关清理行为并显示勾选态。</summary>
        private const string CleanupMenuPath = "Framework/发布/构建后清理调试文件夹";

        /// <summary>
        /// Unity IL2CPP / Burst 构建产出的、不应随包发布的文件夹名后缀。
        /// 每次构建必然生成、无法从 PlayerSettings 关闭，只能构建后删除。
        /// </summary>
        private static readonly string[] NonShippableFolderSuffixes =
        {
            "_BurstDebugInformation_DoNotShip",
            "_BackUpThisFolder_ButDontShipItWithYourGame"
        };

        public int callbackOrder => 0;

        public void OnPostprocessBuild(BuildReport report)
        {
            // 仅在构建成功时收尾；失败时保留现场便于排查。
            if (report.summary.result == BuildResult.Succeeded)
            {
                CleanupNonShippableFolders(report.summary.outputPath);
            }

            SwitchBackProfileIfRequested(report);
        }

        /// <summary>
        /// 删除构建输出目录下的不可发布调试文件夹（Burst 调试信息 / IL2CPP 符号备份）。
        /// </summary>
        /// <param name="outputPath">构建产物主文件路径（Windows 下为 .exe，其兄弟目录即输出目录）。</param>
        private static void CleanupNonShippableFolders(string outputPath)
        {
            if (!EditorPrefs.GetBool(CleanupDebugFoldersPrefsKey, true))
                return;

            if (string.IsNullOrEmpty(outputPath))
                return;

            // 输出目录：exe 类产物取其所在目录；目录类产物取上一级（调试文件夹是其兄弟）。
            string buildDir = File.Exists(outputPath)
                ? Path.GetDirectoryName(outputPath)
                : Path.GetDirectoryName(outputPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));

            if (string.IsNullOrEmpty(buildDir) || !Directory.Exists(buildDir))
                return;

            foreach (string dir in Directory.GetDirectories(buildDir))
            {
                string name = Path.GetFileName(dir);
                if (!MatchesNonShippableSuffix(name))
                    continue;

                try
                {
                    Directory.Delete(dir, true);
                    Debug.Log($"[FullPackage] 已清理不可发布调试文件夹：{name}");
                }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
                {
                    // 受控降级：文件被占用/权限不足时仅告警，不影响构建结果，可手动删除。
                    Debug.LogWarning($"[FullPackage] 清理调试文件夹失败（可手动删除）：{name} —— {ex.Message}");
                }
            }
        }

        /// <summary>
        /// 判断文件夹名是否命中任一不可发布后缀。
        /// </summary>
        /// <param name="folderName">文件夹名（不含路径）。</param>
        /// <returns>命中返回 true。</returns>
        private static bool MatchesNonShippableSuffix(string folderName)
        {
            foreach (string suffix in NonShippableFolderSuffixes)
            {
                if (folderName.EndsWith(suffix, StringComparison.Ordinal))
                    return true;
            }

            return false;
        }

        /// <summary>
        /// 整包一键流程登记了自动回切时，构建成功后切回 HotUpdateRemote（只消费一次）。
        /// </summary>
        /// <param name="report">构建报告。</param>
        private static void SwitchBackProfileIfRequested(BuildReport report)
        {
            if (!EditorPrefs.GetBool(FullPackagePublisherWindow.AutoSwitchBackAfterBuildPrefsKey, false))
                return;

            // 只消费一次，避免后续普通构建被误触发。
            EditorPrefs.DeleteKey(FullPackagePublisherWindow.AutoSwitchBackAfterBuildPrefsKey);

            if (report.summary.result != BuildResult.Succeeded)
            {
                Debug.LogWarning("[FullPackage] Build 未成功，跳过自动切回 HotUpdateRemote。");
                return;
            }

            AddressablesSetup.SwitchToHotUpdateRemote();
            Debug.Log("[FullPackage] Build 成功，已自动切回 HotUpdateRemote。");
        }

        /// <summary>
        /// 切换“构建后自动清理调试文件夹”开关。
        /// </summary>
        [MenuItem(CleanupMenuPath)]
        private static void ToggleCleanupDebugFolders()
        {
            bool enabled = EditorPrefs.GetBool(CleanupDebugFoldersPrefsKey, true);
            EditorPrefs.SetBool(CleanupDebugFoldersPrefsKey, !enabled);
        }

        /// <summary>
        /// 菜单勾选态：反映当前开关。
        /// </summary>
        /// <returns>恒为 true（菜单项始终可点）。</returns>
        [MenuItem(CleanupMenuPath, true)]
        private static bool ToggleCleanupDebugFoldersValidate()
        {
            Menu.SetChecked(CleanupMenuPath, EditorPrefs.GetBool(CleanupDebugFoldersPrefsKey, true));
            return true;
        }
    }
}
