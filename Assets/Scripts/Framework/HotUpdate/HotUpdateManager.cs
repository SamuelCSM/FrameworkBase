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
                
                // 解析版本信息
                string json = File.ReadAllText(tempPath);
                UpdateInfo serverVersion = JsonUtility.FromJson<UpdateInfo>(json);
                
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
        /// 按 CodeVersion 下载代码热更补丁（支持 PatchFiles 为空时按约定 URL 补全）。
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

                    // 服务端未提供 MD5 时跳过校验（约定 URL 补全场景）。
                    if (!string.IsNullOrEmpty(patchFile.MD5))
                    {
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
        /// 列出的每个程序集——先 <c>Blokus.Core.dll.bytes</c>（双端同源规则内核），
        /// 再 <c>GameProtocol.dll.bytes</c>（项目协议目录），最后 <c>HotUpdate.dll.bytes</c>
        /// （业务逻辑 + HotfixEntry）。依赖程序集必须先于被依赖方完成 <see cref="Assembly.Load(byte[])"/>，
        /// 否则解释域加载 HotUpdate 时会因找不到依赖而失败。
        /// </para>
        /// <para>
        /// 同源修复约束：客户端的 Blokus.Core BUG 可经此处下发的 <c>Blokus.Core.dll.bytes</c> 热更补丁修复；
        /// 但服务端的 Blokus.Core 为<b>原生编译</b>（GameServer 项目引用），无法热更，须「重新编译 + 重新部署 +
        /// 停服维护重启」。修复后两端必须按版本卡死配对（匹配时校验协议/数据版本一致，见需求 14.6、24.6），
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
                // 按依赖顺序逐个加载，确保 Blokus.Core 与 GameProtocol 先于引用它们的 HotUpdate 就绪。
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
