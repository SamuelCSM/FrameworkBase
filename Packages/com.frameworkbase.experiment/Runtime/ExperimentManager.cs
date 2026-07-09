using System;
using System.Collections.Generic;

namespace Framework.Experiment
{
    /// <summary>
    /// A/B 实验管理器：解析变体（稳定分组）+ 首次使用时打曝光埋点 + QA 覆盖。
    ///
    /// <para>数据流：实验定义来自 <see cref="IExperimentConfigSource"/>（默认 RemoteConfig），
    /// 分配走纯 <see cref="ExperimentAssigner"/>（稳定哈希 + 权重），曝光走 <see cref="IExposureSink"/>
    /// （默认 Analytics）。两个依赖都可注入 → 可脱离 Unity 单测。</para>
    ///
    /// <para>用法：业务在拿到分配单元后 <see cref="SetUnitId"/>（登录前用设备 ID、登录后可切用户 ID），
    /// 之后 <see cref="GetVariant"/> / <see cref="IsInVariant"/> 取分组——首次取某实验即自动打曝光。</para>
    /// </summary>
    public sealed class ExperimentManager
    {
        private readonly IExperimentConfigSource _source;
        private readonly IExposureSink _sink;
        private readonly Dictionary<string, string> _overrides = new Dictionary<string, string>();
        private readonly HashSet<string> _exposed = new HashSet<string>();
        private string _unitId = string.Empty;

        /// <summary>用默认来源（RemoteConfig）与出口（Analytics）构造。</summary>
        public ExperimentManager()
            : this(new RemoteConfigExperimentSource(), new AnalyticsExposureSink())
        {
        }

        /// <summary>用指定来源 / 出口构造（测试或自定义接入）。传 null 回落各自默认。</summary>
        public ExperimentManager(IExperimentConfigSource source, IExposureSink sink)
        {
            _source = source ?? new RemoteConfigExperimentSource();
            _sink = sink ?? new AnalyticsExposureSink();
        }

        /// <summary>当前分配单元 ID。</summary>
        public string UnitId => _unitId;

        /// <summary>
        /// 设置分配单元 ID（用户 ID / 设备 ID）。这是分组的稳定锚点；切换会改变后续分配结果，
        /// 故一旦某实验已曝光就不建议再切单元（会导致同一玩家跨单元漂移）。
        /// </summary>
        public void SetUnitId(string unitId) => _unitId = unitId ?? string.Empty;

        /// <summary>强制某实验落指定变体（QA / 本地联调）。传空变体等于清除，见 <see cref="ClearOverride"/>。</summary>
        public void SetOverride(string experimentKey, string variant)
        {
            if (string.IsNullOrEmpty(experimentKey))
                return;
            _overrides[experimentKey] = variant ?? ExperimentAssigner.Control;
        }

        /// <summary>清除某实验的强制变体。</summary>
        public void ClearOverride(string experimentKey)
        {
            if (!string.IsNullOrEmpty(experimentKey))
                _overrides.Remove(experimentKey);
        }

        /// <summary>
        /// 取实验变体，并在<b>本会话首次</b>取该实验时打一条曝光埋点（去重，避免每次判定都刷曝光）。
        /// </summary>
        /// <param name="experimentKey">实验 key。</param>
        /// <param name="trackExposure">是否触发曝光（默认 true）。仅想预览分组不打点时传 false。</param>
        public string GetVariant(string experimentKey, bool trackExposure = true)
        {
            string variant = ResolveVariant(experimentKey);

            if (trackExposure && !string.IsNullOrEmpty(experimentKey) && _exposed.Add(experimentKey))
            {
                try
                {
                    _sink.TrackExposure(experimentKey, variant);
                }
                catch (Exception ex)
                {
                    // 曝光埋点失败不得影响业务取分组。
                    GameLog.Warning($"[Experiment] 曝光埋点失败 exp={experimentKey}: {ex.Message}");
                }
            }

            return variant;
        }

        /// <summary>是否命中某变体（会触发曝光，语义上「判定即使用即曝光」）。</summary>
        public bool IsInVariant(string experimentKey, string variant)
            => string.Equals(GetVariant(experimentKey), variant, StringComparison.Ordinal);

        /// <summary>只解析分组、<b>不</b>触发曝光（用于调试面板 / 预热，不污染曝光数据）。</summary>
        public string PeekVariant(string experimentKey)
            => GetVariant(experimentKey, trackExposure: false);

        private string ResolveVariant(string experimentKey)
        {
            if (string.IsNullOrEmpty(experimentKey))
                return ExperimentAssigner.Control;

            if (_overrides.TryGetValue(experimentKey, out string forced))
                return forced;

            if (_source.TryGet(experimentKey, out ExperimentDefinition def))
                return ExperimentAssigner.Assign(_unitId, def);

            return ExperimentAssigner.Control;
        }
    }

    /// <summary>
    /// 实验能力的全局访问点（静态门面）。业务经 <see cref="Instance"/> 使用；
    /// 需自定义来源 / 出口或测试隔离时 <see cref="SetInstance"/> 注入。
    /// </summary>
    public static class Experiments
    {
        private static ExperimentManager _instance;

        /// <summary>默认单例（惰性创建，走 RemoteConfig + Analytics）。</summary>
        public static ExperimentManager Instance => _instance ??= new ExperimentManager();

        /// <summary>替换全局实例（自定义接入 / 测试）。传 null 复位为下次惰性重建。</summary>
        public static void SetInstance(ExperimentManager instance) => _instance = instance;
    }
}
