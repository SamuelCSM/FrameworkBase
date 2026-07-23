using System;

namespace Framework
{
    /// <summary>一条引导完成后的重复策略。</summary>
    public enum GuideRepeatMode
    {
        Once = 0,
        Always = 1,
    }

    /// <summary>步骤动作执行阶段。</summary>
    public enum GuideActionPhase
    {
        Enter = 0,
        Exit = 1,
        Cancel = 2,
    }

    /// <summary>单个动作失败时的处理方式。</summary>
    public enum GuideActionFailurePolicy
    {
        AbortGuide = 0,
        Continue = 1,
    }

    /// <summary>配置驱动的引导定义。StartTrigger 负责“何时尝试”，StartRule 负责“此刻能否开始”。</summary>
    [Serializable]
    public sealed class GuideDefinition
    {
        public int Id;
        public string Key;
        public int StartRuleId;
        public int StartTriggerId;
        public int Priority;
        public GuideRepeatMode RepeatMode;
        public string Description;
    }

    /// <summary>引导内稳定步骤；StepId 是断点身份，Order 只控制当前版本的展示顺序。</summary>
    [Serializable]
    public sealed class GuideStepDefinition
    {
        public int GuideId;
        public int StepId;
        public int Order;
        public int CompleteTriggerId;
        /// <summary>本步等待完成信号的超时毫秒；&lt;=0 表示沿用运行器级 <see cref="GuideRunner.StepTimeout"/>。</summary>
        public int TimeoutMs;
        public string Key;
        public string Description;
    }

    /// <summary>步骤某阶段要执行的一条通用 Action。</summary>
    [Serializable]
    public sealed class GuideStepActionDefinition
    {
        public int GuideId;
        public int StepId;
        public GuideActionPhase Phase;
        public int ActionId;
        public int Order;
        public GuideActionFailurePolicy FailurePolicy;
        public string Description;
    }

    /// <summary>初始化后由 <see cref="GuideRunner"/> 冻结使用的全局引导目录。</summary>
    [Serializable]
    public sealed class GuideCatalog
    {
        public int SchemaVersion = 1;
        public GuideDefinition[] Guides = Array.Empty<GuideDefinition>();
        public GuideStepDefinition[] Steps = Array.Empty<GuideStepDefinition>();
        public GuideStepActionDefinition[] StepActions = Array.Empty<GuideStepActionDefinition>();
    }
}
