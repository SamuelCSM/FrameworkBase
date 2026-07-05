using System;
using System.Collections.Generic;
using UnityEngine;

namespace Framework.HotUpdate
{
    /// <summary>
    /// 版本管理器
    /// 负责版本对比、更新类型判断和兼容性检查
    /// </summary>
    public class VersionManager
    {
        /// <summary>代码热更默认（入口）补丁文件名——承载 HotfixEntry 的游戏逻辑程序集。</summary>
        public const string DefaultCodePatchFileName = "HotUpdate.dll.bytes";

        /// <summary>
        /// 可热更程序集的 bytes 文件名列表，按「依赖在前、被依赖方在后」的<b>加载顺序</b>排列。
        /// <para>
        /// 顺序约束：<c>Blokus.Core</c> 是双端同源的规则内核，<c>GameProtocol</c> 是项目协议目录，
        /// <c>HotUpdate</c>（业务逻辑层）通过 <c>HotUpdate.asmdef</c> 同时引用二者，因此被依赖的
        /// <c>Blokus.Core.dll.bytes</c> 与 <c>GameProtocol.dll.bytes</c> 必须先于 <c>HotUpdate.dll.bytes</c>
        /// 完成 <see cref="System.Reflection.Assembly.Load(byte[])"/>，否则解释域在加载 HotUpdate 时会找不到依赖。
        /// </para>
        /// <para>
        /// 该列表与 <c>ProjectSettings/HybridCLRSettings.asset</c> 的 <c>hotUpdateAssemblies</c> 同源
        /// （二者均为「Blokus.Core + GameProtocol + HotUpdate」这一组可热更程序集）；此处仅按依赖拓扑排定<b>加载次序</b>
        /// （依赖在前），不要求与设置文件中的书写顺序逐字一致。
        /// </para>
        /// <para>
        /// 同源修复约束：客户端侧 Blokus.Core 规则内核的 BUG 可经热更下发（作为 <c>Blokus.Core.dll.bytes</c>
        /// 补丁覆盖修复，无需整包）；但服务端的 Blokus.Core 是<b>原生编译</b>（GameServer 项目引用、随服务端 IL2CPP/JIT
        /// 原生构建），无法热更，必须「重新编译 + 重新部署 + 停服维护重启」。因此修复双端同源规则时，
        /// 客户端与服务端必须按版本卡死配对（version 锁定 + 匹配时校验协议/数据版本一致，见需求 14.6、24.6），
        /// 避免出现一端已修、另一端未修导致双端裁定分叉。
        /// </para>
        /// </summary>
        public static string[] HotUpdateAssemblyFileNames
        {
            get
            {
                // 配置优先：AppConfig.HotUpdateAssemblyFiles 非空时生效——新项目改配置表即可换程序集组，
                // Framework 不写死项目专属程序集名（复用地基时无需改框架代码）。留空回退本项目内置默认。
                var cfg = Core.AppConfig.Load();
                if (cfg != null && cfg.HotUpdateAssemblyFiles != null && cfg.HotUpdateAssemblyFiles.Length > 0)
                    return cfg.HotUpdateAssemblyFiles;

                return DefaultHotUpdateAssemblyFileNames;
            }
        }

        /// <summary>本项目内置默认程序集组（AppConfig 未配置时的回退，按依赖拓扑排序）。</summary>
        private static readonly string[] DefaultHotUpdateAssemblyFileNames =
        {
            "Blokus.Core.dll.bytes", // 依赖：规则内核（双端同源），须先加载
            "GameProtocol.dll.bytes", // 依赖：项目协议目录（DTO/消息枚举），须先于业务逻辑加载
            DefaultCodePatchFileName, // 被依赖方：业务逻辑 + HotfixEntry，后加载
        };

        /// <summary>承载热更入口 <c>HotUpdate.Entry.HotfixEntry</c> 的程序集名（即 HotUpdate）。</summary>
        public static readonly string EntryHotUpdateAssemblyName = ToAssemblyName(DefaultCodePatchFileName);

        /// <summary>
        /// 由热更 DLL 的 bytes 文件名推导程序集名（去除 <c>.dll.bytes</c> / <c>.bytes</c> / <c>.dll</c> 后缀）。
        /// 例：<c>Blokus.Core.dll.bytes</c> → <c>Blokus.Core</c>，<c>GameProtocol.dll.bytes</c> → <c>GameProtocol</c>。
        /// </summary>
        public static string ToAssemblyName(string bytesFileName)
        {
            if (string.IsNullOrEmpty(bytesFileName))
                return bytesFileName;

            string name = bytesFileName;
            if (name.EndsWith(".bytes", StringComparison.OrdinalIgnoreCase))
                name = name.Substring(0, name.Length - ".bytes".Length);
            if (name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
                name = name.Substring(0, name.Length - ".dll".Length);
            return name;
        }

        /// <summary>
        /// 比较两个版本号
        /// </summary>
        /// <param name="version1">版本1（如"1.0.0"）</param>
        /// <param name="version2">版本2（如"1.0.1"）</param>
        /// <returns>
        /// 返回值 > 0: version1 > version2
        /// 返回值 = 0: version1 = version2
        /// 返回值 < 0: version1 < version2
        /// </returns>
        public static int CompareVersion(string version1, string version2)
        {
            if (string.IsNullOrEmpty(version1) && string.IsNullOrEmpty(version2))
                return 0;
            
            if (string.IsNullOrEmpty(version1))
                return -1;
            
            if (string.IsNullOrEmpty(version2))
                return 1;
            
            try
            {
                string[] parts1 = version1.Split('.');
                string[] parts2 = version2.Split('.');
                
                int maxLength = Math.Max(parts1.Length, parts2.Length);
                
                for (int i = 0; i < maxLength; i++)
                {
                    int num1 = i < parts1.Length ? int.Parse(parts1[i]) : 0;
                    int num2 = i < parts2.Length ? int.Parse(parts2[i]) : 0;
                    
                    if (num1 != num2)
                    {
                        return num1.CompareTo(num2);
                    }
                }
                
                return 0;
            }
            catch (Exception ex)
            {
                Logger.Error($"[VersionManager] 版本比较失败: {version1} vs {version2}, 错误: {ex.Message}");
                return 0;
            }
        }
        
        /// <summary>
        /// 判断更新类型
        /// </summary>
        /// <param name="currentVersion">当前版本信息</param>
        /// <param name="targetVersion">目标版本信息</param>
        /// <returns>更新类型</returns>
        public static UpdateType DetermineUpdateType(UpdateInfo currentVersion, UpdateInfo targetVersion)
        {
            if (currentVersion == null || targetVersion == null)
            {
                Logger.Warning("[VersionManager] 版本信息为空，无法判断更新类型");
                return UpdateType.None;
            }
            
            // 比较应用版本号
            int appVersionCompare = CompareVersion(currentVersion.AppVersion, targetVersion.AppVersion);
            
            // 如果应用版本不同，需要整包更新
            if (appVersionCompare != 0)
            {
                Logger.Log($"[VersionManager] 应用版本不同，需要整包更新: {currentVersion.AppVersion} -> {targetVersion.AppVersion}");
                return UpdateType.FullUpdate;
            }
            
            // 应用版本相同，检查资源版本和代码版本
            bool resourceChanged = currentVersion.ResourceVersion != targetVersion.ResourceVersion;
            bool codeChanged = currentVersion.CodeVersion != targetVersion.CodeVersion;
            
            if (resourceChanged || codeChanged)
            {
                Logger.Log($"[VersionManager] 资源或代码版本不同，需要热更新: " +
                          $"资源版本 {currentVersion.ResourceVersion} -> {targetVersion.ResourceVersion}, " +
                          $"代码版本 {currentVersion.CodeVersion} -> {targetVersion.CodeVersion}");
                return UpdateType.HotUpdate;
            }
            
            Logger.Log("[VersionManager] 版本相同，无需更新");
            return UpdateType.None;
        }

        /// <summary>是否需要下载代码热更（以 CodeVersion 为准，不依赖 PatchFiles 是否为空）。</summary>
        public static bool ShouldUpdateCode(UpdateInfo serverVersion, UpdateInfo localVersion)
        {
            if (serverVersion == null || localVersion == null)
                return false;

            if (DetermineUpdateType(localVersion, serverVersion) == UpdateType.FullUpdate)
                return false;

            return serverVersion.CodeVersion != localVersion.CodeVersion;
        }

        /// <summary>
        /// 解析代码热更补丁列表：优先使用服务端 PatchFiles（可包含多个 DLL，逐文件带 Size/MD5 校验）；
        /// CodeVersion 已变更但列表为空时，按 UpdateServerUrl 约定路径补全<b>全部</b>可热更程序集 DLL
        /// （<see cref="HotUpdateAssemblyFileNames"/>，含 Blokus.Core.dll.bytes、GameProtocol.dll.bytes 与 HotUpdate.dll.bytes）。
        /// <para>
        /// 多文件支持要点：服务端清单（version.json 的 <c>PatchFiles</c>）可下发多个 DLL；下载侧会对每个
        /// 文件按其 Size 计权进度、按其 MD5 逐个校验（见 HotUpdateManager.DownloadPatchAsync）。约定补全分支
        /// 不带 MD5（留空表示跳过校验），仅用于服务端未提供清单时的兜底。
        /// </para>
        /// </summary>
        public static bool TryResolveCodePatchFiles(
            UpdateInfo serverVersion,
            string updateServerUrl,
            out List<PatchFile> patchFiles)
        {
            patchFiles = null;

            if (serverVersion?.PatchFiles != null && serverVersion.PatchFiles.Count > 0)
            {
                // 服务端清单可包含多个 DLL（如 Blokus.Core.dll.bytes + GameProtocol.dll.bytes + HotUpdate.dll.bytes），原样透传，
                // 由下载侧逐文件按 Size/MD5 校验。
                patchFiles = serverVersion.PatchFiles;
                return true;
            }

            if (string.IsNullOrEmpty(updateServerUrl))
            {
                Logger.Warning("[VersionManager] CodeVersion 已变更但 UpdateServerUrl 为空，无法补全补丁 URL");
                return false;
            }

            string baseUrl = updateServerUrl.TrimEnd('/');
            patchFiles = new List<PatchFile>(HotUpdateAssemblyFileNames.Length);
            foreach (string fileName in HotUpdateAssemblyFileNames)
            {
                patchFiles.Add(new PatchFile
                {
                    FileName = fileName,
                    Url      = $"{baseUrl}/{fileName}",
                    Size     = 0,
                    MD5      = string.Empty
                });
            }

            Logger.Log($"[VersionManager] PatchFiles 为空，已按约定补全 {patchFiles.Count} 个热更程序集: " +
                       $"{string.Join(", ", patchFiles.ConvertAll(p => p.FileName))}");
            return true;
        }
        
        /// <summary>
        /// 检查版本兼容性
        /// </summary>
        /// <param name="currentVersion">当前版本</param>
        /// <param name="minCompatibleVersion">最低兼容版本</param>
        /// <returns>是否兼容</returns>
        public static bool CheckCompatibility(string currentVersion, string minCompatibleVersion)
        {
            if (string.IsNullOrEmpty(minCompatibleVersion))
            {
                // 没有最低兼容版本限制，认为兼容
                return true;
            }
            
            int compareResult = CompareVersion(currentVersion, minCompatibleVersion);
            bool isCompatible = compareResult >= 0;
            
            if (!isCompatible)
            {
                Logger.Warning($"[VersionManager] 版本不兼容: 当前版本 {currentVersion} < 最低兼容版本 {minCompatibleVersion}");
            }
            else
            {
                Logger.Log($"[VersionManager] 版本兼容: 当前版本 {currentVersion} >= 最低兼容版本 {minCompatibleVersion}");
            }
            
            return isCompatible;
        }
        
        /// <summary>
        /// 获取当前本地版本信息
        /// </summary>
        /// <returns>本地版本信息</returns>
        public static UpdateInfo GetLocalVersion()
        {
            // 优先读取 persistentDataPath（热更后保存的最新版本）
            string persistentPath = System.IO.Path.Combine(Application.persistentDataPath, "version.json");
            if (System.IO.File.Exists(persistentPath))
            {
                try
                {
                    string json = System.IO.File.ReadAllText(persistentPath);
                    UpdateInfo versionInfo = JsonUtility.FromJson<UpdateInfo>(json);
                    if (versionInfo != null && !string.IsNullOrEmpty(versionInfo.AppVersion))
                    {
                        // 避免旧安装残留的 persistent/version.json 污染新整包版本判断。
                        // 若 AppVersion 与当前安装包不一致，忽略该缓存并回退读取 StreamingAssets 出厂版本。
                        if (!string.Equals(versionInfo.AppVersion, Application.version, StringComparison.Ordinal))
                        {
                            Logger.Warning($"[VersionManager] 忽略旧本地版本（persistent）：{versionInfo.AppVersion}，当前安装包版本：{Application.version}");
                        }
                        else
                        {
                            Logger.Log($"[VersionManager] 读取本地版本（persistent）: " +
                                       $"App={versionInfo.AppVersion} Resource={versionInfo.ResourceVersion} Code={versionInfo.CodeVersion}");
                            return versionInfo;
                        }
                    }
                }
                catch (Exception ex)
                {
                    Logger.Error($"[VersionManager] 读取本地版本文件失败: {ex.Message}");
                }
            }

            // 其次读取 StreamingAssets（打包时同步进去的出厂版本）
            string streamingPath = System.IO.Path.Combine(Application.streamingAssetsPath, "version.json");
            if (System.IO.File.Exists(streamingPath))
            {
                try
                {
                    string json = System.IO.File.ReadAllText(streamingPath);
                    UpdateInfo versionInfo = JsonUtility.FromJson<UpdateInfo>(json);
                    Logger.Log($"[VersionManager] 读取出厂版本（StreamingAssets）: " +
                               $"Resource={versionInfo.ResourceVersion} Code={versionInfo.CodeVersion}");
                    return versionInfo;
                }
                catch (Exception ex)
                {
                    Logger.Error($"[VersionManager] 读取 StreamingAssets 版本失败: {ex.Message}");
                }
            }

            // 最后兜底（理论上不应走到这里）
            Logger.Warning("[VersionManager] 未找到任何版本文件，使用硬编码默认值 Resource=1 Code=1");
            return new UpdateInfo
            {
                AppVersion           = Application.version,
                ResourceVersion      = 1,
                CodeVersion          = 1,
                ForceUpdate          = false,
                MinCompatibleVersion = Application.version,
                PatchFiles           = new System.Collections.Generic.List<PatchFile>(),
                Description          = "初始版本",
                Type                 = UpdateType.None
            };
        }
        
        /// <summary>
        /// 资源热更完成后写回本地 version.json。
        /// 仅提升 ResourceVersion；CodeVersion 留给代码补丁流程更新，避免未下载 DLL 却标记代码已更新。
        /// </summary>
        public static void CommitResourceUpdate(UpdateInfo serverVersion)
        {
            if (serverVersion == null)
            {
                Logger.Warning("[VersionManager] CommitResourceUpdate 跳过: serverVersion 为空");
                return;
            }

            UpdateInfo local = GetLocalVersion();
            local.ResourceVersion = serverVersion.ResourceVersion;

            if (!string.IsNullOrEmpty(serverVersion.AppVersion))
                local.AppVersion = serverVersion.AppVersion;

            if (!string.IsNullOrEmpty(serverVersion.MinCompatibleVersion))
                local.MinCompatibleVersion = serverVersion.MinCompatibleVersion;

            // 本地持久化文件不保留补丁列表，避免过期 URL 干扰下次启动判断。
            local.Type = UpdateType.None;
            if (local.PatchFiles == null)
                local.PatchFiles = new System.Collections.Generic.List<PatchFile>();
            else
                local.PatchFiles.Clear();

            SaveLocalVersion(local);
            Logger.Log($"[VersionManager] 资源热更版本已落盘: Resource={local.ResourceVersion}, Code={local.CodeVersion}");
        }

        /// <summary>
        /// 代码热更完成后写回本地 version.json（提升 CodeVersion，清空 PatchFiles）。
        /// </summary>
        public static void CommitCodeUpdate(UpdateInfo serverVersion)
        {
            if (serverVersion == null)
            {
                Logger.Warning("[VersionManager] CommitCodeUpdate 跳过: serverVersion 为空");
                return;
            }

            UpdateInfo local = GetLocalVersion();
            local.CodeVersion = serverVersion.CodeVersion;

            if (!string.IsNullOrEmpty(serverVersion.AppVersion))
                local.AppVersion = serverVersion.AppVersion;

            if (!string.IsNullOrEmpty(serverVersion.MinCompatibleVersion))
                local.MinCompatibleVersion = serverVersion.MinCompatibleVersion;

            local.Type = UpdateType.None;
            if (local.PatchFiles == null)
                local.PatchFiles = new List<PatchFile>();
            else
                local.PatchFiles.Clear();

            SaveLocalVersion(local);
            Logger.Log($"[VersionManager] 代码热更版本已落盘: Resource={local.ResourceVersion}, Code={local.CodeVersion}");
        }

        /// <summary>
        /// 保存版本信息到本地
        /// </summary>
        /// <param name="versionInfo">版本信息</param>
        public static void SaveLocalVersion(UpdateInfo versionInfo)
        {
            try
            {
                string versionFilePath = System.IO.Path.Combine(Application.persistentDataPath, "version.json");
                string json = JsonUtility.ToJson(versionInfo, true);
                System.IO.File.WriteAllText(versionFilePath, json);
                Logger.Log($"[VersionManager] 保存本地版本: {versionInfo.AppVersion}");
            }
            catch (Exception ex)
            {
                Logger.Error($"[VersionManager] 保存本地版本文件失败: {ex.Message}");
            }
        }
        
        /// <summary>
        /// 计算需要下载的补丁总大小
        /// </summary>
        /// <param name="patchFiles">补丁文件列表</param>
        /// <returns>总大小（字节）</returns>
        public static long CalculateTotalSize(IReadOnlyList<PatchFile> patchFiles)
        {
            if (patchFiles == null || patchFiles.Count == 0)
                return 0;
            
            long totalSize = 0;
            foreach (var file in patchFiles)
            {
                totalSize += file.Size;
            }
            
            return totalSize;
        }
        
        /// <summary>
        /// 格式化文件大小
        /// </summary>
        /// <param name="bytes">字节数</param>
        /// <returns>格式化后的字符串（如"1.5 MB"）</returns>
        public static string FormatFileSize(long bytes)
        {
            string[] sizes = { "B", "KB", "MB", "GB", "TB" };
            double len = bytes;
            int order = 0;
            
            while (len >= 1024 && order < sizes.Length - 1)
            {
                order++;
                len = len / 1024;
            }
            
            return $"{len:0.##} {sizes[order]}";
        }
    }
}
