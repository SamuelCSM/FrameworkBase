using System.Collections.Generic;
using Cysharp.Threading.Tasks;
using Framework.Core;

namespace Framework
{
    /// <summary>
    /// 游戏阶段导航管理器。
    ///
    /// <para>
    /// <see cref="GameStageManager"/> 只负责执行一次阶段切换；
    /// 本类负责维护阶段返回栈，提供 Push / Back 语义，适合接入安卓返回键。
    /// </para>
    ///
    /// <code>
    /// await GameEntry.StageNavigation.PushStageAsync(new MainStage());
    /// await GameEntry.StageNavigation.PushStageAsync(new BattleStage(data));
    /// await GameEntry.StageNavigation.GoBackAsync();
    /// </code>
    /// </summary>
    public class GameStageNavigationManager : Core.FrameworkComponent<GameStageNavigationManager>
    {
        // ── 内部状态 ─────────────────────────────────────────────────────────

        /// <summary>阶段返回栈。只保存可重建业务状态，不保存 Unity 场景对象。</summary>
        private readonly Stack<GameStage> _backStack = new Stack<GameStage>();

        private bool _isNavigating;

        // ── 属性 ──────────────────────────────────────────────────────────────

        /// <summary>是否正在执行阶段导航。</summary>
        public bool IsNavigating => _isNavigating;

        /// <summary>阶段返回栈数量。</summary>
        public int BackStackCount => _backStack.Count;

        /// <summary>当前是否存在可返回的阶段。</summary>
        public bool CanGoBackStage => _backStack.Count > 0;

        // ── 生命周期 ─────────────────────────────────────────────────────────

        /// <summary>初始化阶段导航管理器。</summary>
        public override void OnInit()
        {
            GameLog.Log("[GameStageNavigationManager] 初始化完成");
        }

        /// <summary>关闭阶段导航管理器并清空返回栈。</summary>
        public override void OnShutdown()
        {
            _backStack.Clear();
            GameLog.Log("[GameStageNavigationManager] 已关闭");
        }

        // ── 阶段导航 ─────────────────────────────────────────────────────────

        /// <summary>
        /// 替换当前阶段。
        /// <para>用于登录成功进主界面、回主城等不可回退跳转。</para>
        /// </summary>
        /// <param name="nextStage">目标阶段。</param>
        /// <param name="clearBackStack">是否清空阶段返回栈。</param>
        public async UniTask ReplaceStageAsync(GameStage nextStage, bool clearBackStack = true)
        {
            if (!CanChangeStage(nextStage, nameof(ReplaceStageAsync)))
                return;

            if (clearBackStack)
                _backStack.Clear();

            await ChangeStageInternalAsync(nextStage);
        }

        /// <summary>
        /// 推入一个可返回的新阶段。
        /// <para>用于从场景 A 进入场景 B，并期望安卓返回键回到场景 A 的情形。</para>
        /// </summary>
        /// <param name="nextStage">目标阶段。</param>
        public async UniTask PushStageAsync(GameStage nextStage)
        {
            if (!CanChangeStage(nextStage, nameof(PushStageAsync)))
                return;

            var currentStage = GameStageManager.Instance.CurrentStage;
            if (currentStage != null)
                _backStack.Push(currentStage);

            try
            {
                await ChangeStageInternalAsync(nextStage);
            }
            catch
            {
                if (currentStage != null && _backStack.Count > 0 && ReferenceEquals(_backStack.Peek(), currentStage))
                    _backStack.Pop();
                throw;
            }
        }

        /// <summary>
        /// 执行返回。
        /// <para>优先关闭当前 UI 栈顶窗口；若没有可关闭的窗口，再回退到上一个阶段。</para>
        /// </summary>
        /// <returns>是否成功处理返回。</returns>
        public async UniTask<bool> GoBackAsync()
        {
            if (_isNavigating)
            {
                GameLog.Warning("[GameStageNavigationManager] 阶段导航进行中，忽略返回");
                return false;
            }

            var uiManager = GameEntry.UI;
            if (uiManager != null && uiManager.GetStackDepth() > 1)
            {
                await uiManager.GoBackAsync();
                return true;
            }

            return await PopStageAsync();
        }

        /// <summary>
        /// 直接回退到上一个阶段，不处理当前 UI 栈。
        /// </summary>
        /// <returns>是否成功回退阶段。</returns>
        public async UniTask<bool> PopStageAsync()
        {
            if (!CanStartNavigation(nameof(PopStageAsync)))
                return false;

            if (_backStack.Count == 0)
            {
                GameLog.Warning("[GameStageNavigationManager] 阶段返回栈为空");
                return false;
            }

            var previousStage = _backStack.Pop();
            try
            {
                await ChangeStageInternalAsync(previousStage);
                return true;
            }
            catch
            {
                _backStack.Push(previousStage);
                throw;
            }
        }

        /// <summary>清空阶段返回栈。</summary>
        public void ClearBackStack()
        {
            _backStack.Clear();
        }

        // ── 内部实现 ─────────────────────────────────────────────────────────

        private bool CanChangeStage(GameStage nextStage, string apiName)
        {
            if (nextStage == null)
            {
                GameLog.Error($"[GameStageNavigationManager] {apiName}: nextStage 为 null");
                return false;
            }

            return CanStartNavigation(apiName);
        }

        private bool CanStartNavigation(string apiName)
        {
            if (_isNavigating)
            {
                GameLog.Warning($"[GameStageNavigationManager] {apiName}: 阶段导航进行中，忽略");
                return false;
            }

            if (GameStageManager.Instance == null)
            {
                GameLog.Error($"[GameStageNavigationManager] {apiName}: StageManager 未初始化");
                return false;
            }

            return true;
        }

        /// <summary>通过 GameStageManager 执行实际阶段切换，并统一防止并发导航。</summary>
        private async UniTask ChangeStageInternalAsync(GameStage nextStage)
        {
            _isNavigating = true;
            try
            {
                await GameStageManager.Instance.ChangeStageAsync(nextStage);
            }
            finally
            {
                _isNavigating = false;
            }
        }
    }
}
