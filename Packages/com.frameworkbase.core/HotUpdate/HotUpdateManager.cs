using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using UnityEngine;
using Cysharp.Threading.Tasks;
using Framework;
using Framework.Core;
using Framework.Serialization;
using Framework.Storage;

namespace Framework.HotUpdate
{
    /// <summary>
    /// 热更新管理器
    /// 负责版本检查、补丁下载、程序集加载和热更新启动
    /// </summary>
    public class HotUpdateManager : FrameworkComponent<HotUpdateManager>
    {
        private const long MaxManifestBytes = 1024 * 1024;
        private const long MaxSignatureBytes = 16 * 1024;

        /// <summary>
        /// 未验签阶段只允许解析的最小信封。该类型只用于选择客户端本地信任根，不承载任何更新决策。
        /// </summary>
        [Serializable]
        private sealed class ManifestKeyEnvelope
        {
            public string KeyId = string.Empty;
        }

        private readonly SemaphoreSlim _checkGate = new SemaphoreSlim(1, 1);
        private readonly SemaphoreSlim _installGate = new SemaphoreSlim(1, 1);
        private UpdateState _state = UpdateState.None;

        /// <summary>承载 HotfixEntry 的热更入口程序集（即 HotUpdate）；StartHotfix 通过它反射启动。</summary>
        private Assembly _hotUpdateAssembly;

        /// <summary>按加载顺序记录的全部已加载热更程序集（依赖在前，被依赖方在后）。</summary>
        private readonly List<Assembly> _loadedHotUpdateAssemblies = new List<Assembly>();

        private PatchDownloader _patchDownloader;
        private FileVerifier _fileVerifier;
        
        /// <summary>
        /// 当前更新状态
        /// </summary>
        public UpdateState State => _state;
        
        /// <summary>
        /// 更新可用事件
        /// </summary>
        public event Action<UpdateInfo> OnUpdateAvailable;
        
        /// <summary>
        /// 更新完成事件
        /// </summary>
        public event Action OnUpdateComplete;
        
        /// <summary>
        /// 更新错误事件
        /// </summary>
        public event Action<string> OnUpdateError;
        
        public override void OnInit()
        {
            base.OnInit();
            HotUpdateSlotManager.PrepareForLaunch();
            _patchDownloader = new PatchDownloader();
            _fileVerifier = new FileVerifier();
            GameLog.Log("[HotUpdateManager] 已初始化事务代码槽与强制验签链路。");
        }
        
        /// <summary>
        /// 检查更新
        /// </summary>
        /// <param name="updateUrl">更新服务器URL</param>
        /// <returns>更新信息</returns>
        public async UniTask<UpdateInfo> CheckUpdateAsync(
            string updateUrl,
            CancellationToken cancellationToken = default)
        {
            await _checkGate.WaitAsync(cancellationToken);
            _state = UpdateState.CheckingUpdate;
            try
            {
                Core.AppConfigAsset config = Core.AppConfig.Load();
                if (!UpdateSecurity.ValidateUpdateServerUrl(updateUrl, config?.AppEnv, out string urlRejectReason))
                    throw new InvalidDataException(urlRejectReason);

                UpdateInfo localVersion = VersionManager.GetLocalVersion();
                string versionUrl = $"{updateUrl.TrimEnd('/')}/version.json";
                string tempPath = Path.Combine(Application.temporaryCachePath, "version_temp.json");
                if (!await _patchDownloader.DownloadFileAsync(
                        versionUrl,
                        tempPath,
                        forceRefresh: true,
                        cancellationToken: cancellationToken))
                {
                    throw new IOException("下载热更新清单失败。");
                }

                long manifestLength = FileStorages.Shared.GetFileSize(tempPath);
                if (manifestLength <= 0 || manifestLength > MaxManifestBytes)
                    throw new InvalidDataException($"热更新清单大小非法：{manifestLength} 字节。");

                byte[] manifestBytes = FileStorages.Shared.ReadBytes(tempPath);
                string json = Encoding.UTF8.GetString(manifestBytes);

                // 未验签阶段只解析 KeyId 信封，用于从本地公钥环选择信任根；其他字段一律不参与决策。
                ManifestKeyEnvelope envelope = JsonSerializers.Shared.FromJson<ManifestKeyEnvelope>(json);
                if (envelope == null || string.IsNullOrWhiteSpace(envelope.KeyId))
                    throw new InvalidDataException("热更新清单缺少可用于选择验签公钥的 KeyId。");

                if (!await VerifyManifestSignatureAsync(
                        updateUrl,
                        manifestBytes,
                        envelope.KeyId,
                        cancellationToken))
                {
                    throw new CryptographicException("热更新清单签名验证失败。");
                }

                // 只有原始字节验签通过后，才允许反序列化完整清单并执行版本、平台、渠道及文件集准入。
                UpdateInfo serverVersion = JsonSerializers.Shared.FromJson<UpdateInfo>(json);
                if (serverVersion == null)
                    throw new InvalidDataException("热更新清单反序列化失败。");
                if (!UpdateSecurity.ValidateManifest(
                        serverVersion,
                        localVersion,
                        config?.AppEnv,
                        config?.AppChannel,
                        out string rejectReason))
                {
                    GameLog.Error($"[HotUpdateManager] 清单未通过安全准入：{rejectReason}");
                    _state = UpdateState.Error;
                    OnUpdateError?.Invoke(rejectReason);
                    return null;
                }

                UpdateType updateType = VersionManager.DetermineUpdateType(localVersion, serverVersion);
                serverVersion.Type = updateType;
                if (!string.IsNullOrEmpty(serverVersion.MinCompatibleVersion) &&
                    !VersionManager.CheckCompatibility(Application.version, serverVersion.MinCompatibleVersion))
                {
                    serverVersion.ForceUpdate = true;
                    serverVersion.Type = UpdateType.FullUpdate;
                }

                if (serverVersion.Type != UpdateType.None)
                    OnUpdateAvailable?.Invoke(serverVersion);

                _state = UpdateState.None;
                return serverVersion;
            }
            catch (OperationCanceledException)
            {
                _state = UpdateState.None;
                throw;
            }
            catch (Exception ex)
            {
                GameLog.Error($"[HotUpdateManager] 检查更新失败：{ex}");
                _state = UpdateState.Error;
                OnUpdateError?.Invoke(ex.Message);
                return null;
            }
            finally
            {
                _checkGate.Release();
            }
        }
        
        /// <summary>
        /// 校验已下载清单的 RSA-SHA256 签名。
        /// 所有环境都必须按 KeyId 从客户端公钥环选择信任根，并对清单原始字节验签；
        /// 公钥、签名文件缺失或签名不匹配一律失败，开发环境也不得绕过远程代码信任边界。
        /// </summary>
        /// <param name="updateUrl">更新服务器根 URL。</param>
        /// <param name="manifestBytes">网络下载并限制大小后的 version.json 原始字节。</param>
        /// <param name="keyId">未验签信封中声明的公钥标识，只用于本地密钥选择。</param>
        /// <param name="cancellationToken">清单检查生命周期取消令牌。</param>
        /// <returns>签名文件存在、大小合法且原始字节验签通过时返回 true。</returns>
        private async UniTask<bool> VerifyManifestSignatureAsync(
            string updateUrl,
            byte[] manifestBytes,
            string keyId,
            CancellationToken cancellationToken)
        {
            Core.AppConfigAsset config = Core.AppConfig.Load();
            string publicKey = UpdateSecurity.ResolvePublicKey(
                keyId,
                config?.UpdateManifestPublicKey,
                config?.UpdateManifestPublicKeys);

            // 任何环境的远程代码清单都必须验签。开发环境应使用独立 development 密钥，而不是跳过信任边界。
            if (string.IsNullOrWhiteSpace(publicKey))
            {
                GameLog.Error($"[HotUpdateManager] 未找到 KeyId={keyId} 对应的清单验签公钥，更新已拒绝。");
                return false;
            }

            string signatureUrl = $"{updateUrl.TrimEnd('/')}/version.json{UpdateSecurity.ManifestSignatureSuffix}";
            string signaturePath = Path.Combine(Application.temporaryCachePath, "version_temp.json.sig");
            if (!await _patchDownloader.DownloadFileAsync(
                    signatureUrl,
                    signaturePath,
                    forceRefresh: true,
                    cancellationToken: cancellationToken))
            {
                return false;
            }

            long signatureLength = FileStorages.Shared.GetFileSize(signaturePath);
            if (signatureLength <= 0 || signatureLength > MaxSignatureBytes)
            {
                GameLog.Error($"[HotUpdateManager] 清单签名文件大小非法：{signatureLength} 字节。");
                return false;
            }

            return UpdateSecurity.VerifyManifestSignature(
                manifestBytes,
                FileStorages.Shared.ReadText(signaturePath),
                publicKey);
        }

        /// <summary>
        /// 按 CodeVersion 下载完整代码槽快照；PatchFiles 必须与客户端程序集白名单完全一致并逐项携带 Size + SHA-256。
        /// </summary>
        public async UniTask<bool> DownloadCodePatchAsync(
            UpdateInfo serverVersion,
            UpdateInfo localVersion,
            string updateServerUrl,
            Action<float> onProgress = null,
            CancellationToken cancellationToken = default)
        {
            if (!VersionManager.ShouldUpdateCode(serverVersion, localVersion))
            {
                GameLog.Log("[HotUpdateManager] 代码版本一致，跳过补丁下载");
                return true;
            }

            if (!VersionManager.TryResolveCodePatchFiles(serverVersion, updateServerUrl, out var patchFiles))
                return false;

            return await DownloadPatchAsync(serverVersion, patchFiles, onProgress, cancellationToken);
        }

        /// <summary>
        /// 下载补丁列表。
        /// </summary>
        /// <returns>是否全部下载并校验成功。</returns>
        public async UniTask<bool> DownloadPatchAsync(
            UpdateInfo updateInfo,
            IReadOnlyList<PatchFile> patchFiles,
            Action<float> onProgress = null,
            CancellationToken cancellationToken = default)
        {
            if (updateInfo == null || patchFiles == null || patchFiles.Count == 0)
            {
                _state = UpdateState.Error;
                return false;
            }

            await _installGate.WaitAsync(cancellationToken);
            _state = UpdateState.Downloading;
            string stagingDirectory = null;
            try
            {
                stagingDirectory = HotUpdateSlotManager.PrepareStagingSlot(updateInfo);
                long totalSize = VersionManager.CalculateTotalSize(patchFiles);
                long completedSize = 0;

                for (int i = 0; i < patchFiles.Count; i++)
                {
                    PatchFile patch = patchFiles[i];
                    if (!UpdateSecurity.ValidateCodePatchFile(patch, out string rejectReason))
                        throw new InvalidDataException(rejectReason);

                    string savePath = HotUpdateSlotManager.GetSafeStagingFilePath(stagingDirectory, patch.FileName);
                    bool downloaded = await _patchDownloader.DownloadFileAsync(
                        patch.Url,
                        savePath,
                        progress =>
                        {
                            float totalProgress = totalSize > 0
                                ? (completedSize + progress * patch.Size) / totalSize
                                : (i + progress) / patchFiles.Count;
                            onProgress?.Invoke(totalProgress);
                        },
                        forceRefresh: true,
                        cancellationToken: cancellationToken);

                    if (!downloaded || !await _fileVerifier.VerifyPatchFileAsync(savePath, patch))
                        throw new InvalidDataException($"补丁下载或完整性校验失败：{patch.FileName}");

                    completedSize += patch.Size;
                }

                _state = UpdateState.Installing;
                if (!HotUpdateSlotManager.CommitStagingSlot(updateInfo, stagingDirectory, out string commitError))
                    throw new IOException(commitError);

                onProgress?.Invoke(1f);
                _state = UpdateState.Complete;
                return true;
            }
            catch (OperationCanceledException)
            {
                CleanupStagingDirectory(stagingDirectory);
                _state = UpdateState.None;
                throw;
            }
            catch (Exception ex)
            {
                GameLog.Error($"[HotUpdateManager] 事务补丁安装失败：{ex}");
                CleanupStagingDirectory(stagingDirectory);
                _state = UpdateState.Error;
                OnUpdateError?.Invoke(ex.Message);
                return false;
            }
            finally
            {
                _installGate.Release();
            }
        }
        
        /// <summary>
        /// 尽力清理由本次安装创建、但尚未提交为正式槽的 staging 目录。清理失败只记录告警，不能覆盖原始失败原因。
        /// </summary>
        private static void CleanupStagingDirectory(string stagingDirectory)
        {
            if (string.IsNullOrEmpty(stagingDirectory) || !Directory.Exists(stagingDirectory))
                return;
            try { Directory.Delete(stagingDirectory, true); }
            catch (Exception cleanupEx)
            {
                GameLog.Warning($"[HotUpdateManager] 清理 staging 目录失败：{cleanupEx.Message}");
            }
        }

        /// <summary>
        /// 加载 AOT 泛型补充元数据（须在 <see cref="LoadHotUpdateAssemblyAsync"/> 之前调用）。
        /// </summary>
        public async UniTask<bool> LoadMetadataAsync()
        {
            GameLog.Log("[HotUpdateManager] 开始加载 AOT 泛型补充元数据");
            try
            {
                return await HybridCLRMetadataLoader.LoadAllAsync();
            }
            catch (Exception ex)
            {
                GameLog.Error($"[HotUpdateManager] 加载元数据失败: {ex.Message}");
                throw;
            }
        }

        /// <summary>
        /// 加载热更新程序集（IL2CPP 真机由 HybridCLR 接管 Assembly.Load）。
        /// <para>
        /// 多程序集按依赖顺序下发：本方法依次加载 <see cref="VersionManager.HotUpdateAssemblyFileNames"/>
        /// 列出的每个程序集——被依赖方在前（如协议目录 <c>GameProtocol.dll.bytes</c>、项目自有的
        /// 双端同源规则内核等），业务逻辑层 <c>HotUpdate.dll.bytes</c>（含 HotfixEntry）最后。
        /// 依赖程序集必须先于被依赖方完成 <see cref="Assembly.Load(byte[])"/>，
        /// 否则解释域加载 HotUpdate 时会因找不到依赖而失败。
        /// </para>
        /// <para>
        /// 双端同源程序集的修复约束：客户端侧 BUG 可经热更补丁下发修复；服务端侧同一程序集通常为
        /// <b>原生编译</b>（服务器工程直接引用），无法热更，须「重新编译 + 重新部署 + 停服维护重启」。
        /// 修复后两端必须按版本卡死配对（匹配时校验协议/数据版本一致），
        /// 避免一端已修、另一端未修导致双端裁定分叉。
        /// </para>
        /// </summary>
        public async UniTask<bool> LoadHotUpdateAssemblyAsync()
        {
            GameLog.Log("[HotUpdateManager] 开始加载热更新程序集");

            try
            {
                _loadedHotUpdateAssemblies.Clear();
                _hotUpdateAssembly = null;

#if UNITY_EDITOR
                // 编辑器模式：热更程序集已由 Unity 正常编译并加载到当前 AppDomain，直接取用即可，
                // 无需读取 *.dll.bytes。按依赖顺序逐个解析以保持与真机一致的语义与日志。
                var loadedAssemblies = AppDomain.CurrentDomain.GetAssemblies();

                foreach (string bytesFileName in VersionManager.HotUpdateAssemblyFileNames)
                {
                    string assemblyName = VersionManager.ToAssemblyName(bytesFileName);
                    Assembly assembly = loadedAssemblies
                        .FirstOrDefault(a => a.GetName().Name == assemblyName);

                    if (assembly == null)
                    {
                        GameLog.Error($"[HotUpdateManager] 在编辑器中找不到热更程序集: {assemblyName}");
                        return false;
                    }

                    _loadedHotUpdateAssemblies.Add(assembly);
                    if (assemblyName == VersionManager.EntryHotUpdateAssemblyName)
                        _hotUpdateAssembly = assembly;

                    GameLog.Log($"[HotUpdateManager] 编辑器模式：使用已加载程序集 {assemblyName}");
                }
#else
                // 真机（IL2CPP）：HybridCLR 解释域通过 Assembly.Load 加载热更 DLL，须先完成 LoadMetadataAsync。
                // 按依赖顺序逐个加载，确保被依赖程序集（协议目录等）先于引用它们的 HotUpdate 就绪。
                foreach (string bytesFileName in VersionManager.HotUpdateAssemblyFileNames)
                {
                    string assemblyName = VersionManager.ToAssemblyName(bytesFileName);

                    byte[] dllBytes = await ReadHotUpdateDllBytesAsync(bytesFileName);
                    if (dllBytes == null || dllBytes.Length == 0)
                    {
                        GameLog.Error($"[HotUpdateManager] 无法读取热更程序集字节: {bytesFileName}");
                        return false;
                    }

                    Assembly assembly = Assembly.Load(dllBytes);
                    _loadedHotUpdateAssemblies.Add(assembly);
                    if (assemblyName == VersionManager.EntryHotUpdateAssemblyName)
                        _hotUpdateAssembly = assembly;

                    GameLog.Log($"[HotUpdateManager] 热更程序集加载成功: {assembly.FullName}");
                }
#endif

                if (_hotUpdateAssembly == null)
                {
                    GameLog.Error($"[HotUpdateManager] 未能定位热更入口程序集: {VersionManager.EntryHotUpdateAssemblyName}");
                    return false;
                }

                GameLog.Log($"[HotUpdateManager] 热更程序集全部加载完成，共 {_loadedHotUpdateAssemblies.Count} 个");
                return true;
            }
            catch (Exception ex)
            {
                GameLog.Error($"[HotUpdateManager] 加载热更新程序集失败: {ex.Message}");
                throw;
            }
        }

        /// <summary>读取指定热更 DLL：优先 persistent（已下载补丁），回退 StreamingAssets 首包。</summary>
        private static async UniTask<byte[]> ReadHotUpdateDllBytesAsync(string fileName)
        {
            if (HotUpdateSlotManager.TryGetActiveCodeVersion(out _))
            {
                if (!HotUpdateSlotManager.TryResolveActiveFile(fileName, out string activePath))
                    throw new FileNotFoundException($"活动代码槽缺少必需程序集，禁止回退并混用整包基线：{fileName}");
                return await FileStorages.Shared.ReadBytesAsync(activePath);
            }

            GameLog.Log($"[HotUpdateManager] 当前没有已验证活动槽，使用整包程序集基线：{fileName}");
            return await StreamingAssetsBytesReader.ReadAsync(fileName);
        }
        
        /// <summary>
        /// 启动热更新逻辑
        /// </summary>
        public bool StartHotfix()
        {
            try
            {
                if (_hotUpdateAssembly == null)
                    throw new InvalidOperationException("热更新入口程序集尚未加载。");

                Type entryType = _hotUpdateAssembly.GetType("HotUpdate.Entry.HotfixEntry");
                if (entryType == null)
                    throw new MissingMemberException("未找到热更新入口类型 HotUpdate.Entry.HotfixEntry。");

                object entryInstance = Activator.CreateInstance(entryType);
                MethodInfo startMethod = entryType.GetMethod("Start");
                if (startMethod == null)
                    throw new MissingMethodException(entryType.FullName, "Start");

                startMethod.Invoke(entryInstance, null);
                GameLog.Log("[HotUpdateManager] 热更新入口启动成功。");
                OnUpdateComplete?.Invoke();
                return true;
            }
            catch (TargetInvocationException ex) when (ex.InnerException != null)
            {
                HotUpdateSlotManager.MarkPendingSlotFailed(ex.InnerException.Message);
                OnUpdateError?.Invoke(ex.InnerException.Message);
                ExceptionDispatchInfo.Capture(ex.InnerException).Throw();
                throw;
            }
            catch (Exception ex)
            {
                HotUpdateSlotManager.MarkPendingSlotFailed(ex.Message);
                OnUpdateError?.Invoke(ex.Message);
                throw;
            }
        }

        public void ConfirmPendingUpdate() => HotUpdateSlotManager.ConfirmPendingSlot();

        public void MarkPendingUpdateFailed(string reason) => HotUpdateSlotManager.MarkPendingSlotFailed(reason);
        
        public override void OnShutdown()
        {
            base.OnShutdown();
            _hotUpdateAssembly = null;
            _loadedHotUpdateAssemblies.Clear();
            GameLog.Log("[HotUpdateManager] 热更新管理器关闭");
        }
    }
    
    /// <summary>
    /// 更新状态
    /// </summary>
    public enum UpdateState
    {
        None,              // 无状态
        CheckingUpdate,    // 检查更新中
        Downloading,       // 下载中
        Installing,        // 安装中
        Complete,          // 完成
        Error              // 错误
    }
}
