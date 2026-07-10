using System;
using System.IO;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEngine;

namespace Framework.Editor
{
    /// <summary>
    /// 整包构建完成后的安全收尾：归档 IL2CPP/Burst 符号与调试信息，并按需回切 Addressables Profile。
    /// <para>
    /// 符号是线上崩溃还原调用栈的必要产物，默认绝不删除。归档目录位于工程 Artifacts/Symbols，
    /// 后续 CI 可上传到崩溃平台或制品库；上传职责由发布流水线扩展，不在后处理器中静默丢弃文件。
    /// </para>
    /// </summary>
    public sealed class FullPackageBuildPostprocessor : IPostprocessBuildWithReport
    {
        private static readonly string[] SymbolFolderSuffixes =
        {
            "_BurstDebugInformation_DoNotShip",
            "_BackUpThisFolder_ButDontShipItWithYourGame",
        };

        public int callbackOrder => 0;

        public void OnPostprocessBuild(BuildReport report)
        {
            if (report.summary.result == BuildResult.Succeeded)
                ArchiveSymbolFolders(report);

            SwitchBackProfileIfRequested(report);
        }

        /// <summary>
        /// 把构建输出旁的符号目录完整复制到按版本、平台和时间隔离的归档目录，并校验文件数量与总字节数。
        /// 原始目录保留不删，避免归档或后续上传链路异常时永久丢失符号。
        /// </summary>
        private static void ArchiveSymbolFolders(BuildReport report)
        {
            string outputPath = report.summary.outputPath;
            if (string.IsNullOrWhiteSpace(outputPath)) return;
            string buildDir = File.Exists(outputPath)
                ? Path.GetDirectoryName(outputPath)
                : Path.GetDirectoryName(outputPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            if (string.IsNullOrWhiteSpace(buildDir) || !Directory.Exists(buildDir)) return;

            string projectRoot = Directory.GetParent(Application.dataPath)?.FullName ?? Directory.GetCurrentDirectory();
            string archiveRoot = Path.Combine(
                projectRoot,
                "Artifacts",
                "Symbols",
                Sanitize(PlayerSettings.bundleVersion),
                report.summary.platform.ToString(),
                DateTime.UtcNow.ToString("yyyyMMdd-HHmmss"));

            foreach (string sourceDirectory in Directory.GetDirectories(buildDir, "*", SearchOption.TopDirectoryOnly))
            {
                string name = Path.GetFileName(sourceDirectory);
                if (!MatchesSymbolSuffix(name)) continue;

                string destination = Path.Combine(archiveRoot, name);
                CopyDirectory(sourceDirectory, destination);
                (int sourceCount, long sourceBytes) = MeasureDirectory(sourceDirectory);
                (int destinationCount, long destinationBytes) = MeasureDirectory(destination);
                if (sourceCount != destinationCount || sourceBytes != destinationBytes)
                {
                    throw new BuildFailedException(
                        $"符号归档校验失败：{name}，source={sourceCount}/{sourceBytes}，archive={destinationCount}/{destinationBytes}");
                }

                Debug.Log($"[FullPackage] 符号已归档并保留原文件：{destination}");
            }
        }

        private static bool MatchesSymbolSuffix(string folderName)
        {
            foreach (string suffix in SymbolFolderSuffixes)
            {
                if (folderName.EndsWith(suffix, StringComparison.Ordinal)) return true;
            }
            return false;
        }

        private static void CopyDirectory(string source, string destination)
        {
            Directory.CreateDirectory(destination);
            foreach (string directory in Directory.GetDirectories(source, "*", SearchOption.AllDirectories))
            {
                string relative = directory.Substring(source.Length)
                    .TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                Directory.CreateDirectory(Path.Combine(destination, relative));
            }
            foreach (string file in Directory.GetFiles(source, "*", SearchOption.AllDirectories))
            {
                string relative = file.Substring(source.Length)
                    .TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                string target = Path.Combine(destination, relative);
                Directory.CreateDirectory(Path.GetDirectoryName(target));
                File.Copy(file, target, true);
            }
        }

        private static (int count, long bytes) MeasureDirectory(string directory)
        {
            int count = 0;
            long bytes = 0;
            foreach (string file in Directory.GetFiles(directory, "*", SearchOption.AllDirectories))
            {
                count++;
                bytes += new FileInfo(file).Length;
            }
            return (count, bytes);
        }

        private static string Sanitize(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return "unknown";
            foreach (char invalid in Path.GetInvalidFileNameChars()) value = value.Replace(invalid, '_');
            return value;
        }

        private static void SwitchBackProfileIfRequested(BuildReport report)
        {
            if (!EditorPrefs.GetBool(FullPackagePublisherWindow.AutoSwitchBackAfterBuildPrefsKey, false))
                return;

            EditorPrefs.DeleteKey(FullPackagePublisherWindow.AutoSwitchBackAfterBuildPrefsKey);
            if (report.summary.result != BuildResult.Succeeded)
            {
                Debug.LogWarning("[FullPackage] Build 未成功，跳过自动切回 HotUpdateRemote。");
                return;
            }

            AddressablesSetup.SwitchToHotUpdateRemote();
            Debug.Log("[FullPackage] Build 成功，已自动切回 HotUpdateRemote。");
        }
    }
}
