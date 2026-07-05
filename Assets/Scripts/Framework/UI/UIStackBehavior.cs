namespace Framework
{
    /// <summary>
    /// 窗口在导航栈中的行为。
    /// 注册 UI 时通过 <see cref="UIRegisterInfo.StackBehavior"/> 指定。
    /// </summary>
    public enum UIStackBehavior
    {
        /// <summary>
        /// 正常入栈（默认）。
        /// 窗口参与导航栈，GoBackAsync 时会按 LIFO 顺序返回。
        /// </summary>
        PushToStack,

        /// <summary>
        /// 不入栈。
        /// 适用于 Toast、HUD Overlay、多实例弹窗等不参与导航返回的 UI。
        /// </summary>
        NoStack,

        /// <summary>
        /// 替换栈顶。
        /// 打开时如果栈顶有其他窗口，将其移除并用自己替代。
        /// 适用于同级页面切换（如主界面 Tab 页之间互切）。
        /// </summary>
        ReplaceTop,
    }
}
