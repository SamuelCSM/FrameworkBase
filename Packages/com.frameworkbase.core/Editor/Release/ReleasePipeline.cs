using System;
using System.Collections.Generic;
using System.Diagnostics;

namespace Framework.Editor.Release
{
    /// <summary>
    /// 发布步骤（阶段三：流水线编排器）。
    /// 步骤实现应保持幂等可重跑；失败以异常表达，由 <see cref="ReleasePipeline.Run"/> 统一截获并中断流水线。
    /// </summary>
    public interface IReleaseStep
    {
        /// <summary>步骤名（报告 / 日志用，稳定标识）。</summary>
        string Name { get; }

        /// <summary>这一步做什么（给发布人看的一句话，Release Center / 报告展示用）。</summary>
        string Description { get; }

        /// <summary>执行步骤；失败抛异常（异常消息面向发布人，说明失败怎么处理）。</summary>
        void Execute(ReleaseContext context);
    }

    /// <summary>单个步骤的执行结果。</summary>
    [Serializable]
    public class ReleaseStepResult
    {
        public string Name;
        public bool Success;
        public bool Skipped;
        public long DurationMs;
        public string Error;
    }

    /// <summary>整条流水线的执行结果（发布报告的骨架）。</summary>
    [Serializable]
    public class ReleasePipelineResult
    {
        public bool Success;
        public string FailedStep;
        public string Error;
        public List<ReleaseStepResult> Steps = new List<ReleaseStepResult>();
    }

    /// <summary>
    /// 发布流水线：顺序执行步骤、失败中断、逐步计时并汇总结果。
    /// UI、CI、命令行入口都应组装步骤列表调用本类，不再各自复制流程逻辑。
    /// </summary>
    public static class ReleasePipeline
    {
        /// <summary>
        /// 顺序执行步骤列表。任一步骤抛异常即中断，后续步骤不执行；
        /// 无论成败都返回完整结果（已执行步骤的耗时与错误全部在内）。
        /// </summary>
        public static ReleasePipelineResult Run(IReadOnlyList<IReleaseStep> steps, ReleaseContext context)
        {
            if (steps == null) throw new ArgumentNullException(nameof(steps));
            if (context == null) throw new ArgumentNullException(nameof(context));

            var result = new ReleasePipelineResult { Success = true };

            for (int i = 0; i < steps.Count; i++)
            {
                IReleaseStep step = steps[i];
                var stepResult = new ReleaseStepResult { Name = step.Name };
                result.Steps.Add(stepResult);

                context.Log?.Invoke($"[{i + 1}/{steps.Count}] {step.Name}：{step.Description}");

                var watch = Stopwatch.StartNew();
                try
                {
                    step.Execute(context);
                    stepResult.Success = true;
                }
                catch (Exception ex)
                {
                    stepResult.Success = false;
                    stepResult.Error = ex.Message;
                    result.Success = false;
                    result.FailedStep = step.Name;
                    result.Error = ex.Message;
                    context.Log?.Invoke($"[ERROR] 步骤 {step.Name} 失败：{ex.Message}");
                    return result;
                }
                finally
                {
                    watch.Stop();
                    stepResult.DurationMs = watch.ElapsedMilliseconds;
                }
            }

            return result;
        }
    }
}
