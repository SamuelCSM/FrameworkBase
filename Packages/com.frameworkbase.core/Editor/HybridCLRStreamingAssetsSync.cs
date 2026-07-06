using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using Framework.HotUpdate;
using HybridCLR.Editor;
using HybridCLR.Editor.Commands;
using UnityEditor;
using UnityEngine;

namespace Framework.Editor
{
    /// <summary>
    /// 将 HybridCLR 裁剪后的 AOT DLL 同步到 StreamingAssets，供真机启动时加载补充元数据。
    /// </summary>
    public static class HybridCLRStreamingAssetsSync
    {
        [MenuItem("Framework/HybridCLR/Sync AOT Metadata -> StreamingAssets")]
        public static void SyncFromMenu()
        {
            try
            {
                EnsureGeneratedMetadataForBuild();
                SyncMetadataToStreamingAssets();
                EditorUtility.DisplayDialog("同步完成",
                    "已同步到 StreamingAssets/HybridCLRMetadata。\n请与 HotUpdate.dll.bytes 一并打入 Player。",
                    "OK");
            }
            catch (Exception ex)
            {
                EditorUtility.DisplayDialog("同步失败", ex.Message, "OK");
            }
        }

        /// <summary>
        /// 检查当前构建目标的 HybridCLR AOT 裁剪产物，缺失时自动执行 Generate/All。
        /// </summary>
        /// <param name="target">需要检查的构建目标；为空时使用当前 ActiveBuildTarget。</param>
        public static void EnsureGeneratedMetadataForBuild(BuildTarget? target = null)
        {
            BuildTarget buildTarget = target ?? EditorUserBuildSettings.activeBuildTarget;
            List<string> missing = GetMissingGeneratedMetadataFiles(buildTarget);
            if (missing.Count == 0)
                return;

            if (buildTarget != EditorUserBuildSettings.activeBuildTarget)
            {
                throw new InvalidOperationException(
                    $"HybridCLR Generate/All 只能生成当前 ActiveBuildTarget 的产物。当前={EditorUserBuildSettings.activeBuildTarget}, 需要={buildTarget}");
            }

            Il2CppToolchainValidator.ValidateForBuildTarget(buildTarget);

            Debug.LogWarning(
                "[HybridCLRStreamingAssetsSync] AOT 元数据生成产物缺失，将自动执行 HybridCLR/Generate/All：\n" +
                string.Join("\n", missing));

            PrebuildCommand.GenerateAll();

            missing = GetMissingGeneratedMetadataFiles(buildTarget);
            if (missing.Count > 0)
            {
                throw new InvalidOperationException(
                    "HybridCLR/Generate/All 执行后仍缺少 AOT 元数据生成产物：\n" +
                    string.Join("\n", missing));
            }
        }

        /// <summary>
        /// 获取指定构建目标缺失的 AOT 裁剪 DLL 源文件列表。
        /// </summary>
        /// <param name="target">需要检查的构建目标；为空时使用当前 ActiveBuildTarget。</param>
        /// <returns>缺失文件或目录的绝对路径列表。</returns>
        public static List<string> GetMissingGeneratedMetadataFiles(BuildTarget? target = null)
        {
            BuildTarget buildTarget = target ?? EditorUserBuildSettings.activeBuildTarget;
            string srcDir = SettingsUtil.GetAssembliesPostIl2CppStripDir(buildTarget);
            var missing = new List<string>();

            if (!Directory.Exists(srcDir))
            {
                missing.Add(srcDir);
                return missing;
            }

            foreach (string assemblyName in ResolvePatchedAotAssemblies())
            {
                string srcFile = Path.Combine(srcDir, assemblyName);
                if (!File.Exists(srcFile))
                    missing.Add(srcFile);
            }

            return missing;
        }

        /// <summary>
        /// 获取整包发布必须打入 StreamingAssets 的 HybridCLR AOT 元数据文件。
        /// </summary>
        /// <returns>StreamingAssets 内必需元数据文件的绝对路径列表。</returns>
        public static List<string> GetRequiredStreamingAssetsMetadataPaths()
        {
            string destDir = Path.Combine(
                Application.dataPath,
                "StreamingAssets",
                HybridCLRMetadataManifest.StreamingAssetsFolder);

            var required = new List<string>
            {
                Path.Combine(destDir, HybridCLRMetadataManifest.ManifestFileName)
            };

            foreach (string assemblyName in ResolvePatchedAotAssemblies())
                required.Add(Path.Combine(destDir, assemblyName + ".bytes"));

            return required;
        }

        /// <summary>
        /// 同步 AOT 元数据到 StreamingAssets/HybridCLRMetadata。
        /// 需先执行 HybridCLR/Generate/AOTDlls 或 Generate/All。
        /// </summary>
        public static void SyncMetadataToStreamingAssets(BuildTarget? target = null, bool refreshAssetDatabase = true)
        {
            BuildTarget buildTarget = target ?? EditorUserBuildSettings.activeBuildTarget;
            string srcDir = SettingsUtil.GetAssembliesPostIl2CppStripDir(buildTarget);

            if (!Directory.Exists(srcDir))
            {
                throw new DirectoryNotFoundException(
                    $"未找到 AOT 裁剪输出目录：{srcDir}\n请先执行 HybridCLR/Generate/AOTDlls 或 Generate/All。");
            }

            string destDir = Path.Combine(Application.dataPath, "StreamingAssets", HybridCLRMetadataManifest.StreamingAssetsFolder);
            Directory.CreateDirectory(destDir);

            var copied = new System.Collections.Generic.List<string>();
            var missing = new System.Collections.Generic.List<string>();
            foreach (string assemblyName in ResolvePatchedAotAssemblies())
            {
                string srcFile = Path.Combine(srcDir, assemblyName);
                if (!File.Exists(srcFile))
                {
                    missing.Add(srcFile);
                    continue;
                }

                string destFile = Path.Combine(destDir, assemblyName + ".bytes");
                File.Copy(srcFile, destFile, overwrite: true);
                copied.Add(assemblyName);
                Debug.Log($"[HybridCLRStreamingAssetsSync] {assemblyName} -> {destFile}");
            }

            if (missing.Count > 0)
            {
                throw new InvalidOperationException(
                    "缺少发布必需的 AOT 元数据源文件：\n" +
                    string.Join("\n", missing));
            }

            if (copied.Count == 0)
            {
                throw new InvalidOperationException(
                    "未复制任何 AOT 元数据文件，请检查 AOTGenericReferences.PatchedAOTAssemblyList 与 Generate/AOTDlls 输出。");
            }

            WriteManifest(destDir, copied.ToArray());

            if (refreshAssetDatabase)
                AssetDatabase.Refresh();
        }

        /// <summary>
        /// 解析需补充元数据的 AOT 程序集清单——以 HybridCLR 生成的
        /// <c>Assets/HybridCLRGenerate/AOTGenericReferences.cs</c> 的 <c>PatchedAOTAssemblyList</c> 为<b>单一数据源</b>，
        /// 消除其与手维护清单之间的漂移（漂移曾导致 manifest 漏/多列程序集）。
        /// </summary>
        /// <remarks>
        /// 刻意<b>解析源文件</b>而非读已编译的 <c>AOTGenericReferences.PatchedAOTAssemblyList</c> 静态字段：
        /// 发布流程会先 <c>Generate/All</c> 再同步，但编译域在同一次调用内不会即时重载，读编译字段会拿到旧值；
        /// 解析磁盘源文件可反映刚生成的最新内容。文件缺失/解析为空时回退到
        /// <see cref="HybridCLRMetadataManifest.PatchedAotAssemblies"/>（运行时同款兜底清单）。
        /// </remarks>
        private static string[] ResolvePatchedAotAssemblies()
        {
            string aotRefPath = Path.Combine(Application.dataPath, "HybridCLRGenerate", "AOTGenericReferences.cs");
            if (File.Exists(aotRefPath))
            {
                try
                {
                    string[] parsed = ParsePatchedAotAssemblyList(File.ReadAllText(aotRefPath));
                    if (parsed.Length > 0)
                        return parsed;
                    Debug.LogWarning("[HybridCLRStreamingAssetsSync] AOTGenericReferences.PatchedAOTAssemblyList 解析为空，回退手维护兜底清单");
                }
                catch (Exception ex)
                {
                    Debug.LogWarning($"[HybridCLRStreamingAssetsSync] 解析 AOTGenericReferences 失败，回退手维护兜底清单：{ex.Message}");
                }
            }
            else
            {
                Debug.LogWarning($"[HybridCLRStreamingAssetsSync] 未找到 {aotRefPath}，回退手维护兜底清单（请先执行 HybridCLR/Generate/All）");
            }

            return HybridCLRMetadataManifest.PatchedAotAssemblies;
        }

        /// <summary>
        /// 从 AOTGenericReferences.cs 源码中抽取 <c>PatchedAOTAssemblyList = new List&lt;string&gt; { "a.dll", ... }</c>
        /// 块内的带引号程序集名。
        /// </summary>
        /// <param name="source">AOTGenericReferences.cs 全文。</param>
        /// <returns>程序集名数组（含 .dll 后缀）；未匹配到时为空数组。</returns>
        private static string[] ParsePatchedAotAssemblyList(string source)
        {
            int keyIndex = source.IndexOf("PatchedAOTAssemblyList", StringComparison.Ordinal);
            if (keyIndex < 0)
                return Array.Empty<string>();

            int braceIndex = source.IndexOf('{', keyIndex);
            int endIndex = braceIndex >= 0 ? source.IndexOf("};", braceIndex, StringComparison.Ordinal) : -1;
            if (braceIndex < 0 || endIndex < 0)
                return Array.Empty<string>();

            string block = source.Substring(braceIndex + 1, endIndex - braceIndex - 1);
            var result = new List<string>();
            foreach (Match m in Regex.Matches(block, "\"([^\"]+)\""))
                result.Add(m.Groups[1].Value);

            return result.ToArray();
        }

        private static void WriteManifest(string destDir, string[] assemblies)
        {
            var data = new HybridCLRMetadataManifestData { assemblies = assemblies };
            string json = JsonUtility.ToJson(data, true);
            string manifestPath = Path.Combine(destDir, HybridCLRMetadataManifest.ManifestFileName);
            File.WriteAllText(manifestPath, json, Encoding.UTF8);
            Debug.Log($"[HybridCLRStreamingAssetsSync] manifest -> {manifestPath}");
        }
    }
}
