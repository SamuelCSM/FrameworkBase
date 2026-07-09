namespace Framework.Experiment
{
    /// <summary>
    /// 纯实验分配逻辑（无 Unity 依赖，可单测）：稳定哈希 + 权重分桶。
    ///
    /// <para>同一 <c>(unit, key, salt)</c> 永远落同一变体（跨会话 / 跨设备一致，复用
    /// <see cref="StableHash"/> FNV-1a，非 <c>string.GetHashCode</c>）——这是 A/B 归因成立的前提：
    /// 玩家分组必须稳定，否则曝光与后续指标对不上。</para>
    /// </summary>
    public static class ExperimentAssigner
    {
        /// <summary>对照组 / 兜底变体名（未命中 / 未启用 / 无有效权重时返回）。</summary>
        public const string Control = "control";

        /// <summary>
        /// 把分配单元（用户 ID / 设备 ID）按定义分到某变体。
        /// 定义无效或总权重为 0 时返回 <see cref="Control"/>。
        /// </summary>
        public static string Assign(string unit, ExperimentDefinition def)
        {
            if (def == null || !def.IsValid)
                return Control;

            int total = 0;
            foreach (ExperimentVariant v in def.variants)
                if (v != null && v.weight > 0)
                    total += v.weight;

            if (total <= 0)
                return Control;

            string seed = (unit ?? string.Empty) + ":" + def.key + ":" + (def.salt ?? string.Empty);
            int bucket = (int)(StableHash.Fnv1a32(seed) % (uint)total);

            int cumulative = 0;
            foreach (ExperimentVariant v in def.variants)
            {
                if (v == null || v.weight <= 0)
                    continue;

                cumulative += v.weight;
                if (bucket < cumulative)
                    return string.IsNullOrEmpty(v.name) ? Control : v.name;
            }

            return Control; // 理论不可达（bucket < total 必落某段）
        }
    }
}
