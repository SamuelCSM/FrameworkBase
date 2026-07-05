namespace Framework
{
    /// <summary>
    /// UI 过渡动画类型
    /// </summary>
    public enum UIAnimationType
    {
        /// <summary>无动画，瞬间显示/隐藏</summary>
        None,

        /// <summary>淡入淡出（Alpha 0↔1）</summary>
        Fade,

        /// <summary>缩放弹出（Scale 0.85→1 + Fade，适合弹窗）</summary>
        ScalePop,

        /// <summary>从底部滑入（适合面板、抽屉）</summary>
        SlideFromBottom,

        /// <summary>从顶部滑入</summary>
        SlideFromTop,

        /// <summary>从左侧滑入</summary>
        SlideFromLeft,

        /// <summary>从右侧滑入</summary>
        SlideFromRight,
    }

    /// <summary>
    /// UI 过渡动画配置。
    ///
    /// 使用预设（推荐）：
    ///   UIAnimationConfig.Fade()       — 淡入淡出
    ///   UIAnimationConfig.ScalePop()   — 弹窗缩放弹出
    ///   UIAnimationConfig.SlideUp()    — 面板从底部滑入
    ///   UIAnimationConfig.None         — 无动画
    ///
    /// 自定义：
    ///   new UIAnimationConfig
    ///   {
    ///       OpenAnim  = UIAnimationType.SlideFromBottom,
    ///       CloseAnim = UIAnimationType.Fade,
    ///       Duration  = 0.3f
    ///   }
    ///
    /// 在 UIBase 子类中重写 AnimConfig 属性返回期望配置：
    ///   protected override UIAnimationConfig AnimConfig
    ///       => UIAnimationConfig.ScalePop();
    /// </summary>
    public class UIAnimationConfig
    {
        /// <summary>打开动画类型</summary>
        public UIAnimationType OpenAnim  = UIAnimationType.Fade;

        /// <summary>关闭动画类型</summary>
        public UIAnimationType CloseAnim = UIAnimationType.Fade;

        /// <summary>动画持续时间（秒）</summary>
        public float Duration = 0.25f;

        // ── 静态预设 ─────────────────────────────────────────────────────────

        /// <summary>无动画（不占帧时间，直接显示/隐藏）</summary>
        public static readonly UIAnimationConfig None = new UIAnimationConfig
        {
            OpenAnim  = UIAnimationType.None,
            CloseAnim = UIAnimationType.None,
        };

        /// <summary>淡入淡出（通用，默认）</summary>
        public static UIAnimationConfig Fade(float duration = 0.25f) => new UIAnimationConfig
        {
            OpenAnim  = UIAnimationType.Fade,
            CloseAnim = UIAnimationType.Fade,
            Duration  = duration,
        };

        /// <summary>缩放弹出 + 淡入（适合对话框、弹窗）</summary>
        public static UIAnimationConfig ScalePop(float duration = 0.3f) => new UIAnimationConfig
        {
            OpenAnim  = UIAnimationType.ScalePop,
            CloseAnim = UIAnimationType.ScalePop,
            Duration  = duration,
        };

        /// <summary>从底部滑入 + 淡入（适合底部面板、背包、设置）</summary>
        public static UIAnimationConfig SlideUp(float duration = 0.3f) => new UIAnimationConfig
        {
            OpenAnim  = UIAnimationType.SlideFromBottom,
            CloseAnim = UIAnimationType.SlideFromBottom,
            Duration  = duration,
        };

        /// <summary>从右侧滑入（适合二级页面）</summary>
        public static UIAnimationConfig SlideFromRight(float duration = 0.3f) => new UIAnimationConfig
        {
            OpenAnim  = UIAnimationType.SlideFromRight,
            CloseAnim = UIAnimationType.SlideFromRight,
            Duration  = duration,
        };
    }
}
