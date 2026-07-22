using Framework.Save;

namespace Framework
{
    /// <summary>
    /// 引导断点存储抽象。<see cref="GuideFlow"/> 每步推进即写档（崩溃 / 杀进程重进从断点续）。
    /// 默认实现 <see cref="PrefsGuideProgressStore"/> 落 PlayerPrefs（设备级）；
    /// 引导进度需要跟账号走（换号重看 / 云同步）时，业务用账号存档自行实现本接口注入。
    /// <para>
    /// 断点存的是步骤 <b>id</b>（而非序号）：线上剧本插入 / 重排步骤时，<see cref="GuideFlow"/>
    /// 按 id 在当前剧本里重新定位，玩家不会续到错位的步骤上；断点步骤被删 / 改名（id 找不到）
    /// 则从头重播。存序号的旧模型无法区分这两种情况，插一步就会让所有后续断点错位一位。
    /// </para>
    /// </summary>
    public interface IGuideProgressStore
    {
        /// <summary>读断点步骤 id；未开始返回 null / 空串。</summary>
        string GetStepId(string guideId);

        /// <summary>写断点步骤 id（当前进入、尚未完成的那一步）。</summary>
        void SetStepId(string guideId, string stepId);

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

        public string GetStepId(string guideId)
            => SaveManager.Instance.GetPref(StepKey(guideId), string.Empty);

        public void SetStepId(string guideId, string stepId)
            => SaveManager.Instance.SetPref(StepKey(guideId), stepId ?? string.Empty);

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
