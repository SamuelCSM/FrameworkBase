using UnityEngine;

namespace Framework
{
    /// <summary>
    /// 场景相机 Rig 接口。
    /// </summary>
    /// <remarks>
    /// 场景相机 Rig 是场景相机行为的唯一拥有者，负责把场景相机配置应用到实际 Camera。
    /// 业务系统可以读取相机用于射线、坐标转换或 UI 跟随，但不应直接修改相机参数。
    /// </remarks>
    public interface ISceneCameraRig
    {
        /// <summary>
        /// 当前场景主相机。
        /// </summary>
        Camera MainCamera { get; }

        /// <summary>
        /// Rig 当前是否已经成功应用过配置。
        /// </summary>
        bool IsApplied { get; }

        /// <summary>
        /// 校验 Rig 的 Inspector 引用和配置是否完整。
        /// </summary>
        /// <param name="error">校验失败时输出错误描述。</param>
        /// <returns>配置完整返回 true。</returns>
        bool TryValidate(out string error);

        /// <summary>
        /// 应用当前 Rig 配置到相机。
        /// </summary>
        void Apply();

        /// <summary>
        /// 标记相机配置已变脏，等待调用方重新应用。
        /// </summary>
        void MarkDirty();
    }
}
