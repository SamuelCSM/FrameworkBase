using System;

namespace Framework
{
    /// <summary>
    /// 引导模块（L2）自有的编排能力号段与强类型 Payload（ADR-008）。
    /// <para>
    /// 这些 TypeId 的 Executor 实现在 <see cref="GuideModule"/>，语义完全属于引导业务，故与 Payload 类型
    /// 一并放在中间层——L1 的 <see cref="BuiltinOrchestrationTypeIds"/> 只保留 UI/时间等中立能力。
    /// R6 门禁拦得住程序集引用方向，拦不住"L1 号段表里出现业务概念"，靠归属划分自律。
    /// </para>
    /// 号段：落在 Action 千位 3 的<b>模块段</b>（x100-x199，见 <see cref="BuiltinOrchestrationTypeIds"/> 规约）；
    /// 引导占 3100-3119。改这些常量须同步改 <c>Guide.xlsx</c> 的 <c>action_ref.TypeId</c> 并重导 config.db。
    /// </summary>
    public static class GuideOrchestrationTypeIds
    {
        /// <summary>Action：把挖孔遮罩聚焦到指定 UI Target。</summary>
        public const int FocusTargetAction = 3100;

        /// <summary>Action：清除挖孔遮罩。</summary>
        public const int ClearFocusAction = 3101;
    }

    /// <summary>引导挖孔 Action 参数：聚焦到哪个 UI Target，以及挖孔留白与压暗强度。</summary>
    [Serializable]
    public sealed class GuideFocusTargetActionPayload
    {
        public int TargetId;
        public float Padding = 8f;
        public float DimAlpha = 0.6f;
    }

    /// <summary>清除挖孔遮罩 Action 参数：无参，仍建行以保证 PayloadId 引用完整可校验。</summary>
    [Serializable]
    public sealed class GuideClearFocusActionPayload
    {
    }
}
