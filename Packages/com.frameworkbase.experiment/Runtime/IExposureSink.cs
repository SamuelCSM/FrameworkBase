using System.Collections.Generic;
using Framework.Core;

namespace Framework.Experiment
{
    /// <summary>曝光埋点出口抽象（默认打到 Analytics；测试可注入假 sink 断言）。</summary>
    public interface IExposureSink
    {
        /// <summary>上报一次「玩家被曝光到某实验的某变体」。</summary>
        void TrackExposure(string experimentKey, string variant);
    }

    /// <summary>
    /// 默认出口：打一条 <c>experiment_exposure</c> 埋点事件（experiment + variant 维度）到
    /// <c>GameEntry.Analytics</c>。有了曝光事件，后续留存 / 付费等指标才能按变体归因做显著性分析。
    /// </summary>
    public sealed class AnalyticsExposureSink : IExposureSink
    {
        /// <summary>曝光事件名。</summary>
        public const string EventName = "experiment_exposure";

        /// <inheritdoc />
        public void TrackExposure(string experimentKey, string variant)
        {
            GameEntry.Analytics?.Track(EventName, new Dictionary<string, object>
            {
                { "experiment", experimentKey },
                { "variant", variant },
            });
        }
    }
}
