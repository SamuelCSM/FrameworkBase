using System;
using Cysharp.Threading.Tasks;
using Framework.Core;
using UnityEngine;

namespace Framework
{
    /// <summary>
    /// 游戏阶段管理器 — 统一编排一次场景切换与阶段生命周期的过场流程。
    ///
    /// 职责：只做流程编排，不感知任何业务类型（UI、音频等），也不维护返回栈。
    /// 需要 Push / Back 语义时使用 <see cref="GameStageNavigationManager"/>。
    ///
    /// 执行流程：
    /// <list type="number">
    ///   <item>退出当前阶段（OnExitAsync）</item>
    ///   <item>旧场景上下文退出（OnSceneExitAsync）</item>
    ///   <item>过场淡出</item>
    ///   <item>加载场景（带进度回调）</item>
    ///   <item>新场景上下文进入（OnSceneEnterAsync）</item>
    ///   <item>进入新阶段（OnEnterAsync，过场遮罩仍显示，UI 打开不可见）</item>
    ///   <item>过场淡入</item>
    /// </list>
    ///
    /// <code>
    /// await GameEntry.StageManager.ChangeStageAsync(new BattleStage(levelId: 101));
    /// </code>
    /// </summary>
    public class GameStageManager : Core.FrameworkComponent<GameStageManager>
    {
        // ── 内部状态 ─────────────────────────────────────────────────────────

        private GameStage _currentStage;
        private bool _isChanging;

        // ── 属性 ──────────────────────────────────────────────────────────────

        /// <summary>当前活跃阶段。</summary>
        public GameStage CurrentStage => _currentStage;

        /// <summary>是否正在执行阶段切换。</summary>
        public bool IsChanging => _isChanging;

        // ── 生命周期 ──────────────────────────────────────────────────────────

        public override void OnInit()
        {
            GameLog.Log("[GameStageManager] 初始化完成");
        }

        public override void OnShutdown()
        {
            _currentStage = null;
            GameLog.Log("[GameStageManager] 已关闭");
        }

        // ── 阶段切换 ──────────────────────────────────────────────────────────

        /// <summary>切换游戏阶段。</summary>
        /// <param name="nextStage">目标阶段实例。</param>
        public async UniTask ChangeStageAsync(GameStage nextStage)
        {
            if (nextStage == null)
            {
                GameLog.Error("[GameStageManager] ChangeStageAsync: nextStage 为 null");
                return;
            }

            if (_isChanging)
            {
                GameLog.Warning("[GameStageManager] 阶段切换进行中，忽略");
                return;
            }

            _isChanging = true;
            GameLog.Log($"[GameStageManager] 阶段切换: {_currentStage?.GetType().Name ?? "null"} → {nextStage.GetType().Name}");

            var config = nextStage.GetTransition() ?? SceneTransitionConfig.Standard;
            string sceneAddress = nextStage.GetSceneAddress();
            bool needSceneSwitch = !string.IsNullOrEmpty(sceneAddress);

            var sceneMgr = GameEntry.Scene;

            try
            {
                // ① 退出当前阶段（由 Stage 自行清理，包括关闭 UI）
                if (_currentStage != null)
                    await _currentStage.OnExitAsync();

                // ② 旧场景上下文退出
                if (needSceneSwitch)
                {
                    var currentCtx = sceneMgr.CurrentContext;
                    if (currentCtx != null)
                        await currentCtx.OnSceneExitAsync();
                }

                // ③ 过场淡出
                var provider = sceneMgr.GetOrCreateTransitionProvider();
                await provider.BeginAsync(config.OverlayColor, config.FadeDuration);

                float startTime = Time.realtimeSinceStartup;

                // ④ 加载场景
                if (needSceneSwitch)
                {
                    await sceneMgr.LoadSceneAsync(
                        sceneAddress,
                        p =>
                        {
                            float mapped = p * 0.9f;
                            provider.OnProgress(mapped);
                            config.OnProgress?.Invoke(mapped);
                        },
                        config.LoadMode);

                    // 补齐最短停留时间
                    float elapsed = Time.realtimeSinceStartup - startTime;
                    float remaining = config.MinLoadingDuration - elapsed;
                    if (remaining > 0f)
                        await UniTask.Delay(TimeSpan.FromSeconds(remaining), ignoreTimeScale: true);

                    // ⑤ 新场景上下文进入
                    var newCtx = sceneMgr.CurrentContext;
                    if (newCtx != null)
                        await newCtx.OnSceneEnterAsync();
                }

                provider.OnProgress(1f);
                config.OnProgress?.Invoke(1f);

                // ⑥ 进入新阶段（遮罩仍显示，UI 打开等操作在幕后完成）
                _currentStage = nextStage;
                await nextStage.OnEnterAsync();

                // ⑦ 过场淡入
                await provider.EndAsync(config.FadeDuration);

                GameLog.Log($"[GameStageManager] 阶段切换完成: {nextStage.GetType().Name}");
            }
            catch (Exception ex)
            {
                GameLog.Error($"[GameStageManager] 阶段切换异常 [{nextStage.GetType().Name}]: {ex.Message}");
                var provider = sceneMgr?.GetOrCreateTransitionProvider();
                provider?.ForceHide();
                throw;
            }
            finally
            {
                _isChanging = false;
            }
        }
    }
}
