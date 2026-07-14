using System;
using System.Collections.Generic;
using System.IO;
using Framework.Serialization;
using Framework.Storage;
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

        /// <summary>热更入口类型全名默认值（AppConfig.HotUpdateEntryTypeFullName 未配置时回退）。</summary>
        public const string DefaultHotUpdateEntryTypeFullName = "HotUpdate.Entry.HotfixEntry";

        /// <summary>
        /// 承载入口类型的热更入口程序集名。
        /// <para>
        /// 配置优先：<c>AppConfig.HotUpdateEntryAssembly</c> 非空时生效；留空回退框架默认（由
        /// <see cref="DefaultCodePatchFileName"/> 推导，即 <c>HotUpdate</c>）。与 <see cref="HotUpdateAssemblyFileNames"/>
        /// 同为配置驱动，框架不写死项目专属入口名——复用地基时改配置即可换入口程序集。
        /// 入口程序集必须是 <see cref="HotUpdateAssemblyFileNames"/> 的成员，否则加载后反射不到入口类型。
        /// </para>
        /// </summary>
        public static string EntryHotUpdateAssemblyName
        {
            get
            {
                var cfg = Core.AppConfig.Load();
                if (cfg != null && !string.IsNullOrWhiteSpace(cfg.HotUpdateEntryAssembly))
                    return cfg.HotUpdateEntryAssembly.Trim();
                return ToAssemblyName(DefaultCodePatchFileName);
            }
        }

        /// <summary>
        /// 热更入口类型全名（含命名空间，须含无参 <c>Start</c> 方法，由 <c>HotUpdateManager.StartHotfix</c> 反射调用）。
        /// 配置优先：<c>AppConfig.HotUpdateEntryTypeFullName</c> 非空时生效；留空回退
        /// <see cref="DefaultHotUpdateEntryTypeFullName"/>。
        /// </summary>
        public static string HotUpdateEntryTypeFullName
        {
            get
            {
                var cfg = Core.AppConfig.Load();
                if (cfg != null && !string.IsNullOrWhiteSpace(cfg.HotUpdateEntryTypeFullName))
                    return cfg.HotUpdateEntryTypeFullName.Trim();
                return DefaultHotUpdateEntryTypeFullName;
            }
        }

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

        /// <summary>仅当文件名属于配置中声明的不可变热更新程序集载荷白名单时返回 true。</summary>
        public static bool IsAllowedHotUpdateAssemblyFile(string fileName)
        {
            if (!UpdateSecurity.IsSafeLeafFileName(fileName))
                return false;

            foreach (string configured in HotUpdateAssemblyFileNames)
            {
                if (string.Equals(configured, fileName, StringComparison.Ordinal))
                    return true;
            }
            return false;
        }

        /// <summary>
        /// 尝试比较两个仅由非负整数和点分隔符组成的版本号。
        /// <para>
        /// 该方法不接受空段、负数、前后空白、预发布标签或整数溢出。安全准入必须调用本方法并在解析失败时拒绝清单，
        /// 不能把格式错误当作“版本相同”，否则攻击者可能利用异常回退绕过整包边界或防降级判断。
        /// </para>
        /// </summary>
        /// <param name="version1">左侧版本号，例如 1.0.0。</param>
        /// <param name="version2">右侧版本号，例如 1.0.1。</param>
        /// <param name="comparison">解析成功时返回比较结果：大于 0、等于 0 或小于 0。</param>
        /// <returns>两个版本号均满足格式约束时返回 true。</returns>
        public static bool TryCompareVersion(string version1, string version2, out int comparison)
        {
            comparison = 0;
            if (!TryParseVersionParts(version1, out int[] left) ||
                !TryParseVersionParts(version2, out int[] right))
            {
                return false;
            }

            int maxLength = Math.Max(left.Length, right.Length);
            for (int i = 0; i < maxLength; i++)
            {
                int leftPart = i < left.Length ? left[i] : 0;
                int rightPart = i < right.Length ? right[i] : 0;
                if (leftPart == rightPart) continue;
                comparison = leftPart.CompareTo(rightPart);
                return true;
            }
            return true;
        }

        /// <summary>
        /// 比较两个数字点分版本号。该兼容入口在格式错误时记录错误并返回 0；
        /// 安全、发布和构建门禁代码必须改用 <see cref="TryCompareVersion"/> 以实现失败关闭。
        /// </summary>
        public static int CompareVersion(string version1, string version2)
        {
            if (TryCompareVersion(version1, version2, out int comparison))
                return comparison;

            GameLog.Error($"[VersionManager] 版本格式无效，无法可靠比较：{version1} vs {version2}");
            return 0;
        }

        /// <summary>
        /// 将数字点分版本解析为整数段；最多允许 8 段，避免异常输入造成不必要分配和比较开销。
        /// </summary>
        private static bool TryParseVersionParts(string version, out int[] parts)
        {
            parts = null;
            if (string.IsNullOrEmpty(version) || !string.Equals(version, version.Trim(), StringComparison.Ordinal))
                return false;

            string[] tokens = version.Split('.');
            if (tokens.Length == 0 || tokens.Length > 8)
                return false;

            parts = new int[tokens.Length];
            for (int i = 0; i < tokens.Length; i++)
            {
                if (tokens[i].Length == 0 || !int.TryParse(tokens[i], out int value) || value < 0)
                {
                    parts = null;
                    return false;
                }
                parts[i] = value;
            }
            return true;
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

            return serverVersion.CodeVersion > localVersion.CodeVersion;
        }

        /// <summary>
        /// 解析代码热更补丁列表：只接受已验签清单中的完整程序集快照，且逐项必须携带
        /// FileName / Url / Size / SHA-256（<see cref="UpdateSecurity.ValidateCompleteCodePatchSet"/>）。
        /// <para>
        /// 安全约束：代码补丁（DLL）是远程代码执行通道，其完整性锚点是已验签清单中的 Size + SHA-256。
        /// 因此 CodeVersion 已变更但清单为空、文件集不完整或补丁缺 SHA-256 时<b>拒绝更新</b>，不再按约定 URL 补全
        /// 无校验补丁；发布工具生成的 version.json 必须包含完整程序集快照，
        /// 手写清单必须补齐 PatchFiles 才能下发代码热更。
        /// </para>
        /// <para>
        /// 下载地址收口：已签名清单 URL 必须位于主更新根下。准入后运行时提取不可变相对路径，
        /// 再映射到包内可信 CDN 列表；Host 不再参与内容身份，也不能由未签名配置任意扩展。
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
                              "拒绝代码热更（禁止按约定 URL 下发无校验 DLL，请使用发布工具生成完整 SHA-256 清单）。");
                return false;
            }

            string appEnv = Core.AppConfig.Load()?.AppEnv;
            if (!UpdateSecurity.ValidateCompleteCodePatchSet(serverVersion.PatchFiles, appEnv, out string reason))
            {
                GameLog.Error($"[VersionManager] 代码补丁快照未通过安全准入，拒绝代码热更：{reason}");
                return false;
            }

            var resolved = new List<PatchFile>(serverVersion.PatchFiles.Count);
            foreach (PatchFile patch in serverVersion.PatchFiles)
            {
                resolved.Add(new PatchFile
                {
                    FileName = patch.FileName,
                    Url = ResolveTrustedPatchUrl(updateServerUrl, patch.Url),
                    Size = patch.Size,
                    SHA256 = patch.SHA256,
                    MD5 = patch.MD5
                });
            }

            patchFiles = resolved;
            return true;
        }

        /// <summary>
        /// 解析已签名清单中的不可变补丁 URL，并强制其与 version.json 更新根同源且位于同一路径根下。
        /// <para>
        /// 不能再按 FileName 拼接稳定地址，否则发布新 DLL 时会覆盖旧清单仍在引用的对象，造成部署窗口内哈希不一致。
        /// </para>
        /// </summary>
        private static string ResolveTrustedPatchUrl(string updateServerUrl, string manifestUrl)
        {
            if (!Uri.TryCreate(manifestUrl, UriKind.Absolute, out Uri patchUri))
            {
                if (string.IsNullOrWhiteSpace(updateServerUrl) ||
                    !Uri.TryCreate(updateServerUrl.TrimEnd('/') + "/" + manifestUrl.TrimStart('/'), UriKind.Absolute, out patchUri))
                {
                    throw new InvalidDataException($"补丁 URL 无法解析：{manifestUrl}");
                }
            }

            if (string.IsNullOrWhiteSpace(updateServerUrl))
                return patchUri.AbsoluteUri;
            if (!Uri.TryCreate(updateServerUrl, UriKind.Absolute, out Uri baseUri))
                throw new InvalidDataException($"更新服务根 URL 无法解析：{updateServerUrl}");

            bool sameOrigin = string.Equals(baseUri.Scheme, patchUri.Scheme, StringComparison.OrdinalIgnoreCase) &&
                              string.Equals(baseUri.Host, patchUri.Host, StringComparison.OrdinalIgnoreCase) &&
                              baseUri.Port == patchUri.Port;
            string basePath = baseUri.AbsolutePath.TrimEnd('/') + "/";
            bool underBasePath = patchUri.AbsolutePath.StartsWith(basePath, StringComparison.Ordinal);
            if (!sameOrigin || !underBasePath)
                throw new InvalidDataException($"补丁 URL 不属于受信任更新根：{patchUri}");
            return patchUri.AbsoluteUri;
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
            UpdateInfo packaged = ReadPackagedVersion() ?? new UpdateInfo
            {
                AppVersion = Application.version,
                ResourceVersion = 1,
                CodeVersion = 1,
                MinCompatibleVersion = Application.version,
                PatchFiles = new List<PatchFile>(),
                Type = UpdateType.None,
            };

            packaged.AppVersion = Application.version;
            packaged.PatchFiles ??= new List<PatchFile>();
            packaged.PatchFiles.Clear();
            packaged.Type = UpdateType.None;

            string persistentPath = System.IO.Path.Combine(Application.persistentDataPath, "version.json");
            if (FileStorages.Shared.FileExists(persistentPath))
            {
                try
                {
                    UpdateInfo persisted = JsonSerializers.Shared.FromJson<UpdateInfo>(
                        FileStorages.Shared.ReadText(persistentPath));
                    if (persisted != null &&
                        string.Equals(persisted.AppVersion, Application.version, StringComparison.Ordinal))
                    {
                        // 资源状态可以独立于整包 Catalog 持续演进；代码状态绝不能来自该可变文件，已验证的 ActiveSlot 才是代码版本唯一事实源。
                        if (persisted.ResourceVersion >= packaged.ResourceVersion)
                            packaged.ResourceVersion = persisted.ResourceVersion;
                        if (!string.IsNullOrEmpty(persisted.MinCompatibleVersion))
                            packaged.MinCompatibleVersion = persisted.MinCompatibleVersion;
                    }
                }
                catch (Exception ex)
                {
                    GameLog.Error($"[VersionManager] 读取 persistent 版本文件失败：{ex.Message}");
                }
            }

            if (HotUpdateSlotManager.TryGetActiveCodeVersion(out int activeCodeVersion))
                packaged.CodeVersion = activeCodeVersion;

            GameLog.Log($"[VersionManager] 本地有效版本 App={packaged.AppVersion} Resource={packaged.ResourceVersion} Code={packaged.CodeVersion}");
            return packaged;
        }

        private static UpdateInfo ReadPackagedVersion()
        {
            string streamingPath = System.IO.Path.Combine(Application.streamingAssetsPath, "version.json");
            if (!FileStorages.Shared.FileExists(streamingPath))
                return null;

            try
            {
                return JsonSerializers.Shared.FromJson<UpdateInfo>(FileStorages.Shared.ReadText(streamingPath));
            }
            catch (Exception ex)
            {
                GameLog.Error($"[VersionManager] 读取出厂版本文件失败：{ex.Message}");
                return null;
            }
        }
        
        /// <summary>
        /// 资源热更完成后写回本地 version.json。
        /// 仅提升 ResourceVersion；CodeVersion 留给代码补丁流程更新，避免未下载 DLL 却标记代码已更新。
        /// </summary>
        public static void CommitResourceUpdate(UpdateInfo serverVersion)
        {
            CommitHotUpdate(serverVersion, resourceUpdated: true, codeUpdated: false);
        }

        /// <summary>
        /// 代码热更完成后写回本地 version.json（提升 CodeVersion，清空 PatchFiles）。
        /// </summary>
        public static void CommitCodeUpdate(UpdateInfo serverVersion)
        {
            CommitHotUpdate(serverVersion, resourceUpdated: false, codeUpdated: true);
        }

        /// <summary>
        /// 仅在统一启动确认点之后提交资源与代码版本，避免把未验证安装提前标记为已生效。
        /// <para>
        /// 调用契约（内容发行事务）：本方法只能在 LaunchFlow 统一确认点（HotfixEntry.Start 成功、
        /// 代码槽 ConfirmPendingSlot、内容事务 ConfirmPending、配置 ConfirmHotUpdateDatabase 之后）调用。
        /// 事实源约束：CodeVersion 只取自已验证活动槽的槽清单（TryGetActiveCodeVersion），
        /// 不信任清单声明；resourceUpdated 只能由本次启动实际完成的资源更新链路给出，
        /// 禁止仅凭"服务端版本更高"或"执行过更新流程"提交。启动确认前的任何失败都会经
        /// AbortPendingContent / 下次启动 PrepareForLaunch 回滚，绝不会走到本方法。
        /// </para>
        /// </summary>
        public static void CommitHotUpdate(UpdateInfo serverVersion, bool resourceUpdated, bool codeUpdated)
        {
            if (serverVersion == null || (!resourceUpdated && !codeUpdated))
                return;
            if (!string.Equals(serverVersion.AppVersion, Application.version, StringComparison.Ordinal))
            {
                GameLog.Error($"[VersionManager] 拒绝为未安装的整包版本提交内容状态：{serverVersion.AppVersion}");
                return;
            }

            UpdateInfo local = GetLocalVersion();
            local.AppVersion = Application.version;
            if (resourceUpdated)
                local.ResourceVersion = serverVersion.ResourceVersion;
            if (codeUpdated && HotUpdateSlotManager.TryGetActiveCodeVersion(out int activeCodeVersion))
                local.CodeVersion = activeCodeVersion;
            if (!string.IsNullOrEmpty(serverVersion.MinCompatibleVersion))
                local.MinCompatibleVersion = serverVersion.MinCompatibleVersion;
            local.Type = UpdateType.None;
            local.PatchFiles = new List<PatchFile>();
            SaveLocalVersion(local);
            GameLog.Log($"[VersionManager] 内容版本提交完成 Resource={local.ResourceVersion} Code={local.CodeVersion}");
        }

        /// <summary>
        /// 崩溃恢复前滚补写：确认阶段被中断（内容发行事务 CommitInProgress=true）时，下次启动依据已验签
        /// 发行记录重建 version.json，无需重新联网。语义等价于用当时的 serverVersion 调用
        /// <see cref="CommitHotUpdate"/>——CodeVersion 仍取自已验证活动槽（事实源），
        /// ResourceVersion / MinCompatibleVersion 取自发行记录。幂等：重复调用用同一结果覆盖写。
        /// </summary>
        /// <param name="record">被中断提交的发行记录（来自 ContentReleaseTransaction 的提交日志）。</param>
        public static void CommitHotUpdateFromRecord(ContentReleaseRecord record)
        {
            if (record == null || record.IsEmpty)
                return;

            var reconstructed = new UpdateInfo
            {
                AppVersion = record.AppVersion,
                ResourceVersion = record.ResourceVersion,
                MinCompatibleVersion = record.MinCompatibleVersion,
            };
            CommitHotUpdate(reconstructed, record.ResourceChanged, record.CodeChanged);
        }

        public static void SaveLocalVersion(UpdateInfo versionInfo)
        {
            if (versionInfo == null) return;
            try
            {
                versionInfo.AppVersion = Application.version;
                versionInfo.PatchFiles ??= new List<PatchFile>();
                string versionFilePath = System.IO.Path.Combine(Application.persistentDataPath, "version.json");
                string json = JsonSerializers.Shared.ToJson(versionInfo, true);
                FileStorages.Shared.AtomicWriteText(versionFilePath, json, versionFilePath + ".bak");
            }
            catch (Exception ex)
            {
                GameLog.Error($"[VersionManager] 保存本地版本文件失败：{ex.Message}");
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
