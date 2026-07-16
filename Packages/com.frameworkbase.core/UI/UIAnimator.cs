using System.Threading;
using Cysharp.Threading.Tasks;
using PrimeTween;
using UnityEngine;

namespace Framework
{
    /// <summary>
    /// UI 过渡动画执行器（基于 PrimeTween）。
    ///
    /// 动画全部基于 CanvasGroup（Alpha + Interactable）+ RectTransform，经 PrimeTween 的
    /// Tween / Sequence 驱动（零 GC、可被 CancellationToken 取消）。
    ///
    /// 缓动函数（沿用手写版的手感取舍）：
    ///   Fade          → InOutCubic（两头慢中间快，柔和）
    ///   Slide 打开    → OutCubic（快进慢出，稳定）
    ///   Slide 关闭    → InCubic（慢起快收）
    ///   ScalePop 打开 → 缩放 OutBack（超出后回弹，活泼）+ 透明 OutCubic
    ///   ScalePop 关闭 → InCubic（快速收缩，干净利落）
    /// </summary>
    public static class UIAnimator
    {
        /// <summary>
        /// UI 过渡是否使用非缩放时间（<c>Time.unscaledDeltaTime</c>）。
        /// <para>默认 false（跟随 <c>Time.timeScale</c>，与历史行为一致）。若游戏会在 <c>timeScale=0</c> 时
        /// 弹窗（如暂停菜单），置 true 可避免过渡因时停而卡住。</para>
        /// </summary>
        public static bool UseUnscaledTime { get; set; } = false;

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

            // 目标位置须在设初始态（会写入偏移）之前记录，作为 Slide 的归位目标；
            // 以动画开始时的原始位置为基准，避免因外部布局变化导致位置累加漂移。
            Vector2 originPos = rect != null ? rect.anchoredPosition : Vector2.zero;

            SetOpenInitialState(cg, rect, config.OpenAnim);

            Sequence seq = BuildOpenSequence(cg, rect, config.OpenAnim, config.Duration, originPos);
            await seq.ToUniTask(ct);

            // 收尾终态（含取消路径）：确保可见可交互、缩放/位置归位。
            cg.alpha          = 1f;
            cg.interactable   = true;
            cg.blocksRaycasts = true;
            if (rect != null)
            {
                rect.localScale       = Vector3.one;
                rect.anchoredPosition = originPos;
            }
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

            // 关闭期间禁用交互，防止动画过程中再次点击。
            cg.interactable   = false;
            cg.blocksRaycasts = false;

            Vector2 originPos = rect != null ? rect.anchoredPosition : Vector2.zero;

            Sequence seq = BuildCloseSequence(cg, rect, config.CloseAnim, config.Duration, originPos);
            await seq.ToUniTask(ct);
        }

        // ── 序列构建 ──────────────────────────────────────────────────────────

        /// <summary>
        /// 构建打开序列：透明度 0→1，并按动画类型并行叠加缩放/位移归位。
        /// 各子补间用 Group 并行，起止同时，与手写版逐帧同步推进的观感一致。
        /// </summary>
        private static Sequence BuildOpenSequence(
            CanvasGroup cg, RectTransform rect, UIAnimationType anim, float duration, Vector2 originPos)
        {
            bool u = UseUnscaledTime;
            switch (anim)
            {
                case UIAnimationType.ScalePop:
                {
                    Sequence s = Sequence.Create(useUnscaledTime: u)
                        .Group(Tween.Alpha(cg, 1f, duration, Ease.OutCubic, useUnscaledTime: u));
                    if (rect != null)
                        s = s.Group(Tween.Scale(rect, 1f, duration, Ease.OutBack, useUnscaledTime: u));
                    return s;
                }

                case UIAnimationType.SlideFromBottom:
                case UIAnimationType.SlideFromTop:
                case UIAnimationType.SlideFromLeft:
                case UIAnimationType.SlideFromRight:
                {
                    // 初始态已把 rect 推到屏外（origin + 偏移），此处动回 origin。
                    Sequence s = Sequence.Create(useUnscaledTime: u)
                        .Group(Tween.Alpha(cg, 1f, duration, Ease.OutCubic, useUnscaledTime: u));
                    if (rect != null)
                        s = s.Group(Tween.UIAnchoredPosition(rect, originPos, duration, Ease.OutCubic, useUnscaledTime: u));
                    return s;
                }

                default: // Fade
                    return Sequence.Create(useUnscaledTime: u)
                        .Group(Tween.Alpha(cg, 1f, duration, Ease.InOutCubic, useUnscaledTime: u));
            }
        }

        /// <summary>
        /// 构建关闭序列：透明度 1→0，并按动画类型并行叠加缩放收缩/位移出屏。
        /// Slide 关闭以当前 origin 为基准动到「origin + 出屏偏移」（与打开方向相反）。
        /// </summary>
        private static Sequence BuildCloseSequence(
            CanvasGroup cg, RectTransform rect, UIAnimationType anim, float duration, Vector2 originPos)
        {
            bool u = UseUnscaledTime;
            switch (anim)
            {
                case UIAnimationType.ScalePop:
                {
                    Sequence s = Sequence.Create(useUnscaledTime: u)
                        .Group(Tween.Alpha(cg, 0f, duration, Ease.InCubic, useUnscaledTime: u));
                    if (rect != null)
                        s = s.Group(Tween.Scale(rect, 0.85f, duration, Ease.InCubic, useUnscaledTime: u));
                    return s;
                }

                case UIAnimationType.SlideFromBottom:
                case UIAnimationType.SlideFromTop:
                case UIAnimationType.SlideFromLeft:
                case UIAnimationType.SlideFromRight:
                {
                    Sequence s = Sequence.Create(useUnscaledTime: u)
                        .Group(Tween.Alpha(cg, 0f, duration, Ease.InCubic, useUnscaledTime: u));
                    if (rect != null)
                    {
                        Vector2 target = originPos + SlideVector(rect, anim);
                        s = s.Group(Tween.UIAnchoredPosition(rect, target, duration, Ease.InCubic, useUnscaledTime: u));
                    }
                    return s;
                }

                default: // Fade
                    return Sequence.Create(useUnscaledTime: u)
                        .Group(Tween.Alpha(cg, 0f, duration, Ease.InOutCubic, useUnscaledTime: u));
            }
        }

        // ── 初始状态与工具 ────────────────────────────────────────────────────

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
                case UIAnimationType.SlideFromTop:
                case UIAnimationType.SlideFromLeft:
                case UIAnimationType.SlideFromRight:
                    cg.alpha = 0f;
                    if (rect) rect.anchoredPosition += SlideVector(rect, anim);
                    break;
            }

            cg.interactable   = false;
            cg.blocksRaycasts = false;
        }

        /// <summary>
        /// Slide 系列的屏外偏移向量：打开时初始态在此偏移处、关闭时动到此偏移处。
        /// 优先用 RectTransform 自身尺寸，fallback 到屏幕尺寸。
        /// </summary>
        private static Vector2 SlideVector(RectTransform rect, UIAnimationType anim)
        {
            switch (anim)
            {
                case UIAnimationType.SlideFromBottom: return new Vector2(0, -SlideOffset(rect, false));
                case UIAnimationType.SlideFromTop:    return new Vector2(0,  SlideOffset(rect, false));
                case UIAnimationType.SlideFromLeft:   return new Vector2(-SlideOffset(rect, true), 0);
                case UIAnimationType.SlideFromRight:  return new Vector2( SlideOffset(rect, true), 0);
                default:                              return Vector2.zero;
            }
        }

        /// <summary>计算滑动偏移量：优先用 RectTransform 自身尺寸，fallback 到屏幕尺寸。</summary>
        private static float SlideOffset(RectTransform rect, bool horizontal)
        {
            if (rect != null)
            {
                float size = horizontal ? rect.rect.width : rect.rect.height;
                if (size > 1f) return size;
            }
            return horizontal ? Screen.width : Screen.height;
        }

        private static CanvasGroup GetOrAddCanvasGroup(GameObject go)
        {
            var cg = go.GetComponent<CanvasGroup>();
            return cg != null ? cg : go.AddComponent<CanvasGroup>();
        }
    }
}
