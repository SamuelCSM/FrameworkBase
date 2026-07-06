using System.Threading;
using Cysharp.Threading.Tasks;
using UnityEngine;

namespace Framework
{
    /// <summary>
    /// UI 过渡动画执行器（纯 UniTask，无需 DOTween）
    ///
    /// 动画全部基于 CanvasGroup（Alpha + Interactable）+ RectTransform，
    /// 利用 UniTask.Yield 在 Update 阶段逐帧插值。
    ///
    /// 缓动函数：
    ///   Fade / Slide  → EaseOutCubic（快进慢出，流畅）
    ///   ScalePop 开  → EaseOutBack（带微弹，活泼）
    ///   ScalePop 关  → EaseInCubic（快速收缩，干净）
    /// </summary>
    public static class UIAnimator
    {
        // ── 公共入口 ──────────────────────────────────────────────────────────

        /// <summary>播放打开动画（GameObject 必须已激活）</summary>
        public static async UniTask PlayOpenAsync(
            GameObject go,
            UIAnimationConfig config,
            CancellationToken ct = default)
        {
            if (go == null || config == null || config.OpenAnim == UIAnimationType.None)
                return;

            var cg   = GetOrAddCanvasGroup(go);
            var rect = go.GetComponent<RectTransform>();

            // 设置初始状态（打开前的隐藏状态）
            SetOpenInitialState(cg, rect, config.OpenAnim);

            await RunAnimation(cg, rect, config.OpenAnim, true, config.Duration, ct);

            // 保证结束时状态正确
            cg.alpha          = 1f;
            cg.interactable   = true;
            cg.blocksRaycasts = true;
        }

        /// <summary>播放关闭动画（结束后由调用方负责 SetActive(false)）</summary>
        public static async UniTask PlayCloseAsync(
            GameObject go,
            UIAnimationConfig config,
            CancellationToken ct = default)
        {
            if (go == null || config == null || config.CloseAnim == UIAnimationType.None)
                return;

            var cg   = GetOrAddCanvasGroup(go);
            var rect = go.GetComponent<RectTransform>();

            // 关闭期间禁用交互，防止动画过程中再次点击
            cg.interactable   = false;
            cg.blocksRaycasts = false;

            await RunAnimation(cg, rect, config.CloseAnim, false, config.Duration, ct);
        }

        // ── 初始状态设置 ──────────────────────────────────────────────────────

        /// <summary>
        /// 在打开动画第一帧渲染之前，将 UI 设为"不可见的初始状态"。
        /// 例如 Fade 将 alpha 置 0，ScalePop 将 scale 缩小到 0.85，Slide 将位置偏移到屏幕外。
        /// 必须在 SetActive(true) 之后、第一帧渲染之前调用，防止玩家看到一帧完整状态。
        /// </summary>
        private static void SetOpenInitialState(CanvasGroup cg, RectTransform rect, UIAnimationType anim)
        {
            switch (anim)
            {
                case UIAnimationType.Fade:
                    cg.alpha = 0f;
                    break;

                case UIAnimationType.ScalePop:
                    cg.alpha = 0f;
                    if (rect) rect.localScale = Vector3.one * 0.85f;
                    break;

                case UIAnimationType.SlideFromBottom:
                    cg.alpha = 0f;
                    if (rect) rect.anchoredPosition += new Vector2(0, -SlideOffset(rect, false));
                    break;

                case UIAnimationType.SlideFromTop:
                    cg.alpha = 0f;
                    if (rect) rect.anchoredPosition += new Vector2(0, SlideOffset(rect, false));
                    break;

                case UIAnimationType.SlideFromLeft:
                    cg.alpha = 0f;
                    if (rect) rect.anchoredPosition += new Vector2(-SlideOffset(rect, true), 0);
                    break;

                case UIAnimationType.SlideFromRight:
                    cg.alpha = 0f;
                    if (rect) rect.anchoredPosition += new Vector2(SlideOffset(rect, true), 0);
                    break;
            }

            cg.interactable   = false;
            cg.blocksRaycasts = false;
        }

        // ── 动画运行器 ────────────────────────────────────────────────────────

        /// <summary>
        /// 逐帧插值循环。每帧在 PlayerLoop.Update 阶段执行一次，直到 duration 耗尽或 ct 取消。
        /// <para>originPos：Slide 动画需要记录动画开始时的原始位置，以此为基准做偏移，
        /// 避免因外部布局变化导致位置累加漂移。</para>
        /// </summary>
        /// <param name="opening">true = 打开（0→1），false = 关闭（1→0）</param>
        private static async UniTask RunAnimation(
            CanvasGroup cg,
            RectTransform rect,
            UIAnimationType anim,
            bool opening,
            float duration,
            CancellationToken ct)
        {
            // 记录动画开始时的原始位置，Slide 系列动画以此为目标位
            Vector2 originPos = rect != null ? rect.anchoredPosition : Vector2.zero;
            float elapsed = 0f;

            while (elapsed < duration)
            {
                if (ct.IsCancellationRequested) break;

                elapsed += Time.deltaTime;
                float raw = Mathf.Clamp01(elapsed / duration);
                float t   = EaseFor(anim, opening, raw);

                ApplyFrame(cg, rect, anim, opening, t, originPos, duration);

                await UniTask.Yield(PlayerLoopTiming.Update, ct);
            }
        }

        /// <summary>
        /// 将当前插值进度 t 应用到 CanvasGroup 和 RectTransform 上。
        /// <para>t 始终是经过缓动函数处理后的 [0,1] 值，表示"当前动画进度"：
        /// 打开时 t=0 为完全隐藏，t=1 为完全显示；关闭方向相反。</para>
        /// <para>originPos 是动画开始时记录的 anchoredPosition，用于 Slide 系列还原目标位置。</para>
        /// </summary>
        private static void ApplyFrame(
            CanvasGroup cg,
            RectTransform rect,
            UIAnimationType anim,
            bool opening,
            float t,
            Vector2 originPos,
            float duration)
        {
            // opening: t 趋近 1 → 逐渐显现；closing: t 趋近 1 → 逐渐消失
            float alpha = opening ? t : (1f - t);

            switch (anim)
            {
                case UIAnimationType.Fade:
                    cg.alpha = alpha;
                    break;

                case UIAnimationType.ScalePop:
                    cg.alpha = alpha;
                    if (rect)
                    {
                        float scale = opening
                            ? Mathf.Lerp(0.85f, 1f, t)
                            : Mathf.Lerp(1f, 0.85f, t);
                        rect.localScale = Vector3.one * scale;
                    }
                    break;

                case UIAnimationType.SlideFromBottom:
                    cg.alpha = alpha;
                    if (rect)
                    {
                        float offset = SlideOffset(rect, false);
                        float yOffset = opening
                            ? Mathf.Lerp(-offset, 0, t)
                            : Mathf.Lerp(0, -offset, t);
                        rect.anchoredPosition = new Vector2(originPos.x, originPos.y + yOffset);
                    }
                    break;

                case UIAnimationType.SlideFromTop:
                    cg.alpha = alpha;
                    if (rect)
                    {
                        float offset = SlideOffset(rect, false);
                        float yOffset = opening
                            ? Mathf.Lerp(offset, 0, t)
                            : Mathf.Lerp(0, offset, t);
                        rect.anchoredPosition = new Vector2(originPos.x, originPos.y + yOffset);
                    }
                    break;

                case UIAnimationType.SlideFromLeft:
                    cg.alpha = alpha;
                    if (rect)
                    {
                        float offset = SlideOffset(rect, true);
                        float xOffset = opening
                            ? Mathf.Lerp(-offset, 0, t)
                            : Mathf.Lerp(0, -offset, t);
                        rect.anchoredPosition = new Vector2(originPos.x + xOffset, originPos.y);
                    }
                    break;

                case UIAnimationType.SlideFromRight:
                    cg.alpha = alpha;
                    if (rect)
                    {
                        float offset = SlideOffset(rect, true);
                        float xOffset = opening
                            ? Mathf.Lerp(offset, 0, t)
                            : Mathf.Lerp(0, offset, t);
                        rect.anchoredPosition = new Vector2(originPos.x + xOffset, originPos.y);
                    }
                    break;
            }
        }

        // ── 缓动函数 ──────────────────────────────────────────────────────────

        /// <summary>
        /// 根据动画类型和方向选择合适的缓动函数，对原始线性进度 raw[0,1] 做映射。
        /// 不同动画使用不同缓动以获得各自最佳手感：
        ///   ScalePop 打开  → EaseOutBack（超出后回弹，活泼）
        ///   ScalePop 关闭  → EaseInCubic（快速收缩，干净利落）
        ///   Slide 打开     → EaseOutCubic（快进慢出，稳定）
        ///   Slide 关闭     → EaseInCubic（慢起快收）
        ///   Fade           → EaseInOutCubic（两头慢中间快，柔和）
        /// </summary>
        private static float EaseFor(UIAnimationType anim, bool opening, float t)
        {
            switch (anim)
            {
                case UIAnimationType.ScalePop:
                    return opening ? EaseOutBack(t) : EaseInCubic(t);

                case UIAnimationType.SlideFromBottom:
                case UIAnimationType.SlideFromTop:
                case UIAnimationType.SlideFromLeft:
                case UIAnimationType.SlideFromRight:
                    return opening ? EaseOutCubic(t) : EaseInCubic(t);

                default: // Fade
                    return EaseInOutCubic(t);
            }
        }

        /// <summary>快进慢出</summary>
        private static float EaseOutCubic(float t) => 1f - Mathf.Pow(1f - t, 3f);

        /// <summary>慢进快出</summary>
        private static float EaseInCubic(float t) => t * t * t;

        /// <summary>慢进慢出</summary>
        private static float EaseInOutCubic(float t)
            => t < 0.5f ? 4f * t * t * t : 1f - Mathf.Pow(-2f * t + 2f, 3f) / 2f;

        /// <summary>超出后回弹（ScalePop 开场专用）</summary>
        private static float EaseOutBack(float t)
        {
            const float c1 = 1.70158f;
            const float c3 = c1 + 1f;
            return 1f + c3 * Mathf.Pow(t - 1f, 3f) + c1 * Mathf.Pow(t - 1f, 2f);
        }

        // ── 工具方法 ──────────────────────────────────────────────────────────

        private static CanvasGroup GetOrAddCanvasGroup(GameObject go)
        {
            var cg = go.GetComponent<CanvasGroup>();
            return cg != null ? cg : go.AddComponent<CanvasGroup>();
        }

        /// <summary>
        /// 计算滑动偏移量：优先用 RectTransform 自身尺寸，fallback 到屏幕尺寸。
        /// </summary>
        private static float SlideOffset(RectTransform rect, bool horizontal)
        {
            if (rect != null)
            {
                float size = horizontal ? rect.rect.width : rect.rect.height;
                if (size > 1f) return size;
            }
            return horizontal ? Screen.width : Screen.height;
        }
    }
}
