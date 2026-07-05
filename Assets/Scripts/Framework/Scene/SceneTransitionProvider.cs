using Cysharp.Threading.Tasks;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

namespace Framework
{
    // ═══════════════════════════════════════════════════════════════════════════
    //  SceneTransitionConfig
    // ═══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// 场景切换的过场参数。
    /// 传给 <see cref="SceneManager.SwitchToAsync"/> 来控制动画时长、最短等待、进度回调和遮罩颜色。
    /// </summary>
    public sealed class SceneTransitionConfig
    {
        /// <summary>淡入 / 淡出动画时长（秒）。设为 0 可跳过动画直接切换。</summary>
        public float FadeDuration { get; set; } = 0.3f;

        /// <summary>
        /// 最短 Loading 停留时间（秒）。
        /// 当实际加载耗时 &lt; 此值时会补齐等待，避免快速设备上出现"进度条闪过"的体验问题。
        /// </summary>
        public float MinLoadingDuration { get; set; } = 0f;

        /// <summary>加载进度回调（0–1），在 SwitchToAsync 执行期间持续触发。</summary>
        public System.Action<float> OnProgress { get; set; }

        /// <summary>过场遮罩颜色，默认黑色。</summary>
        public Color OverlayColor { get; set; } = Color.black;

        /// <summary>场景加载模式：Single = 替换当前场景，Additive = 叠加场景。</summary>
        public LoadSceneMode LoadMode { get; set; } = LoadSceneMode.Single;

        // ── 预设 ──────────────────────────────────────────────────────────────

        /// <summary>快速切换：0.2s 淡入淡出，无最短等待。</summary>
        public static SceneTransitionConfig Fast =>
            new SceneTransitionConfig { FadeDuration = 0.2f };

        /// <summary>标准切换：0.3s 淡入淡出，0.3s 最短停留（默认）。</summary>
        public static SceneTransitionConfig Standard =>
            new SceneTransitionConfig { FadeDuration = 0.3f, MinLoadingDuration = 0.3f };

        /// <summary>无动画直接切换（测试 / 开发模式）。</summary>
        public static SceneTransitionConfig Instant =>
            new SceneTransitionConfig { FadeDuration = 0f };
    }

    // ═══════════════════════════════════════════════════════════════════════════
    //  ISceneTransitionProvider
    // ═══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// 场景过场 UI 提供者接口。
    ///
    /// <para>
    /// SceneManager 仅通过此接口驱动过场 UI，不感知具体实现。
    /// 这样可以完全自定义过场效果（例如用 UIManager 打开一个带动画的窗口），
    /// 而无需修改 SceneManager 本身。
    /// </para>
    ///
    /// <para>用法：</para>
    /// <code>
    /// // 使用框架内置的黑屏 + 进度条过场（默认，无需配置）
    /// // SceneManager 在未注入时自动使用 BuiltInFadeTransition
    ///
    /// // 注入自定义过场（如调用 UIManager 显示自己的 Loading 窗口）
    /// GameEntry.Scene.SetTransitionProvider(new MyCustomTransition());
    /// </code>
    /// </summary>
    public interface ISceneTransitionProvider
    {
        /// <summary>
        /// 加载开始前调用（播放进入动画 / 显示 Loading 界面）。
        /// </summary>
        /// <param name="color">遮罩底色（如果实现使用了遮罩）。</param>
        /// <param name="fadeDuration">淡入时长（秒），0 表示跳过动画。</param>
        UniTask BeginAsync(Color color, float fadeDuration);

        /// <summary>
        /// 加载进度更新，在加载过程中持续调用。
        /// </summary>
        /// <param name="progress">0–1 的归一化进度。</param>
        void OnProgress(float progress);

        /// <summary>
        /// 加载完成后调用（播放退出动画 / 隐藏 Loading 界面）。
        /// </summary>
        /// <param name="fadeDuration">淡出时长（秒），0 表示跳过动画。</param>
        UniTask EndAsync(float fadeDuration);

        /// <summary>
        /// 异常时立即强制隐藏，保证不遮住画面。
        /// </summary>
        void ForceHide();
    }

    // ═══════════════════════════════════════════════════════════════════════════
    //  BuiltInFadeTransition（框架默认实现）
    // ═══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// 框架内置的过场实现：黑屏遮罩 + 底部进度条。
    ///
    /// <para>
    /// 直接通过 <c>new GameObject</c> 创建 Canvas，而非走 UIManager，
    /// 原因是：过场遮罩需要在场景卸载期间保持稳定存在，且在 UIBootstrap
    /// 还未完成初始化时也需要可用；Canvas 使用 <c>ScreenSpaceOverlay</c> +
    /// SortingOrder = 9999，永远位于所有 UI 之上。
    /// </para>
    ///
    /// <para>
    /// 如需自定义（使用 UIManager 的窗口系统），实现
    /// <see cref="ISceneTransitionProvider"/> 并调用
    /// <see cref="SceneManager.SetTransitionProvider"/> 即可替换。
    /// </para>
    /// </summary>
    public sealed class BuiltInFadeTransition : ISceneTransitionProvider
    {
        // ── UI 节点引用 ───────────────────────────────────────────────────────

        private GameObject  _root;
        private CanvasGroup _canvasGroup;
        private Image       _bgImage;
        private Image       _progressFill;

        // ── ISceneTransitionProvider ──────────────────────────────────────────

        public async UniTask BeginAsync(Color color, float fadeDuration)
        {
            EnsureCreated(color);
            _canvasGroup.blocksRaycasts = true;
            _progressFill.fillAmount    = 0f;
            await FadeAsync(0f, 1f, fadeDuration);
        }

        public void OnProgress(float progress)
        {
            if (_progressFill != null)
                _progressFill.fillAmount = Mathf.Clamp01(progress);
        }

        public async UniTask EndAsync(float fadeDuration)
        {
            if (_root == null) return;
            _progressFill.fillAmount = 1f;
            await FadeAsync(1f, 0f, fadeDuration);
            _canvasGroup.blocksRaycasts = false;
        }

        public void ForceHide()
        {
            if (_canvasGroup == null) return;
            _canvasGroup.alpha          = 0f;
            _canvasGroup.blocksRaycasts = false;
        }

        // ── 内部实现 ──────────────────────────────────────────────────────────

        /// <summary>
        /// 懒加载创建 Canvas 层级（仅首次调用时执行）。
        /// 挂载 DontDestroyOnLoad 确保跨场景存活。
        /// </summary>
        private void EnsureCreated(Color bgColor)
        {
            if (_root != null)
            {
                _bgImage.color = bgColor;
                return;
            }

            // ── 根节点 ──────────────────────────────────────────────────────
            _root = new GameObject("[SceneTransitionOverlay]");
            UnityEngine.Object.DontDestroyOnLoad(_root);

            var canvas = _root.AddComponent<Canvas>();
            canvas.renderMode   = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = 9999;

            // 屏蔽点击穿透（过场期间不允许操作底层 UI）
            _root.AddComponent<GraphicRaycaster>();

            _canvasGroup                  = _root.AddComponent<CanvasGroup>();
            _canvasGroup.alpha            = 0f;
            _canvasGroup.blocksRaycasts   = false;
            _canvasGroup.interactable     = false;

            // ── 黑屏背景 ────────────────────────────────────────────────────
            var bgGO = CreateFullStretch("Background", _root.transform);
            _bgImage               = bgGO.AddComponent<Image>();
            _bgImage.color         = bgColor;
            _bgImage.raycastTarget = true;

            // ── 进度条容器（贴屏幕底部，全宽 6px 高）────────────────────────
            var barContainerGO = new GameObject("ProgressBarContainer");
            barContainerGO.transform.SetParent(_root.transform, false);
            var barContainerRT          = barContainerGO.AddComponent<RectTransform>();
            barContainerRT.anchorMin    = new Vector2(0f, 0f);
            barContainerRT.anchorMax    = new Vector2(1f, 0f);
            barContainerRT.pivot        = new Vector2(0.5f, 0f);
            barContainerRT.sizeDelta    = new Vector2(0f, 6f);
            barContainerRT.anchoredPosition = Vector2.zero;

            // 进度条背景（深灰）
            var barBgGO      = CreateFullStretch("ProgressBarBg", barContainerGO.transform);
            var barBg        = barBgGO.AddComponent<Image>();
            barBg.color      = new Color(0.15f, 0.15f, 0.15f, 1f);
            barBg.raycastTarget = false;

            // 进度条填充（白色，Filled 模式）
            var barFillGO        = CreateFullStretch("ProgressBarFill", barContainerGO.transform);
            _progressFill        = barFillGO.AddComponent<Image>();
            _progressFill.color  = Color.white;
            _progressFill.type   = Image.Type.Filled;
            _progressFill.fillMethod = Image.FillMethod.Horizontal;
            _progressFill.fillOrigin = (int)Image.OriginHorizontal.Left;
            _progressFill.fillAmount = 0f;
            _progressFill.raycastTarget = false;
        }

        /// <summary>创建一个四角锚点全拉伸的子节点（无需 Image，仅 RectTransform）。</summary>
        private static GameObject CreateFullStretch(string name, Transform parent)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            var rt        = go.AddComponent<RectTransform>();
            rt.anchorMin  = Vector2.zero;
            rt.anchorMax  = Vector2.one;
            rt.offsetMin  = Vector2.zero;
            rt.offsetMax  = Vector2.zero;
            return go;
        }

        /// <summary>
        /// 使用 <see cref="Time.unscaledDeltaTime"/> 淡入/淡出，不受 timeScale 影响。
        /// </summary>
        private async UniTask FadeAsync(float from, float to, float duration)
        {
            _canvasGroup.alpha = from;
            if (duration <= 0f)
            {
                _canvasGroup.alpha = to;
                return;
            }

            float elapsed = 0f;
            while (elapsed < duration)
            {
                elapsed             += Time.unscaledDeltaTime;
                _canvasGroup.alpha   = Mathf.Lerp(from, to, elapsed / duration);
                await UniTask.Yield(PlayerLoopTiming.Update);
            }
            _canvasGroup.alpha = to;
        }
    }
}
