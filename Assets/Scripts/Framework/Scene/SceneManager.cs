using System;
using System.Collections.Generic;
using Cysharp.Threading.Tasks;
using UnityEngine;
using UnityEngine.AddressableAssets;
using UnityEngine.ResourceManagement.AsyncOperations;
using UnityEngine.ResourceManagement.ResourceProviders;
using UnityEngine.SceneManagement;

namespace Framework
{
    /// <summary>
    /// 场景管理器 — 负责场景切换、异步加载、过场动画与进度回调。
    ///
    /// <para>主要接口：</para>
    /// <list type="bullet">
    ///   <item><see cref="SwitchToAsync"/>     — 高级入口：过场动画 → 加载 → 结束动画（推荐）</item>
    ///   <item><see cref="LoadSceneAsync"/>    — 低级入口：仅加载，无过场动画</item>
    ///   <item><see cref="UnloadSceneAsync"/>  — 卸载场景并释放资源</item>
    ///   <item><see cref="PreloadSceneAsync"/> — 后台预加载（不激活），SwitchToAsync 自动复用</item>
    ///   <item><see cref="CancelPreload"/>     — 释放预加载缓存</item>
    /// </list>
    ///
    /// <para>
    /// 过场 UI 通过 <see cref="ISceneTransitionProvider"/> 接口驱动。
    /// 未注入时自动使用内置的 <see cref="BuiltInFadeTransition"/>（黑屏 + 底部进度条）。
    /// 如需接入自定义 Loading 窗口（如 UIManager 的窗口系统），调用
    /// <see cref="SetTransitionProvider"/> 注入即可。
    /// </para>
    /// </summary>
    public class SceneManager : Core.FrameworkComponent
    {
        // ── 内部状态 ─────────────────────────────────────────────────────────

        /// <summary>当前主场景名（Single 模式切换后更新）。</summary>
        private string _currentScene;

        /// <summary>防止并发调用 SwitchToAsync。</summary>
        private bool _isSwitching;

        /// <summary>已激活场景的句柄缓存（场景地址 → 句柄）。</summary>
        private readonly Dictionary<string, AsyncOperationHandle<SceneInstance>> _loadedScenes
            = new Dictionary<string, AsyncOperationHandle<SceneInstance>>();

        /// <summary>已后台预加载（未激活）的句柄缓存（场景地址 → 句柄）。</summary>
        private readonly Dictionary<string, AsyncOperationHandle<SceneInstance>> _preloadedScenes
            = new Dictionary<string, AsyncOperationHandle<SceneInstance>>();

        /// <summary>
        /// 过场 UI 提供者。
        /// 默认为 null，第一次使用时懒加载 <see cref="BuiltInFadeTransition"/>。
        /// </summary>
        private ISceneTransitionProvider _transitionProvider;

        // ── 场景上下文 ────────────────────────────────────────────────────────

        /// <summary>当前场景上下文中注册的 SceneBase 组件，按类型索引。</summary>
        private readonly Dictionary<System.Type, SceneBase> _sceneContexts = new Dictionary<System.Type, SceneBase>();

        /// <summary>当前激活的场景上下文（Single 场景切换后更新）。</summary>
        public SceneBase CurrentContext { get; private set; }

        // ── 事件 ──────────────────────────────────────────────────────────────

        /// <summary>场景激活完成时触发，参数为场景地址。</summary>
        public event Action<string> OnSceneLoaded;

        /// <summary>场景卸载完成时触发，参数为场景地址。</summary>
        public event Action<string> OnSceneUnloaded;

        /// <summary>SwitchToAsync 开始（过场动画开始之前）时触发，参数为（from, to）。</summary>
        public event Action<string, string> OnSwitchStarted;

        /// <summary>SwitchToAsync 完成（过场动画结束之后）时触发，参数为（from, to）。</summary>
        public event Action<string, string> OnSwitchCompleted;

        // ── 属性 ──────────────────────────────────────────────────────────────

        /// <summary>当前主场景地址。</summary>
        public string CurrentScene => _currentScene;

        /// <summary>是否正在执行 SwitchToAsync。</summary>
        public bool IsSwitching => _isSwitching;

        // ── 生命周期 ──────────────────────────────────────────────────────────

        public override void OnInit()
        {
            _currentScene = UnityEngine.SceneManagement.SceneManager.GetActiveScene().name;
            GameLog.Log($"[SceneManager] 初始化完成，当前场景: {_currentScene}");
        }

        public override void OnShutdown()
        {
            foreach (var kvp in _loadedScenes)
                if (kvp.Value.IsValid()) Addressables.UnloadSceneAsync(kvp.Value);
            _loadedScenes.Clear();

            foreach (var kvp in _preloadedScenes)
                if (kvp.Value.IsValid()) Addressables.UnloadSceneAsync(kvp.Value);
            _preloadedScenes.Clear();

            OnSceneLoaded     = null;
            OnSceneUnloaded   = null;
            OnSwitchStarted   = null;
            OnSwitchCompleted = null;

            GameLog.Log("[SceneManager] 已关闭");
        }

        // ── 过场提供者注入 ────────────────────────────────────────────────────

        /// <summary>
        /// 注入自定义过场 UI 提供者，替换默认的黑屏 + 进度条实现。
        ///
        /// <para>典型用法（接入 UIManager 的窗口系统）：</para>
        /// <code>
        /// public class MyLoadingTransition : ISceneTransitionProvider
        /// {
        ///     public async UniTask BeginAsync(Color _, float fadeDuration)
        ///         => await GameEntry.UI.OpenUIAsync&lt;MyLoadingWindow&gt;();
        ///
        ///     public void OnProgress(float p)
        ///         => GameEntry.UI.GetUI&lt;MyLoadingWindow&gt;()?.SetProgress(p);
        ///
        ///     public async UniTask EndAsync(float fadeDuration)
        ///         => await GameEntry.UI.CloseUIAsync&lt;MyLoadingWindow&gt;();
        ///
        ///     public void ForceHide()
        ///         => GameEntry.UI.CloseUI&lt;MyLoadingWindow&gt;();
        /// }
        ///
        /// // 在 GameEntry 初始化后调用一次：
        /// GameEntry.Scene.SetTransitionProvider(new MyLoadingTransition());
        /// </code>
        /// </summary>
        public void SetTransitionProvider(ISceneTransitionProvider provider)
        {
            _transitionProvider = provider;
            GameLog.Log($"[SceneManager] 过场提供者已更换: {provider?.GetType().Name ?? "null（将使用内置）"}");
        }

        /// <summary>获取当前使用的过场提供者（若未注入则懒加载内置实现）。</summary>
        internal ISceneTransitionProvider GetOrCreateTransitionProvider()
        {
            if (_transitionProvider == null)
                _transitionProvider = new BuiltInFadeTransition();
            return _transitionProvider;
        }

        // ═══════════════════════════════════════════════════════════════════════
        //  高级 API — 带过场动画的切换
        // ═══════════════════════════════════════════════════════════════════════

        /// <summary>
        /// 带过场动画切换场景（推荐入口）。
        ///
        /// <para>执行流程：</para>
        /// <list type="number">
        ///   <item>调用 <see cref="ISceneTransitionProvider.BeginAsync"/> 播放进入动画</item>
        ///   <item>异步加载新场景，期间持续回调 <see cref="ISceneTransitionProvider.OnProgress"/></item>
        ///   <item>补齐 <see cref="SceneTransitionConfig.MinLoadingDuration"/> 最短停留时间</item>
        ///   <item>调用 <see cref="ISceneTransitionProvider.EndAsync"/> 播放退出动画</item>
        /// </list>
        /// </summary>
        /// <param name="sceneName">Addressables 中的场景地址。</param>
        /// <param name="config">过场参数，为 null 时使用 <see cref="SceneTransitionConfig.Standard"/>。</param>
        public async UniTask SwitchToAsync(string sceneName, SceneTransitionConfig config = null)
        {
            if (string.IsNullOrEmpty(sceneName))
            {
                GameLog.Error("[SceneManager] SwitchToAsync: 场景名称不能为空");
                return;
            }

            if (_isSwitching)
            {
                GameLog.Warning($"[SceneManager] 场景切换进行中，忽略: {sceneName}");
                return;
            }

            config ??= SceneTransitionConfig.Standard;

            string fromScene = _currentScene;
            _isSwitching = true;
            OnSwitchStarted?.Invoke(fromScene, sceneName);
            GameLog.Log($"[SceneManager] 切换开始: {fromScene} → {sceneName}");

            var provider = GetOrCreateTransitionProvider();

            try
            {
                // ① 调用提供者：显示 Loading / 淡出
                await provider.BeginAsync(config.OverlayColor, config.FadeDuration);

                float startTime = Time.realtimeSinceStartup;

                // ② 加载场景（真实进度 0–0.9，激活阶段补到 1.0）
                await LoadSceneAsync(
                    sceneName,
                    p =>
                    {
                        float mapped = p * 0.9f;
                        provider.OnProgress(mapped);
                        config.OnProgress?.Invoke(mapped);
                    },
                    config.LoadMode);

                // ③ 补齐最短停留（防止快速设备进度条一闪而过）
                float elapsed   = Time.realtimeSinceStartup - startTime;
                float remaining = config.MinLoadingDuration - elapsed;
                if (remaining > 0f)
                    await UniTask.Delay(TimeSpan.FromSeconds(remaining), ignoreTimeScale: true);

                provider.OnProgress(1f);
                config.OnProgress?.Invoke(1f);

                // ④ 调用提供者：隐藏 Loading / 淡入
                await provider.EndAsync(config.FadeDuration);

                OnSwitchCompleted?.Invoke(fromScene, sceneName);
                GameLog.Log($"[SceneManager] 切换完成: {fromScene} → {sceneName}");
            }
            catch (Exception ex)
            {
                GameLog.Error($"[SceneManager] 切换异常 [{sceneName}]: {ex.Message}");
                provider.ForceHide();
                throw;
            }
            finally
            {
                _isSwitching = false;
            }
        }

        // ═══════════════════════════════════════════════════════════════════════
        //  低级 API — 仅加载 / 卸载
        // ═══════════════════════════════════════════════════════════════════════

        /// <summary>
        /// 仅异步加载场景（不带过场动画）。
        /// 适合 Additive 叠加场景或需要外部自行控制过场的情形。
        /// </summary>
        /// <param name="sceneName">Addressables 场景地址。</param>
        /// <param name="onProgress">进度回调（0–1）。</param>
        /// <param name="loadMode">Single = 替换 / Additive = 叠加。</param>
        public async UniTask LoadSceneAsync(
            string sceneName,
            Action<float> onProgress = null,
            LoadSceneMode loadMode   = LoadSceneMode.Single)
        {
            if (string.IsNullOrEmpty(sceneName))
            {
                GameLog.Error("[SceneManager] LoadSceneAsync: 场景名称不能为空");
                return;
            }

            GameLog.Log($"[SceneManager] 加载场景: {sceneName}");

            try
            {
                AsyncOperationHandle<SceneInstance> handle;

                if (_preloadedScenes.TryGetValue(sceneName, out var preloaded))
                {
                    // 激活预加载场景（已在内存中，仅需 Activate）
                    GameLog.Log($"[SceneManager] 激活预加载场景: {sceneName}");
                    var activateOp = preloaded.Result.ActivateAsync();
                    while (!activateOp.isDone)
                    {
                        onProgress?.Invoke(activateOp.progress);
                        await UniTask.Yield();
                    }
                    handle = preloaded;
                    _preloadedScenes.Remove(sceneName);
                }
                else
                {
                    handle = Addressables.LoadSceneAsync(sceneName, loadMode);
                    while (!handle.IsDone)
                    {
                        onProgress?.Invoke(handle.PercentComplete);
                        await UniTask.Yield();
                    }

                    if (handle.Status != AsyncOperationStatus.Succeeded)
                    {
                        GameLog.Error($"[SceneManager] 加载失败: {sceneName}");
                        return;
                    }
                }

                _loadedScenes[sceneName] = handle;
                if (loadMode == LoadSceneMode.Single)
                    _currentScene = sceneName;

                onProgress?.Invoke(1f);
                GameLog.Log($"[SceneManager] 加载完成: {sceneName}");
                OnSceneLoaded?.Invoke(sceneName);
            }
            catch (Exception ex)
            {
                GameLog.Error($"[SceneManager] 加载异常 [{sceneName}]: {ex.Message}");
                throw;
            }
        }

        /// <summary>
        /// 异步卸载场景，完成后清理未使用的资源。
        /// 单场景时（sceneCount == 1）不允许卸载。
        /// </summary>
        public async UniTask UnloadSceneAsync(string sceneName)
        {
            if (string.IsNullOrEmpty(sceneName))
            {
                GameLog.Error("[SceneManager] UnloadSceneAsync: 场景名称不能为空");
                return;
            }

            if (sceneName == _currentScene &&
                UnityEngine.SceneManagement.SceneManager.sceneCount == 1)
            {
                GameLog.Warning($"[SceneManager] 不能卸载唯一场景: {sceneName}");
                return;
            }

            if (!_loadedScenes.TryGetValue(sceneName, out var handle))
            {
                GameLog.Warning($"[SceneManager] 非托管场景，尝试原生卸载: {sceneName}");
                var scene = UnityEngine.SceneManagement.SceneManager.GetSceneByName(sceneName);
                if (scene.isLoaded)
                {
                    var op = UnityEngine.SceneManagement.SceneManager.UnloadSceneAsync(scene);
                    while (!op.isDone) await UniTask.Yield();
                }
                return;
            }

            GameLog.Log($"[SceneManager] 卸载场景: {sceneName}");

            try
            {
                var unloadHandle = Addressables.UnloadSceneAsync(handle);
                await unloadHandle.Task;

                if (unloadHandle.Status != AsyncOperationStatus.Succeeded)
                {
                    GameLog.Error($"[SceneManager] 卸载失败: {sceneName}");
                    return;
                }

                _loadedScenes.Remove(sceneName);
                GameLog.Log($"[SceneManager] 卸载完成: {sceneName}");
                OnSceneUnloaded?.Invoke(sceneName);

                // 异步释放未使用资源，避免阻塞主线程
                await Resources.UnloadUnusedAssets();
                GC.Collect();
            }
            catch (Exception ex)
            {
                GameLog.Error($"[SceneManager] 卸载异常 [{sceneName}]: {ex.Message}");
                throw;
            }
        }

        // ═══════════════════════════════════════════════════════════════════════
        //  预加载 API
        // ═══════════════════════════════════════════════════════════════════════

        /// <summary>
        /// 后台预加载场景（不激活）。
        /// 后续调用 <see cref="SwitchToAsync"/> 或 <see cref="LoadSceneAsync"/>
        /// 时将自动复用，跳过网络下载环节。
        /// </summary>
        public async UniTask PreloadSceneAsync(string sceneName)
        {
            if (string.IsNullOrEmpty(sceneName))
            {
                GameLog.Error("[SceneManager] PreloadSceneAsync: 场景名称不能为空");
                return;
            }

            if (_preloadedScenes.ContainsKey(sceneName))
            {
                GameLog.Warning($"[SceneManager] 已预加载: {sceneName}");
                return;
            }

            GameLog.Log($"[SceneManager] 预加载开始: {sceneName}");

            try
            {
                // activateOnLoad = false：加载到内存但不激活
                var handle = Addressables.LoadSceneAsync(sceneName, LoadSceneMode.Additive, false);
                await handle.Task;

                if (handle.Status != AsyncOperationStatus.Succeeded)
                {
                    GameLog.Error($"[SceneManager] 预加载失败: {sceneName}");
                    return;
                }

                _preloadedScenes[sceneName] = handle;
                GameLog.Log($"[SceneManager] 预加载完成: {sceneName}");
            }
            catch (Exception ex)
            {
                GameLog.Error($"[SceneManager] 预加载异常 [{sceneName}]: {ex.Message}");
                throw;
            }
        }

        /// <summary>释放指定场景的预加载缓存（场景从内存中卸载）。</summary>
        public void CancelPreload(string sceneName)
        {
            if (_preloadedScenes.TryGetValue(sceneName, out var handle))
            {
                if (handle.IsValid()) Addressables.UnloadSceneAsync(handle);
                _preloadedScenes.Remove(sceneName);
                GameLog.Log($"[SceneManager] 取消预加载: {sceneName}");
            }
        }

        // ═══════════════════════════════════════════════════════════════════════
        //  查询工具
        // ═══════════════════════════════════════════════════════════════════════

        /// <summary>场景当前是否已激活（含非本管理器加载的场景）。</summary>
        public bool IsSceneLoaded(string sceneName)
        {
            var scene = UnityEngine.SceneManagement.SceneManager.GetSceneByName(sceneName);
            return scene.isLoaded;
        }

        /// <summary>场景是否处于预加载（未激活）状态。</summary>
        public bool IsScenePreloaded(string sceneName) =>
            _preloadedScenes.ContainsKey(sceneName);

        /// <summary>Unity 当前已激活的场景总数。</summary>
        public int GetLoadedSceneCount() =>
            UnityEngine.SceneManagement.SceneManager.sceneCount;

        /// <summary>获取指定索引的场景名，越界返回 null。</summary>
        public string GetSceneNameAt(int index)
        {
            if (index < 0 || index >= UnityEngine.SceneManagement.SceneManager.sceneCount)
                return null;
            return UnityEngine.SceneManagement.SceneManager.GetSceneAt(index).name;
        }

        // ═══════════════════════════════════════════════════════════════════════
        //  场景上下文
        // ═══════════════════════════════════════════════════════════════════════

        /// <summary>
        /// 注册场景上下文组件（由 SceneBase.Awake 自动调用）。
        /// 重复类型会以最后一次注册为准。
        /// </summary>
        public void RegisterContext(SceneBase context)
        {
            if (context == null)
            {
                GameLog.Error("[SceneManager] RegisterContext: context 为 null");
                return;
            }

            var type = context.GetType();
            _sceneContexts[type] = context;
            CurrentContext = context;
            GameLog.Log($"[SceneManager] 注册场景上下文: {type.Name}");
        }

        /// <summary>
        /// 注销场景上下文组件（由 SceneBase.OnDestroy 自动调用）。
        /// </summary>
        public void UnregisterContext(SceneBase context)
        {
            if (context == null) return;

            var type = context.GetType();
            if (_sceneContexts.TryGetValue(type, out var existing) && existing == context)
            {
                _sceneContexts.Remove(type);
                if (CurrentContext == context)
                    CurrentContext = null;
                GameLog.Log($"[SceneManager] 注销场景上下文: {type.Name}");
            }
        }

        /// <summary>
        /// 获取当前场景中指定类型的上下文组件。
        /// </summary>
        public T GetContext<T>() where T : SceneBase
        {
            var type = typeof(T);
            if (_sceneContexts.TryGetValue(type, out var context))
                return context as T;

            GameLog.Warning($"[SceneManager] 场景上下文未找到: {type.Name}");
            return null;
        }
    }
}
