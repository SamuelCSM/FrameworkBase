using System;
using System.Collections.Generic;

namespace Framework
{
    /// <summary>
    /// 引导剧本：一条引导的有序步骤序列（构造即校验、构造后不可变）。
    /// 框架只关心步骤的推进与断点，每步做什么（高亮谁、说什么话）由业务在
    /// <see cref="GuideFlow.StepEntered"/> 回调里按步骤 id 自行编排。
    /// </summary>
    public sealed class GuideScript
    {
        /// <param name="id">引导唯一 id（断点存档的 key）。</param>
        /// <param name="steps">有序步骤 id：非空、不含空项、不重复。</param>
        public GuideScript(string id, params string[] steps)
        {
            if (string.IsNullOrWhiteSpace(id))
                throw new ArgumentException("引导 id 不能为空。", nameof(id));
            if (steps == null || steps.Length == 0)
                throw new ArgumentException($"引导 '{id}' 必须至少有一个步骤。", nameof(steps));

            var seen = new HashSet<string>(StringComparer.Ordinal);
            for (int i = 0; i < steps.Length; i++)
            {
                if (string.IsNullOrWhiteSpace(steps[i]))
                    throw new ArgumentException($"引导 '{id}' 第 {i + 1} 个步骤 id 为空。", nameof(steps));
                if (!seen.Add(steps[i]))
                    throw new ArgumentException($"引导 '{id}' 步骤 id '{steps[i]}' 重复。", nameof(steps));
            }

            Id = id;
            Steps = Array.AsReadOnly((string[])steps.Clone());
        }

        /// <summary>引导唯一 id。</summary>
        public string Id { get; }

        /// <summary>有序步骤 id。</summary>
        public IReadOnlyList<string> Steps { get; }
    }
}
