using Framework.Save;

namespace Framework
{
    /// <summary>
    /// 引导断点存储抽象。<see cref="GuideFlow"/> 每步推进即写档（崩溃 / 杀进程重进从断点续）。
    /// 默认实现 <see cref="PrefsGuideProgressStore"/> 落 PlayerPrefs（设备级）；
    /// 引导进度需要跟账号走（换号重看 / 云同步）时，业务用账号存档自行实现本接口注入。
    /// </summary>
    public interface IGuideProgressStore
    {
        /// <summary>读断点步骤序号；未开始返回 0。</summary>
        int GetStepIndex(string guideId);

        /// <summary>写断点步骤序号。</summary>
        void SetStepIndex(string guideId, int index);

        /// <summary>该引导是否已整条完成。</summary>
        bool IsCompleted(string guideId);

        /// <summary>标记整条完成（含跳过）。</summary>
        void MarkCompleted(string guideId);

        /// <summary>清除该引导全部进度（调试 / 重玩）。</summary>
        void Clear(string guideId);
    }

    /// <summary>
    /// 默认断点存储：SaveManager 偏好键值（PlayerPrefs，设备级、不随账号隔离——
    /// 新手引导通常一台设备看一次即可；需按账号请自行实现 <see cref="IGuideProgressStore"/>）。
    /// </summary>
    public sealed class PrefsGuideProgressStore : IGuideProgressStore
    {
        private static string StepKey(string guideId) => "guide_step_" + guideId;
        private static string DoneKey(string guideId) => "guide_done_" + guideId;

        public int GetStepIndex(string guideId)
            => SaveManager.Instance.GetPref(StepKey(guideId), 0);

        public void SetStepIndex(string guideId, int index)
            => SaveManager.Instance.SetPref(StepKey(guideId), index);

        public bool IsCompleted(string guideId)
            => SaveManager.Instance.GetPref(DoneKey(guideId), false);

        public void MarkCompleted(string guideId)
            => SaveManager.Instance.SetPref(DoneKey(guideId), true);

        public void Clear(string guideId)
        {
            SaveManager.Instance.DeletePref(StepKey(guideId));
            SaveManager.Instance.DeletePref(DoneKey(guideId));
        }
    }
}
