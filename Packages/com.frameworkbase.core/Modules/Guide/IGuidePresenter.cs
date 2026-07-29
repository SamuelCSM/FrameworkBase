using System;

namespace Framework
{
    /// <summary>
    /// 引导表现缝（L2，ADR-008 补遗）：把「聚焦某 UI Target / 清除遮罩」抽象为可替换实现。
    /// 框架自带矩形挖孔基线 <see cref="GuidePresentationService"/>；L3 可注入 Shader、原对象提层、
    /// 视觉副本等替换实现（构造时经 <see cref="GuideModule"/> 注入）。
    /// <para>
    /// 契约边界钉死在<b>表现</b>：presenter 不认识 Step/Runner——步骤推进仍由 CompleteTrigger 驱动，
    /// 表现层不得反向推进引导（守 ADR-008 第 5 点编排/表现分离）。将来 payload 携带高亮模式/形状提示时，
    /// 由实现自行解释，且<b>对不认识的模式必须安全降级</b>（内置实现降级为矩形并告警一次），不得抛错把引导卡死。
    /// </para>
    /// </summary>
    public interface IGuidePresenter : IDisposable
    {
        /// <summary>
        /// 把遮罩聚焦到指定 UI Target 并挖孔（孔内点击穿透给真实控件，孔外压暗拦截）。
        /// </summary>
        /// <param name="targetId">语义 TargetId。</param>
        /// <param name="scope">目标解析作用域（同一 TargetId 存在于多窗口实例时据此精确定位）；可为 null。</param>
        /// <param name="padding">挖孔相对目标包围盒的外扩像素（仅放大视觉孔，不放大穿透区）。</param>
        /// <param name="dimAlpha">压暗强度，0~1。</param>
        /// <returns>目标解析成功并已聚焦返回 true；TargetId 不存在或 Scope 不匹配返回 false（动作据此判失败）。</returns>
        bool TryFocus(int targetId, object scope, float padding, float dimAlpha);

        /// <summary>清除遮罩表现：移除挖孔遮罩，回到无引导表现态。</summary>
        void Clear();

        /// <summary>孔外压暗区被点击时触发（供业务叠加「请点击高亮处」反馈，或作「点任意处继续」的 Trigger 源）。</summary>
        event Action DimClicked;
    }
}
