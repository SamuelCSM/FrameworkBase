using Framework.Save;

namespace Framework
{
    /// <summary>配置引导的强类型断点快照。</summary>
    public readonly struct GuideProgress
    {
        public GuideProgress(int currentStepId, bool isCompleted)
        {
            CurrentStepId = currentStepId;
            IsCompleted = isCompleted;
        }

        public int CurrentStepId { get; }
        public bool IsCompleted { get; }
    }

    /// <summary>
    /// int GuideId + int StepId 的进度存储。步骤断点存稳定 StepId，不存 Order/数组下标，
    /// 因而线上插入或重排步骤不会把玩家续到错误位置。
    /// </summary>
    public interface IGuideRuntimeProgressStore
    {
        GuideProgress Get(int guideId);
        void SetCurrentStep(int guideId, int stepId);
        void MarkCompleted(int guideId);
        void Clear(int guideId);
    }

    /// <summary>
    /// 默认设备级实现。正式业务若要求按账号隔离/云同步，应在业务组合根注入账号存档实现。
    /// </summary>
    public sealed class PrefsGuideRuntimeProgressStore : IGuideRuntimeProgressStore
    {
        private static string StepKey(int guideId) => "guide_v2_step_" + guideId;
        private static string DoneKey(int guideId) => "guide_v2_done_" + guideId;

        public GuideProgress Get(int guideId)
            => new GuideProgress(
                SaveManager.Instance.GetPref(StepKey(guideId), 0),
                SaveManager.Instance.GetPref(DoneKey(guideId), false));

        public void SetCurrentStep(int guideId, int stepId)
            => SaveManager.Instance.SetPref(StepKey(guideId), stepId);

        public void MarkCompleted(int guideId)
            => SaveManager.Instance.SetPref(DoneKey(guideId), true);

        public void Clear(int guideId)
        {
            SaveManager.Instance.DeletePref(StepKey(guideId));
            SaveManager.Instance.DeletePref(DoneKey(guideId));
        }
    }
}
