using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.ExceptionServices;
using UnityEngine;
using Cysharp.Threading.Tasks;
using Framework;
using Framework.Core;
using Framework.Serialization;

namespace Framework.HotUpdate
{
    /// <summary>
    /// 热更新管理器
    /// 负责版本检查、补丁下载、程序集加载和热更新启动
    /// </summary>
    public class HotUpdateManager : FrameworkComponent
    {
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
            _patchDownloader = new PatchDownloader();
            _fileVerifier = new FileVerifier();
            GameLog.Log("[HotUpdateManager] 热更新管理器初始化");
        }
        
        /// <summary>
        /// 检查更新
        /// </summary>
        /// <param name="updateUrl">更新服务器URL</param>
        /// <returns>更新信息</returns>
        public async UniTask<UpdateInfo> CheckUpdateAsync(string updateUrl)
        {
            _state = UpdateState.CheckingUpdate;
            GameLog.Log($"[HotUpdateManager] 开始检查更新: {updateUrl}");
            
            try
            {
                // 获取本地版本
                UpdateInfo localVersion = VersionManager.GetLocalVersion();
                GameLog.Log($"[HotUpdateManager] 本地版本: {localVersion.AppVersion}, 资源版本: {localVersion.ResourceVersion}, 代码版本: {localVersion.CodeVersion}");
                
                // 从服务器下载version.json
                // version.json：每次必须拿最新，forceRefresh=true 跳过断点续传
                string versionUrl = $"{updateUrl}/version.json";
                string tempPath = Path.Combine(Application.temporaryCachePath, "version_temp.json");
                bool downloadSuccess = await _patchDownloader.DownloadFileAsync(
                    versionUrl, tempPath, forceRefresh: true);
                
                if (!downloadSuccess)
                {
                    GameLog.Error("[HotUpdateManager] 下载版本文件失败");
                    _state = UpdateState.Error;
                    OnUpdateError?.Invoke("下载版本文件失败");
                    return null;
                }

                // 清单验签：AppConfig 配置了公钥即强制校验 version.json.sig。
                // 验签失败按"清单不可信"处理——本次不做任何热更（宁可停更，不执行可疑补丁）。
                if (!await VerifyManifestSignatureAsync(updateUrl, tempPath))
                {
                    _state = UpdateState.Error;
                    OnUpdateError?.Invoke("版本清单签名校验失败");
                    return null;
                }

                // 解析版本信息
                string json = File.ReadAllText(tempPath);
                UpdateInfo serverVersion = JsonSerializers.Shared.FromJson<UpdateInfo>(json);
                
                GameLog.Log($"[HotUpdateManager] 服务器版本: {serverVersion.AppVersion}, 资源版本: {serverVersion.ResourceVersion}, 代码版本: {serverVersion.CodeVersion}");
                
                // 判断更新类型
                UpdateType updateType = VersionManager.DetermineUpdateType(localVersion, serverVersion);
                serverVersion.Type = updateType;
                
                // 检查版本兼容性
                if (!string.IsNullOrEmpty(serverVersion.MinCompatibleVersion))
                {
                    bool isCompatible = VersionManager.CheckCompatibility(localVersion.AppVersion, serverVersion.MinCompatibleVersion);
                    
                    if (!isCompatible)
                    {
                        GameLog.Warning("[HotUpdateManager] 版本不兼容，需要强制更新");
                        serverVersion.ForceUpdate = true;
                        serverVersion.Type = UpdateType.FullUpdate;
                    }
                }
                
                // 触发更新可用事件
                if (updateType != UpdateType.None)
                {
                    OnUpdateAvailable?.Invoke(serverVersion);
                    GameLog.Log($"[HotUpdateManager] 发现更新: {updateType}, 补丁数量: {serverVersion.PatchFiles.Count}");
                }
                else
                {
                    GameLog.Log("[HotUpdateManager] 版本检查完成，无需更新");
                }
                
                _state = UpdateState.None;
                return serverVersion;
            }
            catch (Exception ex)
            {
                GameLog.Error($"[HotUpdateManager] 检查更新失败: {ex.Message}");
                _state = UpdateState.Error;
                OnUpdateError?.Invoke(ex.Message);
                throw;
            }
        }
        
        /// <summary>
        /// 校验已下载清单的 RSA 签名。
        /// AppConfig.UpdateManifestPublicKey 为空时跳过（开发期）；非空时下载伴生 version.json.sig
        /// 并对清单原始字节验签，签名缺失或不匹配一律判失败。
        /// </summary>
        /// <param name="updateUrl">更新服务器根 URL。</param>
        /// <param name="manifestPath">已下载到本地的 version.json 路径。</param>
        /// <returns>验签通过（或未启用验签）返回 true。</returns>
        private async UniTask<bool> VerifyManifestSignatureAsync(string updateUrl, string manifestPath)
        {
            string publicKeyPem = AppConfig.Load()?.UpdateManifestPublicKey;
            if (string.IsNullOrWhiteSpace(publicKeyPem))
            {
                if (UpdateSecurity.IsProductionEnv(AppConfig.Load()?.AppEnv))
                    GameLog.Warning("[HotUpdateManager] 生产环境未配置清单验签公钥（UpdateManifestPublicKey），" +
                                    "热更清单处于无签名保护状态，正式发布前必须补齐");
                return true;
            }

            string signatureUrl = $"{updateUrl}/version.json{UpdateSecurity.ManifestSignatureSuffix}";
            string signaturePath = Path.Combine(Application.temporaryCachePath, "version_temp.json.sig");

            bool signatureDownloaded = await _patchDownloader.DownloadFileAsync(
                signatureUrl, signaturePath, forceRefresh: true);
            if (!signatureDownloaded)
            {
                GameLog.Error("[HotUpdateManager] 已启用清单验签但下载 version.json.sig 失败，按验签失败处理");
                return false;
            }

            byte[] manifestBytes = File.ReadAllBytes(manifestPath);
            string signatureBase64 = File.ReadAllText(signaturePath);

            if (!UpdateSecurity.VerifyManifestSignature(manifestBytes, signatureBase64, publicKeyPem))
            {
                GameLog.Error("[HotUpdateManager] version.json 签名校验失败，清单不可信，本次热更中止");
                return false;
            }

            GameLog.Log("[HotUpdateManager] version.json 签名校验通过");
            return true;
        }

        /// <summary>
        /// 按 CodeVersion 下载代码热更补丁（PatchFiles 必须由服务端清单提供且逐项带 MD5）。
        /// </summary>
        public async UniTask<bool> DownloadCodePatchAsync(
            UpdateInfo serverVersion,
            UpdateInfo localVersion,
            string updateServerUrl,
            Action<float> onProgress = null)
        {
            if (!VersionManager.ShouldUpdateCode(serverVersion, localVersion))
            {
                GameLog.Log("[HotUpdateManager] 代码版本一致，跳过补丁下载");
                return true;
            }

            if (!VersionManager.TryResolveCodePatchFiles(serverVersion, updateServerUrl, out var patchFiles))
                return false;

            return await DownloadPatchAsync(serverVersion, patchFiles, onProgress);
        }

        /// <summary>
        /// 下载补丁列表。
        /// </summary>
        /// <returns>是否全部下载并校验成功。</returns>
        public async UniTask<bool> DownloadPatchAsync(
            UpdateInfo updateInfo,
            IReadOnlyList<PatchFile> patchFiles,
            Action<float> onProgress = null)
        {
            _state = UpdateState.Downloading;

            if (updateInfo == null || patchFiles == null || patchFiles.Count == 0)
            {
                GameLog.Log("[HotUpdateManager] 没有需要下载的补丁文件");
                _state = UpdateState.None;
                return false;
            }

            GameLog.Log($"[HotUpdateManager] 开始下载补丁，共{patchFiles.Count}个文件");

            try
            {
                long totalSize = VersionManager.CalculateTotalSize(patchFiles);
                long downloadedSize = 0;

                GameLog.Log($"[HotUpdateManager] 总下载大小: {VersionManager.FormatFileSize(totalSize)}");

                string downloadDir = Application.persistentDataPath;

                for (int i = 0; i < patchFiles.Count; i++)
                {
                    PatchFile patchFile = patchFiles[i];
                    string savePath = Path.Combine(downloadDir, patchFile.FileName);

                    GameLog.Log($"[HotUpdateManager] 下载文件 ({i + 1}/{patchFiles.Count}): {patchFile.FileName}");

                    bool success = await _patchDownloader.DownloadFileAsync(
                        patchFile.Url, savePath,
                        progress =>
                        {
                            float totalProgress = totalSize > 0
                                ? (downloadedSize + progress * patchFile.Size) / (float)totalSize
                                : (i + progress) / patchFiles.Count;
                            onProgress?.Invoke(totalProgress);
                        },
                        forceRefresh: true);

                    if (!success)
                    {
                        GameLog.Error($"[HotUpdateManager] 下载文件失败: {patchFile.FileName}");
                        _state = UpdateState.Error;
                        OnUpdateError?.Invoke($"下载文件失败: {patchFile.FileName}");
                        return false;
                    }

                    // 补丁哈希为强制项：清单缺 MD5 在 TryResolveCodePatchFiles 已被拒绝，
                    // 此处兜底再拦一次（其它调用方直接传入清单时同样不允许旁路校验）。
                    if (string.IsNullOrWhiteSpace(patchFile.MD5))
                    {
                        GameLog.Error($"[HotUpdateManager] 补丁 {patchFile.FileName} 未携带 MD5，拒绝安装（代码补丁禁止无校验下发）");

                        if (File.Exists(savePath))
                            File.Delete(savePath);

                        _state = UpdateState.Error;
                        OnUpdateError?.Invoke($"补丁缺少校验哈希: {patchFile.FileName}");
                        return false;
                    }

                    bool verified = await _fileVerifier.VerifyFileAsync(savePath, patchFile.MD5);
                    if (!verified)
                    {
                        GameLog.Error($"[HotUpdateManager] 文件校验失败: {patchFile.FileName}");

                        if (File.Exists(savePath))
                            File.Delete(savePath);

                        _state = UpdateState.Error;
                        OnUpdateError?.Invoke($"文件校验失败: {patchFile.FileName}");
                        return false;
                    }

                    downloadedSize += patchFile.Size > 0 ? patchFile.Size : 0;
                }

                VersionManager.CommitCodeUpdate(updateInfo);
                GameLog.Log("[HotUpdateManager] 补丁下载完成");
                onProgress?.Invoke(1.0f);
                _state = UpdateState.Complete;
                return true;
            }
            catch (Exception ex)
            {
                GameLog.Error($"[HotUpdateManager] 下载补丁失败: {ex.Message}");
                _state = UpdateState.Error;
                OnUpdateError?.Invoke(ex.Message);
                throw;
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
            string persistentPath = Path.Combine(Application.persistentDataPath, fileName);
            if (File.Exists(persistentPath))
                return await UniTask.RunOnThreadPool(() => File.ReadAllBytes(persistentPath));

            GameLog.Warning($"[HotUpdateManager] persistent 无热更 DLL，回退 StreamingAssets: {fileName}");
            return await StreamingAssetsBytesReader.ReadAsync(fileName);
        }
        
        /// <summary>
        /// 启动热更新逻辑
        /// </summary>
        public void StartHotfix()
        {
            GameLog.Log("[HotUpdateManager] 启动热更新逻辑");
            
            try
            {
                if (_hotUpdateAssembly == null)
                {
                    GameLog.Error("[HotUpdateManager] 热更新程序集未加载，无法启动");
                    return;
                }
                
                // 通过反射创建HotfixEntry实例并调用Start方法
                Type entryType = _hotUpdateAssembly.GetType("HotUpdate.Entry.HotfixEntry");
                if (entryType == null)
                {
                    GameLog.Error("[HotUpdateManager] 找不到HotfixEntry类型");
                    return;
                }
                
                object entryInstance = Activator.CreateInstance(entryType);
                MethodInfo startMethod = entryType.GetMethod("Start");
                
                if (startMethod == null)
                {
                    GameLog.Error("[HotUpdateManager] 找不到Start方法");
                    return;
                }
                
                startMethod.Invoke(entryInstance, null);
                
                GameLog.Log("[HotUpdateManager] 热更新逻辑启动成功");
                OnUpdateComplete?.Invoke();
            }
            catch (TargetInvocationException ex) when (ex.InnerException != null)
            {
                GameLog.Error($"[HotUpdateManager] 启动热更新逻辑失败: {ex.InnerException.Message}\n{ex.InnerException}");
                OnUpdateError?.Invoke(ex.InnerException.Message);
                ExceptionDispatchInfo.Capture(ex.InnerException).Throw();
                throw;
            }
            catch (Exception ex)
            {
                GameLog.Error($"[HotUpdateManager] 启动热更新逻辑失败: {ex.Message}");
                OnUpdateError?.Invoke(ex.Message);
                throw;
            }
        }
        
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
