using System;
using System.Collections.Generic;
using Framework.Core;
using UnityEngine;

namespace Framework.Experiment
{
    /// <summary>实验定义来源抽象（默认读 RemoteConfig；测试可注入假源）。</summary>
    public interface IExperimentConfigSource
    {
        /// <summary>取指定 key 的实验定义；不存在返回 false。</summary>
        bool TryGet(string experimentKey, out ExperimentDefinition def);
    }

    /// <summary>
    /// 默认来源：读 <c>GameEntry.RemoteConfig</c> 的 "experiments" 键（一段 JSON 清单），
    /// 解析并缓存；远程值变化（重新 Fetch）时自动重解析。运营在后台改 experiments 即可
    /// 调整分组 / 放量 / 开关实验，无需发版。
    /// </summary>
    public sealed class RemoteConfigExperimentSource : IExperimentConfigSource
    {
        /// <summary>承载实验清单的 RemoteConfig 键。</summary>
        public const string RemoteKey = "experiments";

        private string _cachedRaw;
        private Dictionary<string, ExperimentDefinition> _cache;

        /// <inheritdoc />
        public bool TryGet(string experimentKey, out ExperimentDefinition def)
        {
            def = null;
            if (string.IsNullOrEmpty(experimentKey))
                return false;

            var rc = GameEntry.RemoteConfig;
            if (rc == null)
                return false;

            string raw = rc.GetString(RemoteKey, string.Empty);
            if (string.IsNullOrEmpty(raw))
                return false;

            if (!string.Equals(raw, _cachedRaw, StringComparison.Ordinal))
            {
                _cache = Parse(raw);
                _cachedRaw = raw;
            }

            return _cache != null && _cache.TryGetValue(experimentKey, out def);
        }

        private static Dictionary<string, ExperimentDefinition> Parse(string json)
        {
            try
            {
                var list = JsonUtility.FromJson<ExperimentConfigList>(json);
                var map = new Dictionary<string, ExperimentDefinition>();
                if (list?.experiments != null)
                {
                    foreach (ExperimentDefinition e in list.experiments)
                        if (e != null && !string.IsNullOrEmpty(e.key))
                            map[e.key] = e;
                }
                return map;
            }
            catch (Exception ex)
            {
                GameLog.Warning($"[Experiment] 解析 experiments 远程配置失败：{ex.Message}");
                return null;
            }
        }
    }
}
