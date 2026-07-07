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
        /// 顺序约束：被依赖的程序集（如协议目录 <c>GameProtocol</c>、项目自有的规则内核等）必须先于
        /// 业务逻辑层 <c>HotUpdate.dll.bytes</c> 完成 <see cref="System.Reflection.Assembly.Load(byte[])"/>，
        /// 否则解释域在加载 HotUpdate 时会找不到依赖。
        /// </para>
        /// <para>
        /// 该列表与 <c>ProjectSettings/HybridCLRSettings.asset</c> 的 <c>hotUpdateAssemblies</c> 同源；
        /// 此处仅按依赖拓扑排定<b>加载次序</b>，不要求与设置文件中的书写顺序逐字一致。
        /// 业务项目若有额外可热更程序集（如双端同源规则内核），通过 AppConfig 的
        /// <c>HotUpdateAssemblyFiles</c> 配置完整清单，无需改框架代码。
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

        /// <summary>基础工程内置默认程序集组（AppConfig 未配置时的回退，按依赖拓扑排序）。</summary>
        private static readonly string[] DefaultHotUpdateAssemblyFileNames =
        {
            "GameProtocol.dll.bytes", // 依赖：项目协议目录（DTO/消息枚举），须先于业务逻辑加载
            DefaultCodePatchFileName, // 被依赖方：业务逻辑 + HotfixEntry，后加载
        };

        /// <summary>承载热更入口 <c>HotUpdate.Entry.HotfixEntry</c> 的程序集名（即 HotUpdate）。</summary>
        public static readonly string EntryHotUpdateAssemblyName = ToAssemblyName(DefaultCodePatchFileName);

        /// <summary>
        /// 由热更 DLL 的 bytes 文件名推导程序集名（去除 <c>.dll.bytes</c> / <c>.bytes</c> / <c>.dll</c> 后缀）。
        /// 例：<c>GameProtocol.dll.bytes</c> → <c>GameProtocol</c>。
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
                GameLog.Error($"[VersionManager] 版本比较失败: {version1} vs {version2}, 错误: {ex.Message}");
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
                GameLog.Warning("[VersionManager] 版本信息为空，无法判断更新类型");
                return UpdateType.None;
            }
            
            // 比较应用版本号
            int appVersionCompare = CompareVersion(currentVersion.AppVersion, targetVersion.AppVersion);
            
            // 如果应用版本不同，需要整包更新
            if (appVersionCompare != 0)
            {
                GameLog.Log($"[VersionManager] 应用版本不同，需要整包更新: {currentVersion.AppVersion} -> {targetVersion.AppVersion}");
                return UpdateType.FullUpdate;
            }
            
            // 应用版本相同，检查资源版本和代码版本
            bool resourceChanged = currentVersion.ResourceVersion != targetVersion.ResourceVersion;
            bool codeChanged = currentVersion.CodeVersion != targetVersion.CodeVersion;
            
            if (resourceChanged || codeChanged)
            {
                GameLog.Log($"[VersionManager] 资源或代码版本不同，需要热更新: " +
                          $"资源版本 {currentVersion.ResourceVersion} -> {targetVersion.ResourceVersion}, " +
                          $"代码版本 {currentVersion.CodeVersion} -> {targetVersion.CodeVersion}");
                return UpdateType.HotUpdate;
            }
            
            GameLog.Log("[VersionManager] 版本相同，无需更新");
            return UpdateType.None;
        }

        /// <summary>
        /// 灰度放量判定：服务端 version.json 携带 GrayPercent（1~99）时，仅命中分桶的设备
        /// 应用本次更新，其余设备视为无更新（服务端上调百分比后自动纳入）。
        /// 分桶盐含目标版本号：每次新发布重新洗牌，避免同一批设备永远当小白鼠；
        /// 同一发布内放量从 5% 上调到 50% 时，前 5% 的设备保持命中（桶号 &lt; 百分比单调扩大）。
        /// </summary>
        public static bool IsDeviceInGrayRollout(UpdateInfo serverVersion, string deviceId)
        {
            if (serverVersion == null)
                return true;

            int percent = serverVersion.GrayPercent;
            if (percent <= 0 || percent >= 100)
                return true;

            string salt = $"{deviceId}:{serverVersion.AppVersion}:{serverVersion.ResourceVersion}:{serverVersion.CodeVersion}:gray";
            return StableHash.Bucket(salt) < percent;
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
        /// 解析代码热更补丁列表：只接受服务端清单 PatchFiles（可包含多个 DLL），且逐项必须携带
        /// FileName / Url / 非空 MD5（<see cref="UpdateSecurity.ValidateCodePatchFile"/>）。
        /// <para>
        /// 安全约束：代码补丁（DLL）是远程代码执行通道，其完整性锚点是（已验签的）清单里的 MD5。
        /// 因此 CodeVersion 已变更但清单为空 / 补丁缺 MD5 时<b>拒绝更新</b>，不再按约定 URL 补全
        /// 无校验补丁——发布工具（HotUpdatePublisher）生成的 version.json 天然带全量哈希，
        /// 手写清单必须补齐 PatchFiles 才能下发代码热更。
        /// </para>
        /// </summary>
        public static bool TryResolveCodePatchFiles(
            UpdateInfo serverVersion,
            string updateServerUrl,
            out List<PatchFile> patchFiles)
        {
            patchFiles = null;

            if (serverVersion?.PatchFiles == null || serverVersion.PatchFiles.Count == 0)
            {
                GameLog.Error("[VersionManager] CodeVersion 已变更但服务端清单未提供 PatchFiles，" +
                              "拒绝代码热更（禁止按约定 URL 下发无校验 DLL，请用发布工具生成带 MD5 的 version.json）");
                return false;
            }

            foreach (PatchFile patch in serverVersion.PatchFiles)
            {
                if (!UpdateSecurity.ValidateCodePatchFile(patch, out string reason))
                {
                    GameLog.Error($"[VersionManager] 补丁清单未通过安全准入，拒绝代码热更: {reason}");
                    return false;
                }
            }

            patchFiles = serverVersion.PatchFiles;
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
                GameLog.Warning($"[VersionManager] 版本不兼容: 当前版本 {currentVersion} < 最低兼容版本 {minCompatibleVersion}");
            }
            else
            {
                GameLog.Log($"[VersionManager] 版本兼容: 当前版本 {currentVersion} >= 最低兼容版本 {minCompatibleVersion}");
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
                            GameLog.Warning($"[VersionManager] 忽略旧本地版本（persistent）：{versionInfo.AppVersion}，当前安装包版本：{Application.version}");
                        }
                        else
                        {
                            GameLog.Log($"[VersionManager] 读取本地版本（persistent）: " +
                                       $"App={versionInfo.AppVersion} Resource={versionInfo.ResourceVersion} Code={versionInfo.CodeVersion}");
                            return versionInfo;
                        }
                    }
                }
                catch (Exception ex)
                {
                    GameLog.Error($"[VersionManager] 读取本地版本文件失败: {ex.Message}");
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
                    GameLog.Log($"[VersionManager] 读取出厂版本（StreamingAssets）: " +
                               $"Resource={versionInfo.ResourceVersion} Code={versionInfo.CodeVersion}");
                    return versionInfo;
                }
                catch (Exception ex)
                {
                    GameLog.Error($"[VersionManager] 读取 StreamingAssets 版本失败: {ex.Message}");
                }
            }

            // 最后兜底（理论上不应走到这里）
            GameLog.Warning("[VersionManager] 未找到任何版本文件，使用硬编码默认值 Resource=1 Code=1");
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
                GameLog.Warning("[VersionManager] CommitResourceUpdate 跳过: serverVersion 为空");
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
            GameLog.Log($"[VersionManager] 资源热更版本已落盘: Resource={local.ResourceVersion}, Code={local.CodeVersion}");
        }

        /// <summary>
        /// 代码热更完成后写回本地 version.json（提升 CodeVersion，清空 PatchFiles）。
        /// </summary>
        public static void CommitCodeUpdate(UpdateInfo serverVersion)
        {
            if (serverVersion == null)
            {
                GameLog.Warning("[VersionManager] CommitCodeUpdate 跳过: serverVersion 为空");
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
            GameLog.Log($"[VersionManager] 代码热更版本已落盘: Resource={local.ResourceVersion}, Code={local.CodeVersion}");
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
                GameLog.Log($"[VersionManager] 保存本地版本: {versionInfo.AppVersion}");
            }
            catch (Exception ex)
            {
                GameLog.Error($"[VersionManager] 保存本地版本文件失败: {ex.Message}");
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
