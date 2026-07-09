using System;

namespace Framework.Experiment
{
    /// <summary>
    /// 一个实验变体。<paramref name="weight"/> 为相对权重（不必凑成 100，按各变体权重之和归一）。
    /// </summary>
    [Serializable]
    public sealed class ExperimentVariant
    {
        /// <summary>变体名（业务按此判定，如 "control" / "v1" / "big_button"）。</summary>
        public string name;

        /// <summary>相对权重（>0 才参与分配）。</summary>
        public int weight;
    }

    /// <summary>
    /// 实验定义。字段名小写以匹配远程配置 JSON 惯例（由运营在后台维护、经 RemoteConfig 下发）。
    /// </summary>
    [Serializable]
    public sealed class ExperimentDefinition
    {
        /// <summary>实验 key（业务与埋点按此标识）。</summary>
        public string key;

        /// <summary>是否启用；false 时一律回落对照组。</summary>
        public bool enabled = true;

        /// <summary>分桶盐值（改盐即重洗分组，用于同一实验重开一轮）。</summary>
        public string salt = "";

        /// <summary>变体列表。</summary>
        public ExperimentVariant[] variants;

        /// <summary>定义是否可用于分配（启用且至少一个变体）。</summary>
        public bool IsValid => enabled && variants != null && variants.Length > 0;
    }

    /// <summary>
    /// 远程配置里 "experiments" 键承载的实验清单（JSON：<c>{"experiments":[{...},{...}]}</c>）。
    /// </summary>
    [Serializable]
    public sealed class ExperimentConfigList
    {
        /// <summary>实验定义数组。</summary>
        public ExperimentDefinition[] experiments;
    }
}
