using PrimeTween;

namespace Framework
{
    /// <summary>
    /// 通用补间（PrimeTween）框架引导层。
    /// <para>
    /// 定位：PrimeTween 是框架选定的<b>通用补间标准</b>（零 GC、结构体句柄、AOT/IL2CPP 安全、WebGL 可 await）。
    /// 它与 UniTask / HybridCLR 同为框架硬依赖，由工程 <c>manifest.json</c> 提供（npm scoped registry
    /// <c>com.kyrylokuzyk</c>）。框架<b>不</b>把 <c>PrimeTween.Tween</c> / <c>PrimeTween.Sequence</c> 再包一层门面——
    /// 那会牺牲其零分配的流式 API 且毫无收益；业务/热更程序集直接 <c>using PrimeTween;</c> 使用即可。框架只做
    /// 三件它该做的事：① 启动期一次性容量与默认缓动配置（本类）；② UniTask + CancellationToken 桥接
    /// （<see cref="TweenAsyncExtensions"/>）；③ UI 过渡预设复用（<see cref="UIAnimator"/>）。详见 ADR-007。
    /// </para>
    /// </summary>
    public static class TweenBootstrap
    {
        /// <summary>默认预留补间容量：移动端典型峰值经验值（UI 过渡 + 少量场景/相机动画并发）。</summary>
        public const int DefaultCapacity = 200;

        private static bool _initialized;

        /// <summary>
        /// 启动期一次性引导（由组合根 <c>GameEntry.Awake</c> 在任何补间发生前调用）。幂等：重复调用忽略。
        /// <para>
        /// <paramref name="capacity"/> 预分配补间池，杜绝运行期扩容 GC——取值以真机 PrimeTweenManager Inspector
        /// 的「Max alive tweens」加安全裕量为准，此处给移动端保守默认。<paramref name="defaultEase"/> 统一未显式
        /// 指定缓动时的手感（框架取 OutCubic：快进慢出、通用稳定）。
        /// </para>
        /// </summary>
        /// <param name="capacity">预留补间容量；<=0 时按 <see cref="DefaultCapacity"/>。</param>
        public static void Initialize(int capacity = DefaultCapacity)
        {
            if (_initialized)
                return;
            _initialized = true;

            int cap = capacity > 0 ? capacity : DefaultCapacity;
            // 预分配补间与序列池：达到该并发数前运行期零分配。
            PrimeTweenConfig.SetTweensCapacity(cap);
            // 未显式指定缓动时的框架默认手感。
            PrimeTweenConfig.defaultEase = Ease.OutCubic;
            GameLog.Log($"[TweenBootstrap] PrimeTween 已就绪：容量={cap}，默认缓动=OutCubic");
        }
    }
}
