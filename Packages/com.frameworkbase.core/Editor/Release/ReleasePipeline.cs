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

    /// <summary>
    /// 带失败补偿的发布步骤（Saga 语义）：本步骤成功执行后、流水线后续步骤失败时，
    /// <see cref="Compensate"/> 会被逆序调用，用于恢复被本步骤改变的共享状态
    /// （如 Addressables Profile 切换、临时产物清理）。补偿应尽力而为，异常只记日志不再中断。
    /// </summary>
    public interface ICompensableStep : IReleaseStep
    {
        /// <summary>撤销本步骤对共享状态的改动（仅在本步骤已成功、后续步骤失败时调用）。</summary>
        void Compensate(ReleaseContext context);
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
        /// <summary>流水线失败后本步骤的补偿是否被执行（仅 ICompensableStep 且已成功的步骤）。</summary>
        public bool Compensated;
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
        /// 顺序执行步骤列表。任一步骤抛异常即中断，后续步骤不执行，并对已成功的
        /// <see cref="ICompensableStep"/> <b>逆序</b>执行补偿（补偿异常只记日志）；
        /// 无论成败都返回完整结果（已执行步骤的耗时、错误与补偿情况全部在内）。
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

                    CompensateExecutedSteps(steps, result, i, context);
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

        /// <summary>对失败步骤之前已成功的可补偿步骤逆序执行补偿（Saga 语义）。</summary>
        private static void CompensateExecutedSteps(
            IReadOnlyList<IReleaseStep> steps, ReleasePipelineResult result, int failedIndex, ReleaseContext context)
        {
            for (int i = failedIndex - 1; i >= 0; i--)
            {
                if (steps[i] is not ICompensableStep compensable)
                    continue;

                try
                {
                    context.Log?.Invoke($"[补偿] {steps[i].Name}");
                    compensable.Compensate(context);
                    result.Steps[i].Compensated = true;
                }
                catch (Exception ex)
                {
                    // 补偿尽力而为：一个补偿失败不阻止其余补偿执行。
                    context.Log?.Invoke($"[WARN] 步骤 {steps[i].Name} 补偿失败：{ex.Message}");
                }
            }
        }
    }
}
